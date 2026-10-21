using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using static DeployAngel.Tool.JsonValues;

namespace DeployAngel.Tool;

/// <summary>Bad tool arguments: answered with JSON-RPC's invalid params error, never as a crash.</summary>
internal sealed class InvalidParamsException : Exception
{
    public InvalidParamsException(string message) : base(message)
    {
    }
}

/// <summary>
/// A Model Context Protocol server over stdio (JSON-RPC 2.0, one message per
/// line), so coding agents can ask whether their release passed production.
/// stdout carries only protocol messages. Same tools as the Ruby gem's and
/// the Python package's servers, hand-rolled like theirs so the tool has no
/// dependencies.
///
/// The tools are read-only with respect to production: nothing here rolls
/// back, restarts, or changes customer infrastructure.
/// </summary>
internal sealed class McpServer
{
    public const int MaxWaitSeconds = 300;

    public static readonly string[] ProtocolVersions = { "2025-06-18", "2025-03-26", "2024-11-05" };

    public const string Instructions =
        "DeployAngel verifies deployments in production. After deploying, call wait_for_verification\n" +
        "with until \"initial\" (about 15 minutes) or \"verdict\". Verdicts: verified means cleared, so you may\n" +
        "report the release as successful. inconclusive means NOT verified: never report it as success.\n" +
        "failed means production regressed: read the findings and exceptions and investigate.\n" +
        "Confidence medium clears a release just as high does (high also needs 80% coverage and a full\n" +
        "baseline): never call a medium clearance weak. suspicious true means warnings DeployAngel is\n" +
        "watching closely, not a failure: report them as \"watching closely:\" and the watching_closely items,\n" +
        "never as \"suspicious\".\n" +
        "Findings are deterministic evidence; any \"investigation\" field is AI inference. These tools cannot\n" +
        "change production. Do not roll back or change production without explicit approval.\n" +
        "If a release isn't cleared yet, get_exercise_plan says what to exercise against production so it\n" +
        "clears sooner. Items with needed true are what clearance waits on; the rest are only worth running.\n" +
        "Only act on status \"exercisable\" or \"waiting_for_activity\"; for \"warm_up\" or\n" +
        "\"no_baseline\" nothing you run can clear it. Exercise routes marked mutating only with a test account\n" +
        "or after asking. Report what you ran with the plan's report_with command (deployangel check).\n";

    private const int ParseError = -32700;
    private const int InvalidRequest = -32600;
    private const int MethodNotFound = -32601;
    private const int InvalidParams = -32602;
    private const int InternalError = -32603;

    private static readonly Dictionary<int, string> Meanings = new()
    {
        [0] = "verified: the release is cleared",
        [1] = "failed: production regressed",
        [2] = "inconclusive: NOT verified; do not report success",
        [3] = "still in progress; call wait_for_verification again",
        [6] = "initial check: no problems so far, NOT cleared yet",
        [7] = "initial check: warnings present, NOT cleared",
    };

    private readonly IApiClient _client;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly TextWriter _errorOutput;
    private readonly Action<double> _sleeper;
    private readonly Func<double> _clock;
    private readonly Func<string?> _gitHead;
    private readonly bool _tokenMissing;
    private List<string>? _scopes;

    internal const string MissingToken = "DeployAngel has no API token yet. Set DEPLOYANGEL_API_TOKEN to a \"CLI & coding agents\" token " +
        "from your app's Settings in DeployAngel, or run `deployangel setup` in the project, then restart this MCP server.";

    public McpServer(IApiClient client, TextReader? input = null, TextWriter? output = null, TextWriter? errorOutput = null,
        Action<double>? sleeper = null, Func<double>? clock = null, Func<string?>? gitHead = null, bool tokenMissing = false)
    {
        _client = client;
        _tokenMissing = tokenMissing;
        _input = input ?? Console.In;
        _output = output ?? Console.Out;
        _errorOutput = errorOutput ?? Console.Error;
        _sleeper = sleeper ?? Cli.Sleep;
        _clock = clock ?? Cli.Now;
        _gitHead = gitHead ?? (() => null);
    }

