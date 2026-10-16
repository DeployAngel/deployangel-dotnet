using System;
using System.Text.Json.Nodes;

namespace DeployAngel.Tool;

/// <summary>
/// Exit codes are the primary signal for agents and CI; they match the Ruby
/// gem's and the Python package's deployangel command.
/// </summary>
internal static class ExitCodes
{
    public const int Verified = 0;
    public const int Failed = 1;
    public const int Inconclusive = 2;
    public const int TimedOut = 3;
    public const int NotFound = 4;
    public const int Usage = 5;
    public const int InitialOk = 6;
    public const int InitialWarnings = 7;

    public static int? ForVerdict(string? verdict) => verdict switch
    {
        "verified" => Verified,
        "failed" => Failed,
        "inconclusive" => Inconclusive,
        _ => null,
    };
}

/// <summary>Which deployment to look at: by ID, version, commit, or the latest.</summary>
internal sealed record Target(string? DeploymentId = null, string? Version = null, string? Commit = null, bool Latest = false)
{
    public static Target LatestDeployment { get; } = new(Latest: true);

    /// <summary>
    /// What the target was given as, for "no deployment found for …". The
    /// latest prints as True, as the Python command prints it.
    /// </summary>
    public string Value => DeploymentId ?? Version ?? Commit ?? "True";

    public string Describe()
    {
        if (!string.IsNullOrEmpty(Version))
        {
            return Version;
        }
        var commit = Commit ?? "";
        if (commit.Length > 0)
        {
            return commit[..Math.Min(commit.Length, 12)];
        }
        return !string.IsNullOrEmpty(DeploymentId) ? DeploymentId : "latest";
    }
}

internal sealed record Outcome(int ExitCode, JsonObject? Document = null, bool TimedOut = false, bool NotFound = false);

/// <summary>
/// Finds a deployment and polls its verdict document until the requested
/// point: the 15-minute initial check, a verdict, or the end of watching.
/// Shared by the deployangel command and the MCP server.
/// </summary>
internal sealed class Waiter
{
    public static readonly string[] UntilModes = { "initial", "verdict", "closed" };

    private const double PendingPoll = 15;

    private readonly IApiClient _client;
    private readonly Action<double> _sleeper;
    private readonly Func<double> _clock;
    private readonly Action<string>? _onProgress;

    public Waiter(IApiClient client, Action<double> sleeper, Func<double> clock, Action<string>? onProgress = null)
    {
        _client = client;
        _sleeper = sleeper;
        _clock = clock;
        _onProgress = onProgress;
    }

    /// <summary>Null means "keep waiting". Failed always returns immediately.</summary>
    public static int? ExitCode(JsonObject document, string untilMode)
    {
        var verification = JsonValues.ObjectOf(JsonValues.Get(document, "verification"));
        var verdictNode = JsonValues.Get(verification, "verdict");
        var verdict = JsonValues.Truthy(verdictNode) ? JsonValues.Text(verdictNode) : null;
        if (verdict == "failed")
        {
            return ExitCodes.Failed;
        }
        if (untilMode == "closed")
        {
            return verdict is not null && JsonValues.StringOf(JsonValues.Get(verification, "state")) == "closed"
                ? ExitCodes.ForVerdict(verdict)
                : null;
        }
        if (untilMode == "initial")
        {
            if (verdict is not null)
            {
                return ExitCodes.ForVerdict(verdict);
            }
            var check = JsonValues.Get(verification, "initial_check");
            if (JsonValues.Truthy(check))
            {
                return JsonValues.StringOf(JsonValues.Get(check, "result")) == "warnings" ? ExitCodes.InitialWarnings : ExitCodes.InitialOk;
            }
            return null;
        }
        return verdict is not null ? ExitCodes.ForVerdict(verdict) : null;
    }

    public Outcome Wait(Target target, string untilMode = "verdict", double timeout = 1800, bool wait = true)
    {
        var deadline = _clock() + timeout;
        string? lastState = null;
        while (true)
        {
            var deployment = Find(target);
            if (deployment is null)
            {
                if (!wait || _clock() >= deadline)
                {
                    return new Outcome(ExitCodes.NotFound, NotFound: true);
                }
                if (lastState != "pending_registration")
                {
                    Progress($"Waiting for deployment {target.Describe()} to be registered…");
                }
                lastState = "pending_registration";
                _sleeper(Clamp(Math.Min(PendingPoll, deadline - _clock()), 1, PendingPoll));
                continue;
            }

            var document = _client.Verification(JsonValues.Text(JsonValues.Get(deployment, "id")));
            var stateNode = JsonValues.Get(JsonValues.Get(document, "verification"), "state");
            var state = stateNode is null ? null : JsonValues.Text(stateNode);
            if (state != lastState)
            {
                var release = JsonValues.Get(document, "deployment");
                Progress($"{JsonValues.Text(JsonValues.Or(JsonValues.Get(release, "version"), JsonValues.Get(release, "commit")))}: {JsonValues.Text(stateNode)}");
            }
            lastState = state;

            var code = ExitCode(document, untilMode);
            if (code is not null)
            {
                return new Outcome(code.Value, document);
            }
            if (!wait)
            {
                return new Outcome(ExitCodes.TimedOut, document);
            }
            if (_clock() >= deadline)
            {
                return new Outcome(ExitCodes.TimedOut, document, TimedOut: true);
            }
            var poll = Clamp(PollAfter(document), 5, 60);
            _sleeper(Clamp(Math.Min(poll, deadline - _clock()), 1, 60));
        }
    }

    private JsonObject? Find(Target target)
    {
        try
        {
            if (!string.IsNullOrEmpty(target.DeploymentId))
            {
                return new JsonObject { ["id"] = target.DeploymentId };
            }
            if (target.Latest)
            {
                return _client.LatestDeployment();
            }
            var found = _client.Deployments(commit: target.Commit, version: target.Version, limit: 1);
            return found.Count > 0 ? found[0] as JsonObject ?? new JsonObject() : null;
        }
        catch (NotFoundException)
        {
            return null;
        }
    }

    private static double PollAfter(JsonObject document)
    {
        var value = JsonValues.Get(document, "poll_after_seconds");
        try
        {
            return JsonValues.Truthy(value) ? JsonValues.ToInteger(value) : 30;
        }
        catch (FormatException)
        {
            return 30;
        }
    }

    private void Progress(string message) => _onProgress?.Invoke(message);

    private static double Clamp(double value, double low, double high) => Math.Max(low, Math.Min(high, value));
}
