using DeployAngel.Core;

namespace DeployAngel.Tests;

public sealed class ReleaseTests : IDisposable
{
    private const string Sha = "3f2a9c1d4e5b6a7980f1e2d3c4b5a69788f9e0d1";
    private readonly string _root = Directory.CreateTempSubdirectory("deployangel-release").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private Release Resolve(IReadOnlyDictionary<string, string?>? env = null, DeployAngelOptions? options = null,
        string? informational = null, Func<string, string?>? http = null) =>
        ReleaseResolver.Resolve(options ?? new DeployAngelOptions(), env ?? Env.Empty, _root, informational,
            http ?? (_ => null));

    [Fact]
    public void PrefersConfiguration()
    {
        var release = Resolve(Env.Of(("HEROKU_RELEASE_VERSION", "v9")), new DeployAngelOptions { Revision = Sha.ToUpperInvariant() });
        Assert.Equal((null, Sha, "config"), (release.Version, release.Commit, release.Source));
    }

    [Fact]
    public void ReadsHerokuBuildCommitBeforeTheSlugCommit()
    {
        var release = Resolve(Env.Of(("HEROKU_RELEASE_VERSION", "v42"), ("HEROKU_BUILD_COMMIT", Sha), ("HEROKU_SLUG_COMMIT", "abcdef1")));
        Assert.Equal(("v42", Sha, "heroku_dyno_metadata"), (release.Version, release.Commit, release.Source));
        Assert.Equal("abcdef1", Resolve(Env.Of(("HEROKU_SLUG_COMMIT", "abcdef1"))).Commit);
    }

    [Fact]
    public void ReadsKamalRenderFlyAndRailway()
    {
        var kamal = Resolve(Env.Of(("KAMAL_VERSION", $"{Sha}_uncommitted_8f2c")));
        Assert.Equal(($"{Sha}_uncommitted_8f2c", Sha, "kamal"), (kamal.Version, kamal.Commit, kamal.Source));
        Assert.Equal((null, Sha), (Resolve(Env.Of(("KAMAL_VERSION", Sha))).Version, Resolve(Env.Of(("KAMAL_VERSION", Sha))).Commit));
        Assert.Equal("render", Resolve(Env.Of(("RENDER_GIT_COMMIT", Sha))).Source);
        var fly = Resolve(Env.Of(("FLY_IMAGE_REF", "registry.fly.io/shop:deployment-01H9XYZ@sha256:abc")));
        Assert.Equal(("01H9XYZ", "fly"), (fly.Version, fly.Source));
        Assert.Equal(("dep-1", null), (Resolve(Env.Of(("RAILWAY_DEPLOYMENT_ID", "dep-1"))).Version,
            Resolve(Env.Of(("RAILWAY_DEPLOYMENT_ID", "dep-1"))).Commit));
        Assert.Equal(Sha, Resolve(Env.Of(("RAILWAY_GIT_COMMIT_SHA", Sha), ("RAILWAY_DEPLOYMENT_ID", "dep-1"))).Commit);
        Assert.Equal("coolify", Resolve(Env.Of(("SOURCE_COMMIT", Sha), ("COOLIFY_RESOURCE_UUID", "x"))).Source);
        Assert.Equal("git_rev", Resolve(Env.Of(("GIT_REV", Sha))).Source);
    }

    [Fact]
    public void DropsAMalformedCommit()
    {
        var release = Resolve(Env.Of(("HEROKU_RELEASE_VERSION", "v3"), ("HEROKU_BUILD_COMMIT", "not-a-sha")));
        Assert.Equal(("v3", null), (release.Version, release.Commit));
    }

    [Fact]
    public void ReadsTheCommitTheAppWasBuiltFrom()
    {
        var release = Resolve(informational: $"1.4.0+{Sha}");
        Assert.Equal((null, Sha, "assembly_version"), (release.Version, release.Commit, release.Source));
        Assert.Null(ReleaseResolver.AssemblyCommit("1.4.0+build.45"));
        Assert.Null(ReleaseResolver.AssemblyCommit("1.4.0"));
        Assert.Equal("abc1234", ReleaseResolver.AssemblyCommit("2.0.0-beta+ABC1234"));
    }

    [Fact]
    public void PlatformVariablesComeBeforeTheBuiltCommit()
    {
        Assert.Equal("render", Resolve(Env.Of(("RENDER_GIT_COMMIT", Sha)), informational: "1.0.0+abcdef1").Source);
    }

    [Fact]
    public void ReadsARevisionFile()
    {
        File.WriteAllText(Path.Combine(_root, "REVISION"), Sha + "\n");
        Assert.Equal((Sha, "revision_file"), (Resolve().Commit, Resolve().Source));
    }

    [Fact]
    public void ReadsEcsImageTagsAndDigests()
    {
        Assert.Null(ReleaseResolver.Ecs("not json"));
        Assert.Equal(Sha, ReleaseResolver.Ecs($$"""{"Image":"1234.dkr.ecr.aws/shop:{{Sha}}"}""")!.Commit);
        Assert.Equal("v1.2", ReleaseResolver.Ecs("""{"Image":"shop:v1.2"}""")!.Version);
        Assert.Equal("sha256:0123456789ab", ReleaseResolver.Ecs("""{"Image":"shop:latest","ImageID":"sha256:0123456789abcdef"}""")!.Version);
        Assert.Null(ReleaseResolver.Ecs("""{"Image":"shop:latest"}"""));
        var release = Resolve(Env.Of(("ECS_CONTAINER_METADATA_URI_V4", "http://169.254.170.2/v4/x")),
            http: _ => $$"""{"Image":"shop:{{Sha}}"}""");
        Assert.Equal("ecs", release.Source);
    }

