using System.Text.Json.Nodes;

namespace DeployAngel.Tool.Tests;

public sealed class PlanTests : CliTestBase
{
    private static JsonObject Document() => Documents.Verdict("observing").With("exercise_plan", Documents.ExercisePlan());

    [Fact]
    public void SaysWhatsShortAndWhatToExercise()
    {
        Assert.Equal(0, Run(new FakeClient(new[] { Document() }), "plan", "--format=text"));
        var output = Stdout.ToString();
        foreach (var text in new[]
                 {
                     "v184: Not cleared yet.", "requests: 12 of 30 (low-traffic rule)",
                     "routes run 3+ times: 1 of the 3 needed (3 normally active)",
                     "GET /orders/{id} (normally active, run 1 of 3)",
                     "POST /password_resets (changed in this release, not run yet) [changes data]",
                     "SendInvoiceJob (normally active, runs when the app starts it)", "Use a test account, or ask first",
                     "Then report it: deployangel check --name=\"exercise plan\"",
                 })
        {
            Assert.Contains(text, output);
        }
        // What clearance waits on comes first; the rest is only worth running.
        var also = output.IndexOf("Also worth running, not needed to clear", StringComparison.Ordinal);
        Assert.True(output.IndexOf("Needed to clear", StringComparison.Ordinal) < output.IndexOf("SendInvoiceJob", StringComparison.Ordinal));
        Assert.True(output.IndexOf("SendInvoiceJob", StringComparison.Ordinal) < also);
        Assert.True(also < output.IndexOf("POST /password_resets", StringComparison.Ordinal));
    }

    [Fact]
    public void CountsNormallyActiveItemsAsNeededFromAServerThatDoesntSay()
    {
        var plan = Documents.ExercisePlan();
        foreach (var item in plan["items"]!.AsArray())
        {
            item!.AsObject().Remove("needed");
        }
        Run(new FakeClient(new[] { Documents.Verdict("observing").With("exercise_plan", plan) }), "plan", "--format=text");
        var output = Stdout.ToString();
        Assert.True(output.IndexOf("GET /orders/{id}", StringComparison.Ordinal) < output.IndexOf("Also worth running", StringComparison.Ordinal));
    }

    [Fact]
    public void PrintsTheDeploymentAndPlanAsJson()
    {
        Run(new FakeClient(new[] { Document() }), "plan", "--format=json");
        var data = JsonNode.Parse(Stdout.ToString())!.AsObject();
        Assert.Equal(new[] { "deployment", "exercise_plan" }, data.Select(pair => pair.Key));
        Assert.Equal("exercisable", (string?)data["exercise_plan"]!["status"]);
    }

    [Fact]
    public void SaysPlainlyWhenTheServerReturnsNoPlanOrNothingIsFound()
    {
        Run(new FakeClient(new[] { Documents.Verdict("observing") }), "plan", "--format=text");
        Assert.Contains("No exercise plan in this response; update the server.", Stdout.ToString());
        Assert.Equal(4, Run(new FakeClient(deployments: new JsonArray()), "plan"));
    }

    [Fact]
    public void AddsTheFirstItemsToVerifyAndTheJobSummary()
    {
        var path = Root["summary.md"];
        RunWith(new FakeClient(new[] { Document() }), Head, new() { ["GITHUB_STEP_SUMMARY"] = path }, "verify", "--format=text");
        Assert.Contains("To clear sooner, exercise (deployangel plan for details):", Stdout.ToString());
        Assert.Contains("**To clear sooner, exercise (deployangel plan for details)**", File.ReadAllText(path));
        Assert.Contains("- SendInvoiceJob", File.ReadAllText(path));
        Assert.DoesNotContain("POST /password_resets", Stdout + File.ReadAllText(path));
    }

    [Fact]
    public void LeavesVerifyAloneWhenTheresNothingToExercise()
    {
        var warm = Documents.Verdict("observing").With("exercise_plan", Documents.ExercisePlan("warm_up"));
        Run(new FakeClient(new[] { warm }), "verify", "--format=text");
        Assert.DoesNotContain("To clear sooner", Stdout.ToString());
    }
}

public sealed class CiEnvironmentTests
{
    [Fact]
    public void ReadsGithubActions()
    {
        var found = CiEnvironment.Detect(new Dictionary<string, string>
        {
            ["GITHUB_ACTIONS"] = "true", ["GITHUB_SHA"] = "abc1234def", ["GITHUB_RUN_NUMBER"] = "123", ["GITHUB_RUN_ID"] = "9876",
            ["GITHUB_SERVER_URL"] = "https://github.com", ["GITHUB_REPOSITORY"] = "acme/shop",
        });
        Assert.Equal("""{"provider":"github_actions","commit":"abc1234def","version":"run-123","source_url":"https://github.com/acme/shop/actions/runs/9876"}""",
            found!.ToJson().ToJsonString());
    }

    [Fact]
    public void RegistersKamalsReleaseLinkedToTheCiRun()
    {
        Assert.Equal("""{"provider":"kamal","commit":"abc1234def","version":null,"source_url":null}""",
            CiEnvironment.Detect(new Dictionary<string, string> { ["KAMAL_VERSION"] = "abc1234def", ["KAMAL_COMMAND"] = "deploy" })!.ToJson().ToJsonString());
        var inCi = CiEnvironment.Detect(new Dictionary<string, string>
        {
            ["KAMAL_VERSION"] = "2026.10.01", ["GITHUB_ACTIONS"] = "true", ["GITHUB_SHA"] = "abc1234def", ["GITHUB_RUN_ID"] = "9876",
            ["GITHUB_SERVER_URL"] = "https://github.com", ["GITHUB_REPOSITORY"] = "acme/shop",
        });
        Assert.Equal("""{"provider":"kamal","commit":null,"version":"2026.10.01","source_url":"https://github.com/acme/shop/actions/runs/9876"}""",
            inCi!.ToJson().ToJsonString());
    }

    [Fact]
    public void RecognizesOtherCiAndNothingOutsideCi()
    {
        Assert.Equal("pipeline-45",
            CiEnvironment.Detect(new Dictionary<string, string> { ["GITLAB_CI"] = "true", ["CI_COMMIT_SHA"] = "a1", ["CI_PIPELINE_IID"] = "45" })!.Version);
        Assert.Equal("circleci",
            CiEnvironment.Detect(new Dictionary<string, string> { ["CIRCLECI"] = "true", ["CIRCLE_SHA1"] = "b2", ["CIRCLE_BUILD_NUM"] = "67" })!.Provider);
        Assert.Null(CiEnvironment.Detect(new Dictionary<string, string>
        {
            ["BUILDKITE"] = "true", ["BUILDKITE_COMMIT"] = "c3", ["BUILDKITE_BUILD_URL"] = "http://insecure",
        })!.SourceUrl);
        Assert.Null(CiEnvironment.Detect(new Dictionary<string, string>()));
    }
}
