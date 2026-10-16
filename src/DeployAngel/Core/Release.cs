using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeployAngel.Core;

/// <summary>Which release this process runs.</summary>
internal sealed class Release
{
    public Release(string? version, string? commit, string source, bool pending = false)
    {
        Version = version;
        Commit = commit;
        Source = source;
        Pending = pending;
    }

    public string? Version { get; }
    public string? Commit { get; }
    public string Source { get; }

    // Unknown so far, with the code fingerprint still to try. It reads the
    // app's debug symbols, so the agent computes it before its first send
    // rather than at boot; until then the release is unknown.
    public bool Pending { get; set; }

    public bool Unknown => Version is null && Commit is null;

    public void Write(JsonOut json)
    {
        json.StartObject();
        json.Property("version", Version);
        json.Property("commit", Commit);
        json.Property("source", Source);
        json.EndObject();
    }

    public override string ToString() => $"Release(version={Version ?? "null"}, commit={Commit ?? "null"}, source={Source})";
}

/// <summary>
/// Resolves the release once at boot (the code fingerprint just before the
/// first send) so that telemetry can be attributed to a deployment.
/// </summary>
internal static class ReleaseResolver
{
    private static readonly Regex CommitFormat = new(@"\A[0-9a-f]{7,40}\z", RegexOptions.CultureInvariant);
    // The commit the .NET SDK appends to the informational version when it
    // builds in a git checkout ("1.2.0+3f2a9c..."), with Source Link.
    private static readonly Regex InformationalCommit = new(@"\+([0-9a-fA-F]{7,40})(?![0-9a-zA-Z])", RegexOptions.CultureInvariant);
    // Tags that move from build to build, so they can't identify a release.
    private static readonly string[] MovingTags = { "latest", "main", "master", "production", "prod", "staging", "stable", "release" };
    // Parent directories searched for .git, for an app that lives in a
    // subdirectory of its repository.
    private const int GitSearchDepth = 3;

    /// <summary>
    /// Order: explicit configuration, Heroku dyno metadata, the hosting
    /// platform's own variables (Kamal, Render, Fly.io, Railway, Coolify, and
    /// Dokku's GIT_REV), the commit the app was built from (its assembly's
    /// informational version), a REVISION file, a git checkout, then ECS
    /// container metadata. When none of them names the release, it's left
    /// pending for the code fingerprint.
    /// </summary>
    public static Release Resolve(DeployAngelOptions options, IReadOnlyDictionary<string, string?> env, string? root,
        string? informationalVersion, Func<string, string?>? http = null)
    {
        http ??= FetchMetadata;
        string? Env(string name) => env.TryGetValue(name, out var value) ? value : null;

        if (Present(options.ReleaseVersion) || Present(options.Revision))
            return Build(options.ReleaseVersion, options.Revision, "config");
        var herokuCommit = HerokuCommit(Env);
        if (Present(Env("HEROKU_RELEASE_VERSION")) || Present(herokuCommit))
            return Build(Env("HEROKU_RELEASE_VERSION"), herokuCommit, "heroku_dyno_metadata");
        if (Present(Env("KAMAL_VERSION"))) return Kamal(Env("KAMAL_VERSION")!);
        if (Present(Env("RENDER_GIT_COMMIT"))) return Build(null, Env("RENDER_GIT_COMMIT"), "render");
        var tag = FlyTag(Env("FLY_IMAGE_REF"));
        if (tag is not null) return Build(tag, null, "fly");
        if (Present(Env("RAILWAY_GIT_COMMIT_SHA")) || Present(Env("RAILWAY_DEPLOYMENT_ID"))) return Railway(Env);
        if (Present(Env("SOURCE_COMMIT")) && Present(Env("COOLIFY_CONTAINER_NAME") ?? Env("COOLIFY_RESOURCE_UUID")))
            return Build(null, Env("SOURCE_COMMIT"), "coolify");
        // Dokku sets GIT_REV, but the name is generic, so the source says so.
        if (Present(Env("GIT_REV"))) return Build(null, Env("GIT_REV"), "git_rev");
        var built = AssemblyCommit(informationalVersion);
        if (built is not null) return Build(null, built, "assembly_version");
        if (root is not null)
        {
            var revision = ReadFile(Path.Combine(root, "REVISION"), 100);
            if (revision is not null) return Build(null, revision, "revision_file");
            // Anything that isn't a commit (a SHA-256 repository's HEAD, a
            // malformed ref) falls through to the next source.
            var checkout = Build(null, GitHead(root), "git_head");
            if (checkout.Commit is not null) return checkout;
        }
        if (Present(Env("ECS_CONTAINER_METADATA_URI_V4")))
        {
            var release = Ecs(http(Env("ECS_CONTAINER_METADATA_URI_V4")!));
            if (release is not null) return release;
        }
        return new Release(null, null, "unknown", pending: true);
    }