    [Fact]
    public void IsUnknownAndPendingWithoutAnySource()
    {
        var release = Resolve();
        Assert.True(release.Unknown);
        Assert.True(release.Pending);
        Assert.Equal("unknown", release.Source);
    }

    [Fact]
    public void ABlankBuildArgRevisionCountsAsUnset()
    {
        var release = Resolve(Env.Of(("GIT_REV", "  ")), new DeployAngelOptions { Revision = " " });
        Assert.True(release.Unknown);
    }

    [Fact]
    public void ReadsADetachedGitHead()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        File.WriteAllText(Path.Combine(_root, ".git", "HEAD"), Sha + "\n");
        Assert.Equal((Sha, "git_head"), (Resolve().Commit, Resolve().Source));
    }

    [Fact]
    public void GitHeadFollowsABranchToItsRefFileOrPackedRefs()
    {
        var git = Directory.CreateDirectory(Path.Combine(_root, ".git")).FullName;
        File.WriteAllText(Path.Combine(git, "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(git, "packed-refs"), $"# pack-refs with: peeled\n{Sha} refs/heads/main\n^abc\n");
        Assert.Equal(Sha, ReleaseResolver.GitHead(_root));
        Directory.CreateDirectory(Path.Combine(git, "refs", "heads"));
        File.WriteAllText(Path.Combine(git, "refs", "heads", "main"), "abcdef1234567\n");
        Assert.Equal("abcdef1234567", ReleaseResolver.GitHead(_root));
    }

    [Fact]
    public void GitHeadFollowsAGitdirFileAndCommondir()
    {
        var main = Directory.CreateDirectory(Path.Combine(_root, "main", ".git")).FullName;
        Directory.CreateDirectory(Path.Combine(main, "refs", "heads"));
        File.WriteAllText(Path.Combine(main, "refs", "heads", "feature"), Sha);
        var worktreeGit = Directory.CreateDirectory(Path.Combine(main, "worktrees", "wt")).FullName;
        File.WriteAllText(Path.Combine(worktreeGit, "HEAD"), "ref: refs/heads/feature");
        File.WriteAllText(Path.Combine(worktreeGit, "commondir"), "../..");
        var worktree = Directory.CreateDirectory(Path.Combine(_root, "wt")).FullName;
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {worktreeGit}");
        Assert.Equal(Sha, ReleaseResolver.GitHead(worktree));
    }

    [Fact]
    public void GitHeadLooksUpToThreeParentDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        File.WriteAllText(Path.Combine(_root, ".git", "HEAD"), Sha);
        var deep = Directory.CreateDirectory(Path.Combine(_root, "a", "b", "c")).FullName;
        Assert.Equal(Sha, ReleaseResolver.GitHead(deep));
        var deeper = Directory.CreateDirectory(Path.Combine(_root, "a", "b", "c", "d")).FullName;
        Assert.Null(ReleaseResolver.GitHead(deeper));
    }

    [Fact]
    public void GitHeadIgnoresUnsafeAndMalformedRefs()
    {
        var git = Directory.CreateDirectory(Path.Combine(_root, ".git")).FullName;
        File.WriteAllText(Path.Combine(git, "HEAD"), "ref: refs/../../etc/passwd");
        Assert.Null(ReleaseResolver.GitHead(_root));
        File.WriteAllText(Path.Combine(git, "HEAD"), "ref: HEAD");
        Assert.Null(ReleaseResolver.GitHead(_root));
        File.WriteAllText(Path.Combine(git, "HEAD"), "not a commit");
        Assert.True(Resolve().Unknown);
    }

    [Fact]
    public void GitHeadComesAfterTheRevisionFileAndBeforeEcs()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        File.WriteAllText(Path.Combine(_root, ".git", "HEAD"), "abcdef1234567");
        var env = Env.Of(("ECS_CONTAINER_METADATA_URI_V4", "http://x"));
        Assert.Equal("git_head", Resolve(env, http: _ => """{"Image":"shop:v9"}""").Source);
        File.WriteAllText(Path.Combine(_root, "REVISION"), Sha);
        Assert.Equal("revision_file", Resolve(env).Source);
    }

    [Fact]
    public void CodeFingerprintIsTheFileManifestsHash()
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["src/Shop/Program.cs"] = "0123456789abcdef" };
        var manifest = FileManifest.From(files, truncated: false);
        var release = ReleaseResolver.CodeFingerprint(manifest);
        Assert.Equal(($"code:{manifest.Hash![..12]}", "code_fingerprint"), (release.Version, release.Source));
    }

    [Fact]
    public void CodeFingerprintNeedsACompleteManifest()
    {
        Assert.True(ReleaseResolver.CodeFingerprint(null).Unknown);
        Assert.True(ReleaseResolver.CodeFingerprint(FileManifest.Empty).Unknown);
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["a.cs"] = "00" };
        Assert.True(ReleaseResolver.CodeFingerprint(FileManifest.From(files, truncated: true)).Unknown);
    }
}
