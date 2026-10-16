using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace DeployAngel.Tool;

/// <summary>
/// Recognizes common CI systems from their environment variables, so
/// <c>deployangel release</c> needs no arguments there: the commit, a build
/// label, the provider, and a link back to the run. Inside a Kamal hook, the
/// release is Kamal's, linked to the CI run when Kamal runs in CI.
/// </summary>
internal sealed record CiEnvironment(string Provider, string? Commit, string? Version, string? RawSourceUrl = null)
{
    /// <summary>Only https links are kept; the cloud rejects anything else.</summary>
    public string? SourceUrl => (RawSourceUrl ?? "").StartsWith("https://", StringComparison.Ordinal) ? RawSourceUrl : null;

    public JsonObject ToJson() => new()
    {
        ["provider"] = Provider,
        ["commit"] = Commit,
        ["version"] = Version,
        ["source_url"] = SourceUrl,
    };

    public static CiEnvironment? Detect(IReadOnlyDictionary<string, string> env)
    {
        var ci = DetectCi(env);
        if (!Releases.Present(Get(env, "KAMAL_VERSION")))
        {
            return ci;
        }
        var kamal = Releases.Kamal(env["KAMAL_VERSION"]);
        return new CiEnvironment("kamal", kamal.Commit, kamal.Version, ci?.SourceUrl);
    }

    private static CiEnvironment? DetectCi(IReadOnlyDictionary<string, string> env)
    {
        if (Get(env, "GITHUB_ACTIONS") == "true")
        {
            string? runUrl = null;
            if (!string.IsNullOrEmpty(Get(env, "GITHUB_RUN_ID")))
            {
                runUrl = $"{Get(env, "GITHUB_SERVER_URL") ?? "None"}/{Get(env, "GITHUB_REPOSITORY") ?? "None"}/actions/runs/{env["GITHUB_RUN_ID"]}";
            }
            return new CiEnvironment("github_actions", Get(env, "GITHUB_SHA"), Label("run", Get(env, "GITHUB_RUN_NUMBER")), runUrl);
        }
        if (Get(env, "GITLAB_CI") == "true")
        {
            return new CiEnvironment("gitlab_ci", Get(env, "CI_COMMIT_SHA"), Label("pipeline", Get(env, "CI_PIPELINE_IID")),
                Get(env, "CI_PIPELINE_URL"));
        }
        if (Get(env, "CIRCLECI") == "true")
        {
            return new CiEnvironment("circleci", Get(env, "CIRCLE_SHA1"), Label("build", Get(env, "CIRCLE_BUILD_NUM")),
                Get(env, "CIRCLE_BUILD_URL"));
        }
        if (Get(env, "BUILDKITE") == "true")
        {
            return new CiEnvironment("buildkite", Get(env, "BUILDKITE_COMMIT"), Label("build", Get(env, "BUILDKITE_BUILD_NUMBER")),
                Get(env, "BUILDKITE_BUILD_URL"));
        }
        return null;
    }

    private static string? Label(string prefix, string? number) => Releases.Present(number) ? $"{prefix}-{number}" : null;

    private static string? Get(IReadOnlyDictionary<string, string> env, string name) =>
        env.TryGetValue(name, out var value) ? value : null;
}
