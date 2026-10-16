using System.Text.Json.Nodes;

namespace DeployAngel.Tool.Tests;

public sealed class ExerciseTests : CliTestBase
{
    private static JsonObject Document()
    {
        var plan = Documents.ExercisePlan();
        var items = plan["items"]!.AsArray();
        items.Add(Documents.Json("""{"kind": "route", "key": "GET /products", "runs": 1, "runs_needed": 3, "mutating": false}"""));
        items.Add(Documents.Json("""{"kind": "route", "key": "GET /about", "runs": 0, "mutating": false}"""));
        items.Add(Documents.Json("""{"kind": "route", "key": "GET /items/{itemId}", "runs": 0, "mutating": false}"""));
        return Documents.Verdict("observing").With("exercise_plan", plan);
    }

    private (int Code, List<string> Sent) Exercise(FakeClient client, Func<string, int?> answer, params string[] argv)
    {
        var sent = new List<string>();
        var code = new Cli(new[] { "exercise" }.Concat(argv), env: new Dictionary<string, string>(), stdout: Stdout, stderr: Stderr, client: client,
            sleeper: Clock.Advance, clock: Clock.Read, gitHead: () => Head, root: Root.Path, requester: url =>
            {
                sent.Add(url);
                return answer(url);
            }).Run();
        return (code, sent);
    }

    private static Dictionary<string, int> Tally(List<string> sent) => sent.GroupBy(url => url).ToDictionary(group => group.Key, group => group.Count());

    [Fact]
    public void SendsThePlansReadOnlyRequestsSpreadsTheShortfallAndRecordsThem()
    {
        var client = new FakeClient(new[] { Document() });
        var (code, sent) = Exercise(client, _ => 200, "--url=https://shop.example.com/");
        Assert.Equal(0, code);
        // 12 of 30 requests: 18 more, 2 + 1 of them by the routes' own runs needed.
        Assert.Equal(new Dictionary<string, int> { ["https://shop.example.com/products"] = 10, ["https://shop.example.com/about"] = 8 }, Tally(sent));
        var recorded = JsonNode.Parse(client.Calls.Single(call => call.Name == "exercise").Args)!;
        Assert.Equal("42", (string?)recorded["deployment_id"]);
        Assert.Equal("""[{"key":"GET /products","requests":10,"statuses":{"2xx":10}},{"key":"GET /about","requests":8,"statuses":{"2xx":8}}]""",
            recorded["routes"]!.ToJsonString());
        Assert.Equal("""[{"key":"GET /orders/{id}","reason":"needs a path parameter"},{"key":"POST /password_resets","reason":"changes data"},{"key":"GET /items/{itemId}","reason":"needs a path parameter"}]""",
            recorded["skipped"]!.ToJsonString());
        // Sent at about 5 a second: 17 pauses after the first request.
        Assert.Equal("2026-09-30T14:00:03Z", (string?)recorded["ran_at"]);
        var output = Stdout.ToString();
        foreach (var text in new[]
                 {
                     "Sent 18 requests to https://shop.example.com/", "GET /products ×10: 10 2xx", "POST /password_resets (changes data)",
                     "Recorded on the release.",
                 })
        {
            Assert.Contains(text, output);
        }
    }

    [Fact]
    public void StopsRequestingAPageTheAppDoesntServe()
    {
        var client = new FakeClient(new[] { Document() });
        var (_, sent) = Exercise(client, url => url.EndsWith("/about", StringComparison.Ordinal) ? 404 : 200, "--url=https://shop.example.com");
        Assert.Equal(new Dictionary<string, int> { ["https://shop.example.com/products"] = 17, ["https://shop.example.com/about"] = 1 }, Tally(sent));
        var routes = JsonNode.Parse(client.Calls.Single(call => call.Name == "exercise").Args)!["routes"]!;
        Assert.Equal("""[{"key":"GET /products","requests":17,"statuses":{"2xx":17}},{"key":"GET /about","requests":1,"statuses":{"4xx":1}}]""",
            routes.ToJsonString());
    }

    [Fact]
    public void SendsNothingOnADryRunAndCapsRequests()
    {
        var client = new FakeClient(new[] { Document() });
        var (code, sent) = Exercise(client, _ => 200, "--url=https://shop.example.com", "--dry-run", "--max-requests=5");
        Assert.Equal(0, code);
        Assert.Empty(sent);
        Assert.DoesNotContain(client.Calls, call => call.Name == "exercise");
        Assert.Contains("Would send 5 requests:", Stdout.ToString());
    }

    [Fact]
    public void StopsWhenTheAppDoesntAnswer()
    {
        var (code, sent) = Exercise(new FakeClient(new[] { Document() }), _ => null, "--url=https://shop.example.com");
        Assert.Equal(5, code);
        Assert.Equal(Exerciser.MaxConsecutiveErrors, sent.Count);
        Assert.Contains("Stopped early: https://shop.example.com isn't answering.", Stdout.ToString());
    }

    [Fact]
    public void SendsNothingWithoutAPlanAndNeedsTheUrl()
    {
        var cleared = Documents.Verdict("closed", "verified")
            .With("exercise_plan", new JsonObject { ["status"] = "nothing_needed", ["summary"] = "Cleared." });
        var (code, sent) = Exercise(new FakeClient(new[] { cleared }), _ => 200, "--url=https://shop.example.com");
        Assert.Equal(0, code);
        Assert.Empty(sent);
        Assert.Contains("Nothing to exercise. Cleared.", Stdout.ToString());

        (code, _) = Exercise(new FakeClient(new[] { Document() }), _ => 200, "--url=shop.example.com");
        Assert.Equal(5, code);
        Assert.Contains("exercise needs --url", Stderr.ToString());
    }
}
