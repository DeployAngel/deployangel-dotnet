using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DeployAngel.Tool;

/// <summary>A release as the agent reports it: a version, a commit, or both, and where they came from.</summary>
internal sealed record Release(string? Version, string? Commit, string Source)
{
    public bool Unknown => Version is null && Commit is null;
}

/// <summary>
/// Which release a process here would report, resolved the way the agent
/// resolves it at boot, from the same environment variables, so
/// <c>deployangel doctor</c> can say it without loading the app. The
/// command needs only the parts that read the environment and the checkout.
/// </summary>
internal static class Releases
{
    private static readonly Regex CommitFormat = new(@"\A[0-9a-f]{7,40}\z", RegexOptions.CultureInvariant);

    // Tags that move from build to build, so they can't identify a release.
    private static readonly string[] MovingTags = { "latest", "main", "master", "production", "prod", "staging", "stable", "release" };

    // Parent directories searched for .git, for an app that lives in a
    // subdirectory of its repository.
    private const int GitSearchDepth = 3;

    /// <summary>
    /// Order: explicit configuration, Heroku dyno metadata, the hosting
    /// platform's own variables (Kamal, Render, Fly.io, Railway, Coolify, and
    /// Dokku's GIT_REV), a REVISION file, a git checkout, then ECS container
    /// metadata. Unknown when none of them names the release.
    /// </summary>
    public static Release Resolve(IReadOnlyDictionary<string, string> env, string? root, Func<string, string?>? http = null)
    {
        http ??= FetchMetadata;
        var version = Env(env, "DEPLOYANGEL_RELEASE_VERSION");
        var revision = Env(env, "DEPLOYANGEL_REVISION");
        if (Present(version) || Present(revision))
        {
            return Build(version, revision, "config");
        }
        if (Present(Env(env, "HEROKU_RELEASE_VERSION")) || Present(HerokuCommit(env)))
        {
            return Build(Env(env, "HEROKU_RELEASE_VERSION"), HerokuCommit(env), "heroku_dyno_metadata");
        }
        if (Present(Env(env, "KAMAL_VERSION")))
        {
            return Kamal(env["KAMAL_VERSION"]);
        }
        if (Present(Env(env, "RENDER_GIT_COMMIT")))
        {
            return Build(null, env["RENDER_GIT_COMMIT"], "render");
        }
        var tag = FlyTag(Env(env, "FLY_IMAGE_REF"));
        if (tag is not null)
        {
            return Build(tag, null, "fly");
        }
        if (Present(Env(env, "RAILWAY_GIT_COMMIT_SHA")) || Present(Env(env, "RAILWAY_DEPLOYMENT_ID")))
        {
            return Railway(env);
        }
        if (Present(Env(env, "SOURCE_COMMIT")) && (Present(Env(env, "COOLIFY_CONTAINER_NAME")) || Present(Env(env, "COOLIFY_RESOURCE_UUID"))))
        {
            return Build(null, env["SOURCE_COMMIT"], "coolify");
        }
        // Dokku sets GIT_REV, but the name is generic, so the source says so.
        if (Present(Env(env, "GIT_REV")))
        {
            return Build(null, env["GIT_REV"], "git_rev");
        }
        if (!string.IsNullOrEmpty(root))
        {
            var file = ReadFile(Path.Combine(root, "REVISION"), 100);
            if (file is not null)
            {
                return Build(null, file, "revision_file");
            }
            // Anything that isn't a commit (a SHA-256 repository's HEAD, a
            // malformed ref) falls through to the next source.
            var checkout = Build(null, GitHead(root), "git_head");
            if (checkout.Commit is not null)
            {
                return checkout;
            }
        }
        if (Present(Env(env, "ECS_CONTAINER_METADATA_URI_V4")))
        {
            var release = Ecs(http(env["ECS_CONTAINER_METADATA_URI_V4"]));
            if (release is not null)
            {
                return release;
            }
        }
        return new Release(null, null, "unknown");
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
    /// The commit HEAD names in a checkout, for servers deployed by
    /// <c>git pull</c>. Read from the repository's files, never by running
    /// git; anything unreadable or unexpected is null.
    /// </summary>
    public static string? GitHead(string root)
    {
        try
        {
            var gitDir = GitDir(root);
            if (gitDir is null)
            {
                return null;
            }
            // A worktree's own directory holds HEAD; its branches live in the
            // main repository, which commondir names.
            var commonDir = gitDir;
            var common = (ReadFile(Path.Combine(gitDir, "commondir"), 1000) ?? "").Trim();
            if (common.Length > 0)
            {
                commonDir = Path.GetFullPath(Path.Combine(gitDir, common));
            }
            var head = (ReadFile(Path.Combine(gitDir, "HEAD"), 200) ?? "").Trim();
            if (!head.StartsWith("ref:", StringComparison.Ordinal))
            {
                return head.Length > 0 ? head : null;
            }
            var reference = head[4..].Trim();
            if (!reference.StartsWith("refs/", StringComparison.Ordinal) || reference.Contains("..", StringComparison.Ordinal))
            {
                return null;
            }
            foreach (var directory in new[] { gitDir, commonDir })
            {
                var commit = (ReadFile(Path.Combine(new[] { directory }.Concat(reference.Split('/')).ToArray()), 200) ?? "").Trim();
                if (commit.Length > 0)
                {
                    return commit;
                }
            }
            return PackedRef(Path.Combine(commonDir, "packed-refs"), reference);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The cloud rejects malformed commits, which would drop every payload, so anything that is not a hex SHA is left out.</summary>
    public static Release Build(string? version, string? commit, string source)
    {
        var normalized = (commit ?? "").Trim().ToLowerInvariant();
        var trimmed = (version ?? "").Trim();
        return new Release(
            trimmed.Length > 0 ? trimmed[..Math.Min(trimmed.Length, 100)] : null,
            CommitFormat.IsMatch(normalized) ? normalized : null,
            source);
    }

    /// <summary>
    /// Railway sets the commit for deploys from GitHub. Others, such as
    /// <c>railway up</c>, only have a deployment ID, which identifies the release.
    /// </summary>
    private static Release Railway(IReadOnlyDictionary<string, string> env) =>
        Present(Env(env, "RAILWAY_GIT_COMMIT_SHA"))
            ? Build(null, env["RAILWAY_GIT_COMMIT_SHA"], "railway")
            : Build(Env(env, "RAILWAY_DEPLOYMENT_ID"), null, "railway");

    /// <summary>Fly.io tags each deploy's image ("registry.fly.io/shop:deployment-01H9…"), and the tag identifies the release.</summary>
    private static string? FlyTag(string? imageRef)
    {
        var name = (imageRef ?? "").Trim().Split('@', 2)[0].Split('/')[^1];
        var parts = name.Split(':', 2);
        var tag = parts.Length == 2 ? RemovePrefix(parts[1], "deployment-") : "";
        return tag.Length > 0 ? tag : null;
    }

    private static string RemovePrefix(string text, string prefix) =>
        text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..] : text;

    /// <summary>
    /// The image tag identifies the release, and is the commit when it looks
    /// like one. A moving tag such as "latest" can't, so the image digest does.
    /// </summary>
    private static Release? Ecs(string? body)
    {
        JsonObject? data;
        try
        {
            data = JsonNode.Parse(body ?? "") as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
        if (data is null)
        {
            return null;
        }
        var name = (JsonValues.StringOf(data["Image"]) ?? "").Split('@', 2)[0].Split('/')[^1];
        var parts = name.Split(':', 2);
        var tag = parts.Length == 2 ? parts[1].Trim() : "";
        if (CommitFormat.IsMatch(tag.ToLowerInvariant()))
        {
            return Build(null, tag, "ecs");
        }
        if (tag.Length > 0 && !MovingTags.Contains(tag.ToLowerInvariant()))
        {
            return Build(tag, null, "ecs");
        }
        var digest = Regex.Match(JsonValues.StringOf(data["ImageID"]) ?? "", @"\Asha256:([0-9a-fA-F]{12})");
        return digest.Success ? Build($"sha256:{digest.Groups[1].Value}", null, "ecs") : null;
    }

    /// <summary>One request to the container's own metadata endpoint, which is local, so the timeout is short. Null on any failure.</summary>
    private static string? FetchMetadata(string uri)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            using var response = http.Send(new HttpRequestMessage(HttpMethod.Get, uri));
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            using var reader = new StreamReader(response.Content.ReadAsStream());
            return reader.ReadToEnd();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>HEROKU_BUILD_COMMIT (runtime-dyno-build-metadata) replaces the deprecated HEROKU_SLUG_COMMIT (runtime-dyno-metadata).</summary>
    private static string? HerokuCommit(IReadOnlyDictionary<string, string> env) =>
        Present(Env(env, "HEROKU_BUILD_COMMIT")) ? Env(env, "HEROKU_BUILD_COMMIT") : Env(env, "HEROKU_SLUG_COMMIT");

    /// <summary>The nearest .git in the root or up to 3 directories above it. A .git file (a worktree or submodule) points to the real one.</summary>
    private static string? GitDir(string root)
    {
        var directory = Path.GetFullPath(root);
        for (var level = 0; level <= GitSearchDepth; level++)
        {
            var candidate = Path.Combine(directory, ".git");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            if (File.Exists(candidate))
            {
                var pointer = (ReadFile(candidate, 1000) ?? "").Trim();
                if (!pointer.StartsWith("gitdir:", StringComparison.Ordinal))
                {
                    return null;
                }
                return Path.GetFullPath(Path.Combine(directory, pointer[7..].Trim()));
            }
            var parent = Path.GetDirectoryName(directory);
            if (parent is null || parent == directory)
            {
                return null;
            }
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
                if (line.StartsWith('#') || line.StartsWith('^'))
                {
                    continue;
                }
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[1] == reference)
                {
                    return parts[0];
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return null;
    }

    private static string? ReadFile(string path, int limit)
    {
        try
        {
            using var reader = new StreamReader(path);
            var buffer = new char[limit];
            var read = reader.ReadBlock(buffer, 0, limit);
            return new string(buffer, 0, read);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Env(IReadOnlyDictionary<string, string> env, string name) =>
        env.TryGetValue(name, out var value) ? value : null;

    internal static bool Present(string? value) => !string.IsNullOrWhiteSpace(value);
}