    /// <summary>The commit in an informational version such as "1.2.0+3f2a9c1".</summary>
    public static string? AssemblyCommit(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion)) return null;
        var match = InformationalCommit.Match(informationalVersion);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    /// <summary>
    /// The commit HEAD names in a checkout, for servers deployed by `git
    /// pull`. Read from the repository's files, never by running git;
    /// anything unreadable or unexpected is null.
    /// </summary>
    public static string? GitHead(string root)
    {
        try
        {
            var gitDir = GitDir(root);
            if (gitDir is null) return null;
            // A worktree's own directory holds HEAD; its branches live in the
            // main repository, which commondir names.
            var commonDir = gitDir;
            var common = (ReadFile(Path.Combine(gitDir, "commondir"), 1000) ?? "").Trim();
            if (common.Length > 0) commonDir = Path.GetFullPath(Path.Combine(gitDir, common));
            var head = (ReadFile(Path.Combine(gitDir, "HEAD"), 200) ?? "").Trim();
            if (!head.StartsWith("ref:", StringComparison.Ordinal)) return head.Length > 0 ? head : null;
            var reference = head[4..].Trim();
            if (!reference.StartsWith("refs/", StringComparison.Ordinal) || reference.Contains("..", StringComparison.Ordinal))
                return null;
            foreach (var directory in new[] { gitDir, commonDir })
            {
                var commit = (ReadFile(Path.Combine(new[] { directory }.Concat(reference.Split('/')).ToArray()), 200) ?? "").Trim();
                if (commit.Length > 0) return commit;
            }
            return PackedRef(Path.Combine(commonDir, "packed-refs"), reference);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The release as a hash of the app's code, when nothing else names it:
    /// the hash of the file digests the agent already sends with its
    /// metadata. The same code gives the same fingerprint, so a redeploy of
    /// unchanged code is not a new release. Unknown when digests are off,
    /// empty, or truncated, since a partial hash could miss what changed.
    /// </summary>
    public static Release CodeFingerprint(FileManifest? manifest)
    {
        if (manifest is null || manifest.Truncated || manifest.Count == 0) return new Release(null, null, "unknown");
        var digest = manifest.Hash ?? "";
        if (!Regex.IsMatch(digest, @"\A[0-9a-f]{12}")) return new Release(null, null, "unknown");
        return Build($"code:{digest[..12]}", null, "code_fingerprint");
    }

    /// <summary>
    /// Kamal sets KAMAL_VERSION in every app container: the git commit by
    /// default, with "_uncommitted_&lt;random&gt;" added for a dirty working
    /// tree, or a version you declared. A plain commit is reported as the
    /// commit; anything else is the version, with the commit it starts with.
    /// </summary>
    public static Release Kamal(string value)
    {
        value = value.Trim();
        var plainCommit = CommitFormat.IsMatch(value.ToLowerInvariant());
        return Build(plainCommit ? null : value, value.Split('_', 2)[0], "kamal");
    }

    /// <summary>
    /// Railway sets the commit for deploys from GitHub. Others, such as
    /// `railway up`, only have a deployment ID, which identifies the release.
    /// </summary>
    private static Release Railway(Func<string, string?> env) =>
        Present(env("RAILWAY_GIT_COMMIT_SHA"))
            ? Build(null, env("RAILWAY_GIT_COMMIT_SHA"), "railway")
            : Build(env("RAILWAY_DEPLOYMENT_ID"), null, "railway");

    /// <summary>
    /// Fly.io tags each deploy's image ("registry.fly.io/shop:deployment-01H9…"),
    /// and the tag identifies the release.
    /// </summary>
    public static string? FlyTag(string? imageRef)
    {
        var name = (imageRef ?? "").Trim().Split('@', 2)[0].Split('/').Last();
        var parts = name.Split(':', 2);
        var tag = parts.Length == 2 ? RemovePrefix(parts[1], "deployment-") : "";
        return tag.Length > 0 ? tag : null;
    }

    /// <summary>
    /// The image tag identifies the release, and is the commit when it looks
    /// like one. A moving tag such as "latest" can't, so the image digest does.
    /// </summary>
    public static Release? Ecs(string? body)
    {
        try
        {
            using var document = JsonDocument.Parse(body ?? "");
            var data = document.RootElement;
            if (data.ValueKind != JsonValueKind.Object) return null;
            var image = data.TryGetProperty("Image", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString()! : "";
            var name = image.Split('@', 2)[0].Split('/').Last();
            var parts = name.Split(':', 2);
            var tag = parts.Length == 2 ? parts[1].Trim() : "";
            if (CommitFormat.IsMatch(tag.ToLowerInvariant())) return Build(null, tag, "ecs");
            if (tag.Length > 0 && !MovingTags.Contains(tag.ToLowerInvariant())) return Build(tag, null, "ecs");
            var imageId = data.TryGetProperty("ImageID", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()! : "";
            var digest = Regex.Match(imageId, @"\Asha256:([0-9a-fA-F]{12})");
            return digest.Success ? Build($"sha256:{digest.Groups[1].Value}", null, "ecs") : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// One request at boot to the container's own metadata endpoint, which is
    /// local, so the timeout is short. Null on any failure.
    /// </summary>
    public static string? FetchMetadata(string uri)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            using var response = client.Send(new HttpRequestMessage(HttpMethod.Get, uri));
            if (!response.IsSuccessStatusCode) return null;
            using var reader = new StreamReader(response.Content.ReadAsStream(), Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The cloud rejects malformed commits, which would drop every payload, so
    /// anything that is not a hex SHA is left out.
    /// </summary>
    public static Release Build(string? version, string? commit, string source)
    {
        var normalized = (commit ?? "").Trim().ToLowerInvariant();
        var trimmedVersion = Present(version) ? version!.Trim() : null;
        if (trimmedVersion is { Length: > 100 }) trimmedVersion = trimmedVersion[..100];
        return new Release(trimmedVersion, CommitFormat.IsMatch(normalized) ? normalized : null, source);
    }

    /// <summary>
    /// HEROKU_BUILD_COMMIT (runtime-dyno-build-metadata) replaces the
    /// deprecated HEROKU_SLUG_COMMIT (runtime-dyno-metadata).
    /// </summary>
    private static string? HerokuCommit(Func<string, string?> env) =>
        Present(env("HEROKU_BUILD_COMMIT")) ? env("HEROKU_BUILD_COMMIT") : env("HEROKU_SLUG_COMMIT");

    /// <summary>
    /// The nearest .git in the root or up to 3 directories above it. A .git
    /// file (a worktree or submodule) points to the real one.
    /// </summary>
    private static string? GitDir(string root)
    {
        var directory = Path.GetFullPath(root);
        for (var i = 0; i <= GitSearchDepth; i++)
        {
            var candidate = Path.Combine(directory, ".git");
            if (Directory.Exists(candidate)) return candidate;
            if (File.Exists(candidate))
            {
                var pointer = (ReadFile(candidate, 1000) ?? "").Trim();
                if (!pointer.StartsWith("gitdir:", StringComparison.Ordinal)) return null;
                return Path.GetFullPath(Path.Combine(directory, pointer[7..].Trim()));
            }
            var parent = Path.GetDirectoryName(directory);
            if (parent is null || parent == directory) return null;
            directory = parent;
        }
        return null;
    }

    private static string? PackedRef(string path, string reference)
    {
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith('#') || line.StartsWith('^')) continue;
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[1] == reference) return parts[0];
            }
        }
        catch (Exception)
        {
            return null;
        }
        return null;
    }

    private static string? ReadFile(string path, int limit)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var reader = new StreamReader(path, Encoding.UTF8);
            var buffer = new char[limit];
            var read = reader.ReadBlock(buffer, 0, limit);
            return new string(buffer, 0, read);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string RemovePrefix(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.Ordinal) ? value[prefix.Length..] : value;

    private static bool Present(string? value) => !string.IsNullOrWhiteSpace(value);
}
