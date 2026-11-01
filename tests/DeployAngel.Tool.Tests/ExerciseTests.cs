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

    private readonly List<IReadOnlyDictionary<string, string>> _sentHeaders = new();

    private (int Code, List<string> Sent) Exercise(FakeClient client, Func<string, int?> answer, params string[] argv) =>
        Exercise(client, answer, new Dictionary<string, string>(), argv);

    private (int Code, List<string> Sent) Exercise(FakeClient client, Func<string, int?> answer, Dictionary<string, string> env, params string[] argv)
    {
        var sent = new List<string>();
        var code = new Cli(new[] { "exercise" }.Concat(argv), env: env, stdout: Stdout, stderr: Stderr, client: client,
            sleeper: Clock.Advance, clock: Clock.Read, gitHead: () => Head, root: Root.Path, requester: (url, headers) =>
            {
                sent.Add(url);
                _sentHeaders.Add(headers);
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
                     "Sent 18 requests to https://shop.example.com/", "GET /products ×10: 200 ×10", "POST /password_resets (changes data)",
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
    public void SaysWhenNoPageAnsweredAndRecordsNothing()
    {
        var client = new FakeClient(new[] { Document() });
        var (code, sent) = Exercise(client, _ => 404, "--url=https://www.shop.example.com");
        Assert.Equal(5, code);
        Assert.Equal(2, sent.Count);
        var output = Stdout.ToString();
        foreach (var text in new[]
                 {
                     "GET /products ×1: 404", "No page answered at https://www.shop.example.com", "Is that the app's own host",
                     "16 more requests weren't sent", "Nothing was recorded on the release.",
                 })
        {
            Assert.Contains(text, output);
        }
        Assert.DoesNotContain(client.Calls, call => call.Name == "exercise");
    }

    [Fact]
    public void RecordsPagesThatTurnedTheRequestAway()
    {
        var client = new FakeClient(new[] { Document() });
        var (code, _) = Exercise(client, _ => 401, "--url=https://shop.example.com");
        Assert.Equal(0, code);
        Assert.Contains("GET /products ×10: 401 ×10", Stdout.ToString());
        Assert.DoesNotContain("No page answered", Stdout.ToString());
        Assert.Contains(client.Calls, call => call.Name == "exercise");
    }

    [Fact]
    public void SendsHeadersNamingThemButNeverShowingValues()
    {
        var client = new FakeClient(new[] { Document() });
        var env = new Dictionary<string, string> { ["DEPLOYANGEL_EXERCISE_HEADERS"] = "Cookie: session_id=s3cret\n\nX-Test: 1\n" };
        var (code, _) = Exercise(client, _ => 200, env, "--url=https://shop.example.com", "--header=Authorization: Bearer t0ken");
        Assert.Equal(0, code);
        Assert.All(_sentHeaders, headers => Assert.Equal(
            new Dictionary<string, string> { ["Authorization"] = "Bearer t0ken", ["Cookie"] = "session_id=s3cret", ["X-Test"] = "1" }, headers));
        var output = Stdout.ToString();
        Assert.Contains("With headers: Authorization, Cookie, X-Test (values not shown)", output);
        Assert.DoesNotContain("t0ken", output);
        Assert.DoesNotContain("s3cret", output);
        Assert.DoesNotContain(client.Calls, call => call.Args.Contains("t0ken", StringComparison.Ordinal) || call.Args.Contains("s3cret", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsAMalformedHeaderWithoutEchoingIt()
    {
        var (code, sent) = Exercise(new FakeClient(new[] { Document() }), _ => 200, "--url=https://shop.example.com", "--header=Authorization Bearer t0ken");
        Assert.Equal(5, code);
        Assert.Empty(sent);
        Assert.Contains("header 1 must look like \"Name: value\"", Stderr.ToString());
        Assert.DoesNotContain("t0ken", Stderr.ToString());
    }

    [Fact]
    public void FillsPathParametersAndNamesWhatsMissing()
    {
        var client = new FakeClient(new[] { Document() });
        var (code, sent) = Exercise(client, _ => 200, "--url=https://shop.example.com", "--param=id=7");
        Assert.Equal(0, code);
        Assert.Contains("https://shop.example.com/orders/7", sent);
        var recorded = JsonNode.Parse(client.Calls.Single(call => call.Name == "exercise").Args)!;
        Assert.Contains("GET /orders/{id}", recorded["routes"]!.AsArray().Select(route => (string?)route!["key"]));
        Assert.DoesNotContain("missing", recorded["skipped"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("GET /items/{itemId} (needs a path parameter: itemId)", Stdout.ToString());
        Assert.Contains("Fill path parameters with --param, such as --param itemId=42.", Stdout.ToString());

        (code, _) = Exercise(client, _ => 200, "--url=https://shop.example.com", "--param=id");
        Assert.Equal(5, code);
        Assert.Contains("--param must look like name=value", Stderr.ToString());
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
