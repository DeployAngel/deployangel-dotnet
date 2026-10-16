using System.Text.Json.Nodes;

namespace DeployAngel.Tool.Tests;

/// <summary>Streams, a clock, and a checkout of its own for each test.</summary>
public abstract class CliTestBase : IDisposable
{
    internal const string Head = "81ac27d0000";

    internal StringWriter Stdout { get; } = new();

    internal StringWriter Stderr { get; } = new();

    internal Clock Clock { get; } = new();

    internal TempDirectory Root { get; } = new();

    internal int Run(FakeClient client, params string[] argv) => RunWith(client, Head, null, argv);

    internal int RunWith(FakeClient? client, string? gitHead, Dictionary<string, string>? env, params string[] argv) =>
        new Cli(argv, env: env ?? new Dictionary<string, string>(), stdout: Stdout, stderr: Stderr, client: client,
            sleeper: Clock.Advance, clock: Clock.Read, gitHead: () => gitHead, root: Root.Path).Run();

    internal void Clear()
    {
        Stdout.GetStringBuilder().Clear();
        Stderr.GetStringBuilder().Clear();
    }

    public void Dispose()
    {
        Root.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// The deployangel command, ported case for case from the Python package's
/// tests (themselves ported from the Ruby gem's specs), so every command
/// behaves the same: options, output, exit codes.
/// </summary>
public sealed class CliTests : CliTestBase
{
    [Fact]
    public void MapsVerdictsToExitCodes()
    {
        foreach (var (verdict, code) in new[] { ("verified", 0), ("failed", 1), ("inconclusive", 2) })
        {
            Assert.Equal(code, Run(new FakeClient(new[] { Documents.Verdict("closed", verdict) }), "verify", "--format=json"));
        }
    }

    [Fact]
    public void PrintsWhatAClearanceDoesntCoverRightUnderItInTextAndInTheJobSummary()
    {
        var document = Documents.Verdict("closed", "verified");
        document["clearance"]!["scope"] = new JsonObject
        {
            ["lines"] = new JsonArray("Not assessed: HTTP p95 latency (needs 200 requests; had 140)", "Not run since the deploy: critical flow sign_in"),
        };
        var summary = Root["summary.md"];

        Assert.Equal(0, RunWith(new FakeClient(new[] { document }), Head, new() { ["GITHUB_STEP_SUMMARY"] = summary }, "verify", "--format=text"));
        Assert.Equal(new[]
        {
            "v184 verified", "State: closed · Verdict: verified · Confidence: high · Coverage: 100%",
            "Not assessed: HTTP p95 latency (needs 200 requests; had 140)", "Not run since the deploy: critical flow sign_in",
        }, Stdout.ToString().Split('\n').Take(4));
        Assert.Contains("Not run since the deploy: critical flow sign_in", File.ReadAllText(summary));
    }

    [Fact]
    public void DefaultsToTheGitHeadCommit()
    {
        var client = new FakeClient(new[] { Documents.Verdict("closed", "verified") });
        Run(client, "verify");
        Assert.Equal(new Call("deployments", """{"commit":"81ac27d0000","version":null,"limit":1}"""), client.Calls[0]);
    }

    [Fact]
    public void Exits3WithoutWaitingWhileInProgress()
    {
        Assert.Equal(3, Run(new FakeClient(new[] { Documents.Verdict("observing") }), "verify"));
    }

    [Fact]
    public void WaitsForAVerdictPrintingProgressToStderr()
    {
        var client = new FakeClient(new[]
        {
            Documents.Verdict("pending"), Documents.Verdict("observing"), Documents.Verdict("closed", "verified"),
        });
        Assert.Equal(0, Run(client, "verify", "--wait", "--format=json"));
        foreach (var text in new[] { "v184: pending", "v184: observing", "v184: closed" })
        {
            Assert.Contains(text, Stderr.ToString());
        }
        Assert.Equal("verified", (string?)JsonNode.Parse(Stdout.ToString())!["verification"]!["verdict"]);
    }

    [Fact]
    public void ReturnsAtTheInitialCheckNeverAsSuccess()
    {
        var ok = new FakeClient(new[]
        {
            Documents.Verdict("observing"),
            Documents.Verdict("observing", initialCheck: new JsonObject { ["result"] = "no_problems_so_far" }),
        });
        var warn = new FakeClient(new[] { Documents.Verdict("observing", initialCheck: new JsonObject { ["result"] = "warnings" }) });
        Assert.Equal(6, Run(ok, "verify", "--wait", "--until=initial"));
        Assert.Equal(7, Run(warn, "verify", "--wait", "--until=initial"));
    }

    [Fact]
    public void FailedReturnsImmediatelyEvenWhenWaitingThroughWatching()
    {
        Assert.Equal(1, Run(new FakeClient(new[] { Documents.Verdict("closed", "failed") }), "verify", "--wait", "--until=closed"));
    }

    [Fact]
    public void KeepsWaitingThroughWatchingWithUntilClosed()
    {
        var client = new FakeClient(new[] { Documents.Verdict("watching", "verified"), Documents.Verdict("closed", "verified") });
        Assert.Equal(0, Run(client, "verify", "--wait", "--until=closed"));
        Assert.Equal(2, client.Calls.Count(call => call.Name == "verification"));
    }

    [Fact]
    public void TimesOutWithExit3AndTheCurrentDocument()
    {
        var client = new FakeClient(new[] { Documents.Verdict("observing") });
        Assert.Equal(3, Run(client, "verify", "--wait", "--timeout=5m", "--format=json"));
        Assert.Contains("timed out", Stderr.ToString());
        Assert.Equal(Clock.Start + 300, Clock.Now);
    }

    [Fact]
    public void WaitsForTheDeploymentToBeRegisteredOrExits4()
    {
        Assert.Equal(4, Run(new FakeClient(deployments: new JsonArray()), "verify"));

        var appears = new FakeClient(new[] { Documents.Verdict("closed", "verified") }, new JsonArray());
        void Sleeper(double seconds)
        {
            Clock.Advance(seconds);
            appears.DeploymentsList = new JsonArray(new JsonObject { ["id"] = 42 });
        }

        var code = new Cli(new[] { "verify", "--wait" }, env: new Dictionary<string, string>(), stdout: Stdout, stderr: Stderr, client: appears,
            sleeper: Sleeper, clock: Clock.Read, gitHead: () => "81ac27d", root: Root.Path).Run();
        Assert.Equal(0, code);
        Assert.Contains("to be registered", Stderr.ToString());
    }

    [Fact]
    public void RendersAReadableSummary()
    {
        var document = Documents.Verdict("closed", "failed");
        document["findings"] = JsonNode.Parse("""
            [{"signal": "http_5xx_rate", "scope": "application", "status": "failing", "baseline_value": 0.002,
              "observed_value": 0.068, "observed_n": 2140},
             {"signal": "missing_recurring_job", "scope": "recurring_job:PruneHistoryJob", "status": "failing",
              "baseline_value": 86400.0, "observed_value": null, "threshold": "expected by 08:15 UTC (declared schedule)"}]
            """);
        document["exceptions"] = JsonNode.Parse("""
            [{"exception_class": "KeyNotFoundException", "top_frame": "Shop/Controllers/OrdersController.cs#Create", "count": 8,
              "sources": {"route:POST /orders": 8}}]
            """);
        document["deployment"]!["promoted_from"] = new JsonObject { ["environment"] = "staging", ["version"] = "v57", ["verdict"] = "verified" };
        Run(new FakeClient(new[] { document }), "verify", "--format=text");

        var output = Stdout.ToString();
        foreach (var text in new[]
                 {
                     "HTTP 5xx rate on application: 0.2% -> 6.8% (2140 samples)",
                     "KeyNotFoundException in Shop/Controllers/OrdersController.cs#Create (8x) route:POST /orders", "Verdict: failed",
                     "Promoted from staging v57 (cleared)",
                     "Recurring job on recurring_job:PruneHistoryJob: didn't run, expected by 08:15 UTC (declared schedule)",
                 })
        {
            Assert.Contains(text, output);
        }
        Assert.DoesNotContain("8640000", output);
    }

    [Fact]
    public void AddsTheVerdictToTheGithubActionsJobSummary()
    {
        var document = Documents.Verdict("closed", "failed");
        document["findings"] = JsonNode.Parse("""
            [{"signal": "p95_latency", "scope": "route:GET /a|b", "status": "failing", "baseline_value": 120.0,
              "observed_value": 480.0, "observed_n": 900},
             {"signal": "job_failure_rate", "scope": "application", "status": "pass"}]
            """);
        document["exceptions"] = JsonNode.Parse("""[{"exception_class": "KeyNotFoundException", "top_frame": "Shop/Jobs/SyncJob.cs#Run", "count": 3}]""");
        var path = Root["summary.md"];
        var code = RunWith(new FakeClient(new[] { document }), Head, new() { ["GITHUB_STEP_SUMMARY"] = path }, "verify", "--format=json");

        Assert.Equal(1, code);
        var summary = File.ReadAllText(path);
        foreach (var text in new[]
                 {
                     "### DeployAngel: v184 failed", "| failing | p95 latency on route:GET /a\\|b: 120 ms -> 480 ms (900 samples) |",
                     "**New exceptions**", "- KeyNotFoundException in Shop/Jobs/SyncJob.cs#Run (3x)",
                     "[Open in DeployAngel](https://app.deployangel.com/apps/1/deployments/42)",
                 })
        {
            Assert.Contains(text, summary);
        }
        Assert.DoesNotContain("Job failure rate", summary);
        Assert.Equal("failed", (string?)JsonNode.Parse(Stdout.ToString())!["verification"]!["verdict"]);
    }

    [Fact]
    public void SaysInTheJobSummaryWhenAnInitialCheckIsntAClearance()
    {
        var path = Root["summary.md"];
        var ok = new FakeClient(new[] { Documents.Verdict("observing", initialCheck: new JsonObject { ["result"] = "no_problems_so_far" }) });
        RunWith(ok, Head, new() { ["GITHUB_STEP_SUMMARY"] = path }, "verify", "--until=initial");
        RunWith(new FakeClient(deployments: new JsonArray()), Head, new() { ["GITHUB_STEP_SUMMARY"] = path }, "verify");

        var summary = File.ReadAllText(path);
        foreach (var text in new[]
                 {
                     "### DeployAngel: v184 has no problems so far, not cleared yet",
                     "Initial check: no problems so far. Not cleared yet.", "### DeployAngel: no deployment found for 81ac27d0000",
                 })
        {
            Assert.Contains(text, summary);
        }
    }

    [Fact]
    public void StillExitsWithTheVerdictWhenTheJobSummaryCantBeWritten()
    {
        var code = RunWith(new FakeClient(new[] { Documents.Verdict("closed", "verified") }), Head,
            new() { ["GITHUB_STEP_SUMMARY"] = "/nonexistent/summary.md" }, "verify", "--format=json");
        Assert.Equal(0, code);
        Assert.Contains("couldn't write the job summary", Stderr.ToString());
    }

    [Fact]
    public void RegistersDeploymentsAndReportsChecksAgainstTheCurrentCommit()
    {
        var client = new FakeClient();
        Assert.Equal(0, Run(client, "release", "--version=v185"));
        Assert.Equal(0, Run(client, "check", "--name=smoke", "--status=pass", "--covers=password_reset"));
        Assert.Contains(new Call("register", """{"commit":"81ac27d0000","version":"v185","kind":null,"provider":null,"source_url":null}"""), client.Calls);
        Assert.Contains(new Call("check", """{"reference":"commit:81ac27d0000","name":"smoke","status":"pass","covers":["password_reset"],"details_url":null}"""),
            client.Calls);
    }

    [Fact]
    public void FillsInCommitLabelProviderAndLinkInsideGithubActions()
    {
        var client = new FakeClient();
        var env = new Dictionary<string, string>
        {
            ["DEPLOYANGEL_API_TOKEN"] = "t", ["GITHUB_ACTIONS"] = "true", ["GITHUB_SHA"] = "abc1234def5678", ["GITHUB_RUN_NUMBER"] = "12",
            ["GITHUB_RUN_ID"] = "99", ["GITHUB_SERVER_URL"] = "https://github.com", ["GITHUB_REPOSITORY"] = "acme/shop",
        };
        Assert.Equal(0, RunWith(client, null, env, "release"));
        Assert.Equal(new Call("register", """{"commit":"abc1234def5678","version":"run-12","kind":null,"provider":"github_actions","source_url":"https://github.com/acme/shop/actions/runs/99"}"""),
            client.Calls[^1]);

        RunWith(client, null, env, "release", "--version=2026.09.30", "--provider=manual");
        var last = JsonNode.Parse(client.Calls[^1].Args)!;
        Assert.Equal("2026.09.30", (string?)last["version"]);
        Assert.Equal("manual", (string?)last["provider"]);
        Assert.Equal("abc1234def5678", (string?)last["commit"]);
    }

    [Fact]
    public void RegistersKamalsReleaseFromAPostDeployHook()
    {
        var client = new FakeClient();
        var env = new Dictionary<string, string> { ["DEPLOYANGEL_API_TOKEN"] = "t", ["KAMAL_VERSION"] = "abc1234def5678", ["KAMAL_COMMAND"] = "deploy" };
        RunWith(client, null, env, "release");
        Assert.Equal(new Call("register", """{"commit":"abc1234def5678","version":null,"kind":null,"provider":"kamal","source_url":null}"""),
            client.Calls[^1]);
    }

    [Fact]
    public void ReportsUsageAndAuthProblemsWithExit5()
    {
        Assert.Equal(5, Run(new FakeClient(), "verify", "--until=never"));
        Assert.Equal(5, Run(new FakeClient(), "check", "--name=x"));
        Assert.Equal(5, Run(new FakeClient(), "frobnicate"));
        var code = new Cli(new[] { "verify" }, env: new Dictionary<string, string>(), stdout: Stdout, stderr: Stderr, gitHead: () => "abc1234",
            root: Root.Path).Run();
        Assert.Equal(5, code);
        Assert.Contains("DEPLOYANGEL_API_TOKEN is not set", Stderr.ToString());
    }

    // The options parse as the Python command's argparse parses them, so a
    // mistake gets the same message from every deployangel command.
    [Theory]
    [InlineData("argument --format: invalid choice: 'x' (choose from text, json)", "verify", "--format=x")]
    [InlineData("argument --until: invalid choice: 'never' (choose from initial, verdict, closed)", "verify", "--until=never")]
    [InlineData("argument --commit: expected one argument", "verify", "--commit")]
    [InlineData("argument --commit: expected one argument", "verify", "--commit", "--wait")]
    [InlineData("argument --wait: ignored explicit argument '1'", "verify", "--wait=1")]
    [InlineData("unrecognized arguments: foo bar", "verify", "foo", "bar")]
    [InlineData("unrecognized arguments: --comm=1", "verify", "--comm=1")]
    [InlineData("unrecognized arguments: -- b", "verify", "--commit=a", "--", "b")]
    [InlineData("argument --max-requests: invalid int value: 'x'", "exercise", "--url=https://x", "--max-requests=x")]
    [InlineData("invalid argument: --timeout=5x", "verify", "--timeout=5x")]
    [InlineData("argument --status: invalid choice: 'maybe' (choose from pass, fail)", "check", "--status=maybe")]
    [InlineData("exception needs a FINGERPRINT", "exception", "-x")]
    [InlineData("unknown command: frobnicate", "frobnicate")]
    public void ReportsBadOptionsAsThePythonCommandDoes(string message, params string[] argv)
    {
        Assert.Equal(5, Run(new FakeClient(), argv));
        Assert.Equal($"deployangel: {message}\nRun `deployangel help` for usage.\n", Stderr.ToString());
    }

    [Fact]
    public void TakesOptionValuesAsTheirOwnArgumentsAndNegativeNumbersAsValues()
    {
        var client = new FakeClient(new[] { Documents.Verdict("closed", "verified") });
        Assert.Equal(0, Run(client, "verify", "--commit", "ABC1234", "--format", "json"));
        Assert.Equal(new Call("deployments", """{"commit":"abc1234","version":null,"limit":1}"""), client.Calls[0]);
        Assert.Equal(0, Run(new FakeClient(new[] { Documents.Verdict("closed", "verified") }), "verify", "--version", "-5"));
    }

    [Fact]
    public void PrintsHelpAndTheVersion()
    {
        Assert.Equal(0, Run(new FakeClient(), "help"));
        Assert.StartsWith("Usage: deployangel <command> [options]\n", Stdout.ToString());
        Assert.Contains("Environment: DEPLOYANGEL_URL (default https://api.deployangel.com)", Stdout.ToString());
        Clear();
        Assert.Equal(0, Run(new FakeClient(), "version"));
        Assert.Matches(@"\A\d+\.\d+\.\d+\S*\n\z", Stdout.ToString());
    }
}