    public void Run()
    {
        string? line;
        while ((line = _input.ReadLine()) is not null)
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }
            var response = HandleLine(line);
            if (response is null)
            {
                continue;
            }
            _output.Write(OneLine(response) + "\n");
            _output.Flush();
        }
    }

    public JsonObject? HandleLine(string line)
    {
        JsonNode? message;
        try
        {
            message = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            return Error(null, ParseError, "invalid JSON");
        }
        if (message is not JsonObject request)
        {
            return Error(null, InvalidRequest, "expected a JSON object");
        }
        return Handle(request);
    }

    public JsonObject? Handle(JsonObject message)
    {
        var id = Get(message, "id");
        var method = Get(message, "method");
        if (id is null)
        {
            // Notifications, including notifications/initialized.
            return null;
        }
        try
        {
            var parameters = Arguments(Get(message, "params"), "params");
            switch (StringOf(method))
            {
                case "initialize":
                    return Result(id, InitializeResult(StringOf(Get(parameters, "protocolVersion"))));
                case "ping":
                    return Result(id, new JsonObject());
                case "tools/list":
                    return Result(id, new JsonObject { ["tools"] = Tools() });
                case "tools/call":
                    return Result(id, CallTool(Get(parameters, "name"), Arguments(Get(parameters, "arguments"), "arguments")));
                default:
                    return Error(id, MethodNotFound, $"unknown method: {Text(method)}");
            }
        }
        catch (InvalidParamsException error)
        {
            return Error(id, InvalidParams, error.Message);
        }
        catch (Exception error)
        {
            // Reported to stderr, never to the agent as a crash.
            _errorOutput.Write($"deployangel mcp: {error.GetType().Name}: {error.Message}\n");
            return Error(id, InternalError, "internal error");
        }
    }

    public JsonArray Tools()
    {
        var tools = new JsonArray
        {
            Tool("get_verification", "Current verdict document for a deployment: state, verdict, confidence, coverage, " +
                                     "findings, new exceptions, clearance report, and what is still watched. Defaults to the current git HEAD.",
                TargetProperties()),
            Tool("wait_for_verification", "Wait for a deployment's verification. until=initial returns at the 15-minute " +
                                          "initial check (\"no problems so far\" is NOT clearance); until=verdict waits for verified, failed, or " +
                                          "inconclusive. Returns the in-progress document if timeout_seconds (max 300) is reached; call again " +
                                          "while it is still in progress. Inconclusive means not verified.",
                With(TargetProperties(),
                    ("until", new JsonObject { ["type"] = "string", ["enum"] = Strings("initial", "verdict"), ["default"] = "initial" }),
                    ("timeout_seconds", new JsonObject
                    {
                        ["type"] = "integer", ["minimum"] = 1, ["maximum"] = MaxWaitSeconds, ["default"] = MaxWaitSeconds,
                    }))),
            Tool("get_exercise_plan", "What stands between a release and clearance, and what to exercise against " +
                                      "production so it clears sooner: normally active routes short of their runs, routes this release changed " +
                                      "that haven't run, and critical flows. needed marks what clearance waits on; the rest are only worth running. " +
                                      "Routes marked mutating change data: use a test account or ask first. " +
                                      "Your requests count as ordinary traffic; report what you ran with report_with. Defaults to the current git HEAD.",
                TargetProperties()),
            Tool("list_deployments", "Recent deployments with their verification state and verdict.",
                new JsonObject { ["limit"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 50, ["default"] = 10 } }),
            Tool("get_exception", "Sanitized details and the application stack trace for an exception fingerprint.",
                new JsonObject { ["fingerprint"] = new JsonObject { ["type"] = "string" } }, "fingerprint"),
            Tool("list_late_regressions", "Failures found after a release was cleared, on paths the clearance did not cover.",
                new JsonObject
                {
                    ["since"] = new JsonObject { ["type"] = "string", ["description"] = "ISO 8601 time" },
                    ["limit"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 50, ["default"] = 10 },
                }),
        };
        if (TokenScopes().Contains("deployments"))
        {
            tools.Add(Tool("register_deployment", "Register a deployment so it is verified (manual and CI deploys).",
                new JsonObject
                {
                    ["commit"] = new JsonObject { ["type"] = "string" },
                    ["version"] = new JsonObject { ["type"] = "string" },
                    ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = Strings("code", "config", "rollback", "promotion") },
                }));
        }
        return tools;
    }

    /// <summary>Forgets the token's scopes, so the next tools/list asks again.</summary>
    internal void ForgetTokenScopes() => _scopes = null;

    private static JsonObject InitializeResult(string? requested) => new()
    {
        ["protocolVersion"] = requested is not null && ProtocolVersions.Contains(requested) ? requested : ProtocolVersions[0],
        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
        ["serverInfo"] = new JsonObject { ["name"] = "deployangel", ["version"] = ToolInfo.Version },
        ["instructions"] = Instructions,
    };

    private JsonObject CallTool(JsonNode? name, JsonObject arguments)
    {
        if (_tokenMissing)
        {
            return new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = MissingToken }),
                ["isError"] = true,
            };
        }
        JsonNode content;
        try
        {
            switch (StringOf(name))
            {
                case "get_verification":
                    content = VerificationContent(Waiter().Wait(TargetOf(arguments), wait: false));
                    break;
                case "wait_for_verification":
                    content = WaitContent(arguments);
                    break;
                case "get_exercise_plan":
                    content = PlanContent(Waiter().Wait(TargetOf(arguments), wait: false));
                    break;
                // MCP requires structured content to be an object, never a bare list.
                case "list_deployments":
                    content = new JsonObject { ["deployments"] = Copy(_client.Deployments(limit: Limit(arguments))) };
                    break;
                case "get_exception":
                    if (!arguments.ContainsKey("fingerprint"))
                    {
                        throw new InvalidParamsException("fingerprint is required");
                    }
                    content = _client.Exception(Text(arguments["fingerprint"]));
                    break;
                case "list_late_regressions":
                    var since = Get(arguments, "since");
                    content = new JsonObject
                    {
                        ["late_regressions"] = Copy(_client.LateRegressions(since: since is null ? null : Text(since), limit: Limit(arguments))),
                    };
                    break;
                case "register_deployment":
                    if (!TokenScopes().Contains("deployments"))
                    {
                        throw new InvalidParamsException("register_deployment is not available for this token");
                    }
                    content = _client.RegisterDeployment(commit: Optional(arguments, "commit"), version: Optional(arguments, "version"),
                        kind: Optional(arguments, "kind"));
                    break;
                default:
                    throw new InvalidParamsException($"unknown tool: {Text(name)}");
            }
        }
        catch (ApiException error)
        {
            return new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = $"DeployAngel API error: {error.Message}" }),
                ["isError"] = true,
            };
        }
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = Pretty(content) }),
            ["structuredContent"] = Copy(content),
            ["isError"] = false,
        };
    }

    private JsonObject WaitContent(JsonObject arguments)
    {
        var untilMode = arguments.ContainsKey("until") ? StringOf(arguments["until"]) : "initial";
        if (untilMode is not ("initial" or "verdict"))
        {
            throw new InvalidParamsException("until must be initial or verdict");
        }
        var timeout = arguments.ContainsKey("timeout_seconds") ? IntegerArgument(arguments["timeout_seconds"], "timeout_seconds") : MaxWaitSeconds;
        timeout = Math.Max(1, Math.Min(MaxWaitSeconds, timeout));
        return VerificationContent(Waiter().Wait(TargetOf(arguments), untilMode: untilMode, timeout: timeout));
    }

    private static JsonObject VerificationContent(Outcome outcome)
    {
        if (outcome.NotFound)
        {
            return new JsonObject { ["found"] = false, ["note"] = "No matching deployment is registered yet." };
        }
        return new JsonObject
        {
            ["exit_code"] = outcome.ExitCode,
            ["meaning"] = Meanings.TryGetValue(outcome.ExitCode, out var meaning) ? meaning : "unknown",
            ["in_progress"] = outcome.ExitCode == ExitCodes.TimedOut,
            ["verification"] = Copy(outcome.Document),
        };
    }

    private static JsonObject PlanContent(Outcome outcome)
    {
        if (outcome.NotFound)
        {
            return new JsonObject { ["found"] = false, ["note"] = "No matching deployment is registered yet." };
        }
        var found = Get(outcome.Document, "exercise_plan");
        var plan = Truthy(found)
            ? Copy(found)
            : new JsonObject { ["note"] = "This DeployAngel server doesn't return exercise plans yet." };
        return new JsonObject { ["deployment"] = Copy(Get(outcome.Document, "deployment")), ["exercise_plan"] = plan };
    }

    private Target TargetOf(JsonObject arguments)
    {
        if (Truthy(Get(arguments, "deployment_id")))
        {
            return new Target(DeploymentId: Text(arguments["deployment_id"]));
        }
        if (Truthy(Get(arguments, "version")))
        {
            return new Target(Version: Text(arguments["version"]));
        }
        if (Truthy(Get(arguments, "commit")))
        {
            return new Target(Commit: Text(arguments["commit"]).ToLowerInvariant());
        }
        if (Truthy(Get(arguments, "latest")))
        {
            return Target.LatestDeployment;
        }
        var head = _gitHead();
        return !string.IsNullOrEmpty(head) ? new Target(Commit: head.ToLowerInvariant()) : Target.LatestDeployment;
    }

    private Waiter Waiter() => new(_client, _sleeper, _clock);

    private List<string> TokenScopes()
    {
        if (_scopes is null)
        {
            try
            {
                _scopes = ItemsOf(Get(_client.TokenInfo(), "scopes")).Select(Text).ToList();
            }
            catch (ApiException)
            {
                _scopes = new List<string>();
            }
        }
        return _scopes;
    }

    /// <summary>params and arguments: an object, or nothing at all.</summary>
    private static JsonObject Arguments(JsonNode? node, string name) => node switch
    {
        JsonObject obj => obj,
        _ when !Truthy(node) => new JsonObject(),
        _ => throw new InvalidParamsException($"{name} must be an object"),
    };

    private static int? Limit(JsonObject arguments) =>
        arguments.ContainsKey("limit") ? arguments["limit"] is null ? null : IntegerArgument(arguments["limit"], "limit") : 10;

    private static int IntegerArgument(JsonNode? value, string name)
    {
        try
        {
            return (int)Math.Clamp(ToInteger(value), int.MinValue, int.MaxValue);
        }
        catch (FormatException)
        {
            throw new InvalidParamsException($"{name} must be an integer");
        }
    }

    private static string? Optional(JsonObject arguments, string key) => Get(arguments, key) is { } value ? Text(value) : null;

    private static JsonObject TargetProperties() => new()
    {
        ["commit"] = new JsonObject { ["type"] = "string", ["description"] = "Commit SHA (prefix of 7+ characters is fine)." },
        ["version"] = new JsonObject { ["type"] = "string", ["description"] = "Release version, e.g. v184." },
        ["deployment_id"] = new JsonObject { ["type"] = "string", ["description"] = "DeployAngel deployment ID." },
        ["latest"] = new JsonObject { ["type"] = "boolean", ["description"] = "Use the most recent deployment." },
    };

    private static JsonObject With(JsonObject properties, params (string Key, JsonObject Schema)[] more)
    {
        foreach (var (key, schema) in more)
        {
            properties[key] = schema;
        }
        return properties;
    }

    private static JsonArray Strings(params string[] values) => new(values.Select(value => (JsonNode?)value).ToArray());

    private static JsonObject Tool(string name, string description, JsonObject properties, params string[] required) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = Strings(required) },
    };

    private static JsonObject Result(JsonNode id, JsonNode payload) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = Copy(id),
        ["result"] = payload,
    };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = Copy(id),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };
}
