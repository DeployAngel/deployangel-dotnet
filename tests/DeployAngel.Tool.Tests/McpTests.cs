using System.Text.Json.Nodes;

namespace DeployAngel.Tool.Tests;

public sealed class McpTests
{
    private readonly Clock _clock = new();
    private readonly FakeClient _fake;
    private readonly McpServer _server;

    public McpTests()
    {
        _fake = new FakeClient(new[] { Documents.Verdict("observing"), Documents.Verdict("closed", "verified") });
        _server = new McpServer(_fake, output: new StringWriter(), errorOutput: new StringWriter(), sleeper: _clock.Advance, clock: _clock.Read,
            gitHead: () => "81ac27d0000");
    }

    private static JsonObject Request(McpServer server, string method, JsonObject? parameters = null, int id = 1) =>
        server.Handle(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject() })!;

    private static JsonNode ResultOf(McpServer server, string method, JsonObject? parameters = null) => Request(server, method, parameters)["result"]!;

    private static JsonObject ToolCall(string name, JsonObject? arguments = null) =>
        new() { ["name"] = name, ["arguments"] = arguments ?? new JsonObject() };

    [Fact]
    public void NegotiatesTheProtocolVersion()
    {
        var result = ResultOf(_server, "initialize", new JsonObject { ["protocolVersion"] = "2025-03-26" });
        Assert.Equal("2025-03-26", (string?)result["protocolVersion"]);
        Assert.Equal("deployangel", (string?)result["serverInfo"]!["name"]);
        var instructions = (string)result["instructions"]!;
        foreach (var text in new[] { "inconclusive means NOT verified", "medium clears a release just as high does", "watching_closely", "get_exercise_plan", "warm_up" })
        {
            Assert.Contains(text, instructions);
        }
        Assert.Equal("2025-06-18", (string?)ResultOf(_server, "initialize", new JsonObject { ["protocolVersion"] = "1999-01-01" })["protocolVersion"]);
    }

    [Fact]
    public void ListsRegisterDeploymentOnlyForTokensWithTheScope()
    {
        List<string> Names() => ResultOf(_server, "tools/list")["tools"]!.AsArray().Select(tool => (string)tool!["name"]!).ToList();

        Assert.DoesNotContain("register_deployment", Names());
        _fake.Scopes = new List<string> { "verifications:read", "deployments" };
        _server.ForgetTokenScopes();
        var names = Names();
        foreach (var name in new[] { "register_deployment", "wait_for_verification", "get_exception", "get_exercise_plan" })
        {
            Assert.Contains(name, names);
        }
    }

    [Fact]
    public void ReturnsEveryToolsStructuredContentAsAnObject()
    {
        _fake.Scopes = new List<string> { "verifications:read", "deployments" };
        var calls = new Dictionary<string, JsonObject>
        {
            ["get_verification"] = new(), ["wait_for_verification"] = new() { ["timeout_seconds"] = 1 }, ["get_exercise_plan"] = new(),
            ["list_deployments"] = new(), ["get_exception"] = new() { ["fingerprint"] = "abc" }, ["list_late_regressions"] = new(),
            ["register_deployment"] = new() { ["commit"] = "abc1234" },
        };
        Assert.Equal(calls.Keys.OrderBy(name => name, StringComparer.Ordinal),
            ResultOf(_server, "tools/list")["tools"]!.AsArray().Select(tool => (string)tool!["name"]!).OrderBy(name => name, StringComparer.Ordinal));
        foreach (var (name, arguments) in calls)
        {
            var result = ResultOf(_server, "tools/call", ToolCall(name, arguments));
            Assert.True(result["structuredContent"] is JsonObject, $"{name} returned {result["structuredContent"]?.GetType().Name}");
        }
        var listed = ResultOf(_server, "tools/call", ToolCall("list_deployments"))["structuredContent"]!;
        Assert.Equal("""{"deployments":[{"id":42}]}""", listed.ToJsonString());
    }

    [Fact]
    public void WaitsForAVerdictWithTheExitCodesMeaning()
    {
        var result = ResultOf(_server, "tools/call", ToolCall("wait_for_verification", new JsonObject { ["until"] = "verdict" }));
        Assert.False((bool)result["isError"]!);
        Assert.Equal(0, (int)result["structuredContent"]!["exit_code"]!);
        Assert.Equal("verified: the release is cleared", (string?)result["structuredContent"]!["meaning"]);
        var text = JsonNode.Parse((string)result["content"]![0]!["text"]!)!;
        Assert.Equal("verified", (string?)text["verification"]!["verification"]!["verdict"]);
        Assert.Equal(new Call("deployments", """{"commit":"81ac27d0000","version":null,"limit":1}"""), _fake.Calls[0]);
    }

    [Fact]
    public void ReturnsAReleasesExercisePlan()
    {
        var client = new FakeClient(new[] { Documents.Verdict("observing").With("exercise_plan", Documents.ExercisePlan()) });
        var server = new McpServer(client, output: new StringWriter(), errorOutput: new StringWriter(), gitHead: () => "81ac27d0000");
        var tools = ResultOf(server, "tools/list")["tools"]!.AsArray();
        Assert.Contains("test account or ask first", (string)tools.First(tool => (string?)tool!["name"] == "get_exercise_plan")!["description"]!);
        var result = ResultOf(server, "tools/call", ToolCall("get_exercise_plan"));
        Assert.Equal("exercisable", (string?)result["structuredContent"]!["exercise_plan"]!["status"]);
        Assert.Equal("v184", (string?)result["structuredContent"]!["deployment"]!["version"]);
    }

    [Fact]
    public void CapsEachWaitAtFiveMinutes()
    {
        var stuck = new McpServer(new FakeClient(new[] { Documents.Verdict("observing") }), output: new StringWriter(), errorOutput: new StringWriter(),
            sleeper: _clock.Advance, clock: _clock.Read);
        var result = stuck.Handle(new JsonObject
        {
            ["id"] = 2, ["method"] = "tools/call",
            ["params"] = ToolCall("wait_for_verification", new JsonObject { ["latest"] = true, ["timeout_seconds"] = 9999 }),
        })!;
        Assert.Equal(3, (int)result["result"]!["structuredContent"]!["exit_code"]!);
        Assert.True((bool)result["result"]!["structuredContent"]!["in_progress"]!);
        Assert.Equal(Clock.Start + 300, _clock.Now);
    }

    [Fact]
    public void IgnoresNotificationsAndRejectsUnknownMethodsAndBadJson()
    {
        Assert.Null(_server.Handle(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }));
        Assert.Equal(-32601, (int)Request(_server, "resources/list")["error"]!["code"]!);
        Assert.Equal(-32700, (int)_server.HandleLine("{nope")!["error"]!["code"]!);
        Assert.Equal(-32602, (int)Request(_server, "tools/call", new JsonObject { ["name"] = "rollback" })["error"]!["code"]!);
    }

    [Fact]
    public void SpeaksOneJsonMessagePerLine()
    {
        var input = new StringReader("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}\n{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n");
        var output = new StringWriter();
        new McpServer(new FakeClient(), input, output, new StringWriter()).Run();
        Assert.Equal("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}\n", output.ToString());
    }

    // Unexpected failures go to stderr, never to the agent as a crash.
    [Fact]
    public void ReportsAnInternalErrorWithoutCrashing()
    {
        var errors = new StringWriter();
        var server = new McpServer(new FakeClient(Array.Empty<JsonObject>()), output: new StringWriter(), errorOutput: errors, gitHead: () => "abc1234");
        var response = Request(server, "tools/call", ToolCall("get_verification"));
        Assert.Equal(-32603, (int)response["error"]!["code"]!);
        Assert.StartsWith("deployangel mcp: ArgumentOutOfRangeException:", errors.ToString());
    }

    // Agents only report a server that won't start as "failed to start", so
    // without a token it starts anyway and each tool says how to set one.
    [Fact]
    public void StartsWithoutATokenFromTheCommandLineAndEachToolSaysHowToSetOne()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var requests = string.Join("\n", new[]
        {
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\"}}",
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}",
            "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"get_verification\",\"arguments\":{}}}",
        }) + "\n";
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = new Cli(new[] { "mcp" }, env: new Dictionary<string, string>(), stdin: new StringReader(requests), stdout: stdout,
            stderr: stderr, root: root, gitHead: () => null).Run();

        Assert.Equal(0, code);
        var responses = stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)!).ToDictionary(response => (int)response["id"]!);
        Assert.Equal("deployangel", (string)responses[1]["result"]!["serverInfo"]!["name"]!);
        Assert.Contains(responses[2]["result"]!["tools"]!.AsArray(), tool => (string)tool!["name"]! == "wait_for_verification");
        Assert.True((bool)responses[3]["result"]!["isError"]!);
        Assert.Contains("Set DEPLOYANGEL_API_TOKEN to a \"CLI & coding agents\" token", (string)responses[3]["result"]!["content"]![0]!["text"]!);
        Assert.Contains("no API token yet", stderr.ToString());
    }
}
