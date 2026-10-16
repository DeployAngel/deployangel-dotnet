using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;

namespace DeployAngel.Tool;

/// <summary>
/// The <c>deployangel</c> command for developers, CI, and coding agents:
/// register deploys, wait for verdicts, say what to exercise, report checks,
/// and run an MCP server. It talks only to DeployAngel's API, never to the
/// app, and never starts the agent. Behaves like the Ruby gem's and the
/// Python package's command: same options, output, and exit codes.
/// </summary>
internal sealed class Cli
{
    public static readonly string Help = $@"Usage: deployangel <command> [options]

  release    Register a deployment          [--commit=SHA] [--version=V] [--kind=code]
                                            [--provider=P] [--url=RUN_URL]
  verify     Report or wait for a verdict   [--commit=SHA | --version=V | --deployment=ID]
                                            [--wait] [--until=initial|verdict|closed] [--timeout=30m]
                                            [--format=text|json] [--all-findings]
  status     Latest deployment and its verification
  plan       What to exercise so a release  [--commit=SHA | --version=V | --deployment=ID]
             clears sooner                  [--format=text|json]
  exercise   Send the plan's read-only      --url=PRODUCTION_URL [--commit=SHA | --version=V | --deployment=ID]
             requests to production         [--max-requests=200] [--dry-run]
  exception  Details for a fingerprint      deployangel exception FINGERPRINT
  check      Report a smoke test result     --name=NAME --status=pass|fail [--covers=a,b]
                                            [--commit=SHA | --deployment=ID] [--details-url=URL]
  install    Add a Kamal deploy hook,       deployangel install kamal
             bake the commit into an image, deployangel install docker
             or set up coding agents        deployangel install agents
  doctor     Check the setup: this checkout  [--format=text|json]
             and what DeployAngel has seen
  setup      Redeem a one-time setup code   deployangel setup --code=CODE
             and save the app's tokens here, without printing them
  token      Print a saved token, to pipe   deployangel token app|api
             into a host's or CI's secrets
  mcp        Run the MCP server over stdio (for coding agents)

With no target, release, verify, plan, and check use the current git HEAD commit.
--format defaults to text in a terminal and json otherwise, such as in a pipe or an agent.
In GitHub Actions, GitLab CI, CircleCI, and Buildkite, release fills in the
commit, a build label (e.g. run-123), and a link to the run automatically.
In a Kamal hook, it registers Kamal's release (KAMAL_VERSION).
Token: DEPLOYANGEL_API_TOKEN, else the one `deployangel setup` saved in .deployangel/.
Environment: DEPLOYANGEL_URL (default {ToolInfo.DefaultEndpoint})

Exit codes: 0 verified, 1 failed, 2 inconclusive, 3 still in progress / timed out,
            4 deployment not found, 5 usage, auth, or network error,
            6 initial check: no problems so far (NOT cleared), 7 initial check: warnings (NOT cleared)".Replace("\r\n", "\n", StringComparison.Ordinal);

    private const string NoTarget = "no target: pass --commit, --version, or --deployment, or run inside a git repository";

    private static readonly Regex Duration = new(@"\A(\d+)(s|m|h)?\z", RegexOptions.CultureInvariant);
    private static readonly Regex ProductionUrl = new(@"\Ahttps?://[^/\s]+", RegexOptions.CultureInvariant);

    private readonly List<string> _argv;
    private readonly IReadOnlyDictionary<string, string> _env;
    private readonly TextReader _stdin;
    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;
    private readonly bool _stdoutIsTerminal;
    private readonly Action<double> _sleeper;
    private readonly Func<double> _clock;
    private readonly Func<string?> _gitHead;
    private readonly string _root;
    private readonly Func<string, int?>? _requester;
    private readonly IApiClient? _injectedClient;
    private IApiClient? _client;
    private JsonObject? _savedCredentials;

    /// <param name="client">Used for every request instead of one built from the token.</param>
    /// <param name="gitHead">The commit HEAD names; null for none. Defaults to asking git.</param>
    /// <param name="requester">Sends one exercise request and returns its status, or null for no answer.</param>
    /// <param name="stdoutIsTerminal">Whether output goes to a person, which makes text the default format.</param>
    public Cli(IEnumerable<string> argv, IReadOnlyDictionary<string, string>? env = null, TextReader? stdin = null,
        TextWriter? stdout = null, TextWriter? stderr = null, IApiClient? client = null, Action<double>? sleeper = null,
        Func<double>? clock = null, Func<string?>? gitHead = null, string? root = null, Func<string, int?>? requester = null,
        bool? stdoutIsTerminal = null)
    {
        _argv = argv.ToList();
        _env = env ?? ProcessEnvironment();
        _stdin = stdin ?? Console.In;
        _stdout = stdout ?? Console.Out;
        _stderr = stderr ?? Console.Error;
        _stdoutIsTerminal = stdoutIsTerminal ?? (stdout is null && !Console.IsOutputRedirected);
        _injectedClient = client;
        _client = client;
        _sleeper = sleeper ?? Sleep;
        _clock = clock ?? Now;
        _root = root ?? Directory.GetCurrentDirectory();
        _gitHead = gitHead ?? (() => GitRevParseHead(_root));
        _requester = requester;
    }

    public static void Sleep(double seconds) => Thread.Sleep(TimeSpan.FromSeconds(seconds));

    public static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    public int Run()
    {
        var command = _argv.Count > 0 ? Shift() : null;
        try
        {
            switch (command)
            {
                case "release":
                    return Release();
                case "verify":
                    return Verify();
                case "status":
                    return Verify(status: true);
                case "plan":
                    return Plan();
                case "exercise":
                    return Exercise();
                case "exception":
                    return ExceptionDetails();
                case "check":
                    return Check();
                case "install":
                    return new Installer(_root, Print, Error).Run(_argv.Count > 0 ? Shift() : null);
                case "doctor":
                    return Doctor();
                case "setup":
                    return Setup();
                case "token":
                    return Token();
                case "mcp":
                    return Mcp();
                case "version" or "--version" or "-v":
                    Print(ToolInfo.Version);
                    return 0;
                case null or "help" or "--help" or "-h":
                    Print(Help);
                    return 0;
                default:
                    return UsageError($"unknown command: {command}");
            }
        }
        catch (UsageException error)
        {
            return UsageError(error.Message);
        }
        catch (UnauthorizedException error)
        {
            Error(error.Message);
            return ExitCodes.Usage;
        }
        catch (NotFoundException error)
        {
            Error(error.Message);
            return ExitCodes.NotFound;
        }
        catch (ApiException error)
        {
            Error(error.Message);
            return ExitCodes.Usage;
        }
    }

    private int Release()
    {
        var options = Parse(new OptionParser().String("--commit").String("--version").String("--kind").String("--provider")
            .String("--url", "source_url"));
        var commit = options["commit"];
        var version = options["version"];
        var provider = options["provider"];
        var sourceUrl = options["source_url"];
        // Explicit options win, then the CI system, then git.
        var detected = CiEnvironment.Detect(_env);
        if (detected is not null)
        {
            commit = Or(commit, detected.Commit);
            version = Or(version, detected.Version);
            provider = Or(provider, detected.Provider);
            sourceUrl = Or(sourceUrl, detected.SourceUrl);
        }
        commit = Or(commit, GitHead());
        if (string.IsNullOrEmpty(commit) && string.IsNullOrEmpty(version))
        {
            return UsageError("release needs --commit or --version (no git repository or CI commit found)");
        }
        var deployment = Client().RegisterDeployment(commit, version, options["kind"], provider, sourceUrl);
        var name = JsonValues.Or(JsonValues.Get(deployment, "version"), JsonValues.Get(deployment, "commit"));
        Print($"Registered deployment {JsonValues.Text(JsonValues.Get(deployment, "id"))} " +
              $"({JsonValues.Text(name)}), verification {JsonValues.Text(JsonValues.Get(deployment, "state"))}");
        return 0;
    }

    private int Verify(bool status = false)
    {
        var options = Parse(TargetOptions()
            .Flag("--wait")
            .String("--until", "until_mode", "verdict", Waiter.UntilModes)
            .String("--timeout", defaultValue: "1800")
            .String("--format", choices: new[] { "text", "json" })
            .Flag("--all-findings"));
        var timeout = ParseDuration(options["timeout"]!);
        var target = status ? Target.LatestDeployment : TargetFrom(options);
        if (target is null)
        {
            return UsageError(NoTarget);
        }

        var wait = options.Flag("wait");
        Action<string>? progress = wait ? message => _stderr.Write(message + "\n") : null;
        var waiter = new Waiter(Client(), _sleeper, _clock, progress);
        var outcome = waiter.Wait(target, options["until_mode"]!, timeout, wait);
        if (outcome.NotFound)
        {
            Error($"no deployment found for {target.Value}");
            StepSummary($"### DeployAngel: no deployment found for {target.Value}\n");
            return outcome.ExitCode;
        }

        var document = outcome.Document!;
        if (options.Flag("all_findings"))
        {
            document = Client().Verification(JsonValues.Text(JsonValues.Get(JsonValues.Get(document, "deployment"), "id")), allFindings: true);
        }
        Output(document, options["format"], () => Formatter.Verification(document));
        StepSummary(Formatter.Markdown(document));
        if (outcome.TimedOut)
        {
            Error("timed out; verification is still in progress");
        }
        return outcome.ExitCode;
    }

    /// <summary>
    /// The release's exercise plan: what stands between it and clearance,
    /// and what to exercise against production so it clears sooner.
    /// </summary>
    private int Plan()
    {
        var options = Parse(TargetOptions().String("--format", choices: new[] { "text", "json" }));
        var target = TargetFrom(options);
        if (target is null)
        {
            return UsageError(NoTarget);
        }

        var outcome = new Waiter(Client(), _sleeper, _clock).Wait(target, wait: false);
        if (outcome.NotFound)
        {
            Error($"no deployment found for {target.Value}");
            return outcome.ExitCode;
        }
        var document = outcome.Document!;
        var data = new JsonObject
        {
            ["deployment"] = JsonValues.Copy(JsonValues.Get(document, "deployment")),
            ["exercise_plan"] = JsonValues.Copy(JsonValues.Get(document, "exercise_plan")),
        };
        Output(data, options["format"], () => Formatter.ExercisePlan(document));
        return 0;
    }

    /// <summary>
    /// Sends the plan's read-only requests from here, then records what it
    /// sent on the release (spec §16, Exercise records).
    /// </summary>
    private int Exercise()
    {
        var options = Parse(new OptionParser().String("--url").String("--commit").String("--version").String("--deployment", "deployment_id")
            .Integer("--max-requests", "max_requests", Exerciser.MaxRequests)
            .Flag("--dry-run", "dry_run"));
        var url = options["url"] ?? "";
        if (!ProductionUrl.IsMatch(url))
        {
            return UsageError("exercise needs --url, the app's production URL, like https://example.com");
        }
        var target = TargetFrom(options);
        if (target is null)
        {
            return UsageError(NoTarget);
        }

        var outcome = new Waiter(Client(), _sleeper, _clock).Wait(target, wait: false);
        if (outcome.NotFound)
        {
            Error($"no deployment found for {target.Value}");
            return outcome.ExitCode;
        }
        var document = outcome.Document!;
        var plan = JsonValues.ObjectOf(JsonValues.Get(document, "exercise_plan"));
        if (!Formatter.Exercisable.Contains(JsonValues.StringOf(JsonValues.Get(plan, "status"))))
        {
            var summary = JsonValues.Truthy(JsonValues.Get(plan, "summary")) ? JsonValues.Text(plan["summary"]) : "";
            Print($"Nothing to exercise. {summary}".TrimEnd());
            return 0;
        }

        var exerciser = new Exerciser(url, Math.Min(Math.Max(options.Integer("max_requests"), 1), Exerciser.MaxRequests), _sleeper, _requester);
        if (options.Flag("dry_run"))
        {
            var (targets, skipped) = exerciser.Plan(plan);
            var preview = new List<string> { $"Would send {targets.Sum(t => t.Count)} requests:" };
            preview.AddRange(targets.Select(t => $"  {exerciser.UrlFor(t.Path)} ×{t.Count}"));
            AddSkipped(preview, skipped);
            Print(string.Join("\n", preview));
            return 0;
        }

        var result = exerciser.Run(plan);
        var lines = new List<string> { $"Sent {result.Sent} requests to {url}:" };
        foreach (var route in result.Routes)
        {
            var statuses = string.Join(", ", JsonValues.ObjectOf(JsonValues.Get(route, "statuses")).Select(pair => $"{JsonValues.Text(pair.Value)} {pair.Key}"));
            lines.Add($"  {JsonValues.Text(JsonValues.Get(route, "key"))} ×{JsonValues.Text(JsonValues.Get(route, "requests"))}: {statuses}");
        }
        AddSkipped(lines, result.Skipped);
        if (result.Unreachable)
        {
            lines.Add($"Stopped early: {url} isn't answering.");
        }
        if (result.Routes.Count > 0)
        {
            var ranAt = DateTimeOffset.FromUnixTimeMilliseconds((long)(_clock() * 1000)).UtcDateTime
                .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            Client().RecordExercise(JsonValues.Text(JsonValues.Get(JsonValues.Get(document, "deployment"), "id")), result.Routes, result.Skipped, ranAt);
            lines.Add("Recorded on the release. Wait for the verdict with: deployangel verify --wait");
        }
        Print(string.Join("\n", lines));
        return result.Unreachable ? ExitCodes.Usage : 0;
    }

    private int ExceptionDetails()
    {
        if (_argv.Count == 0 || _argv[0].StartsWith('-'))
        {
            return UsageError("exception needs a FINGERPRINT");
        }
        var fingerprint = Shift();
        var options = Parse(new OptionParser().String("--format", choices: new[] { "text", "json" }));
        var details = Client().Exception(fingerprint);
        Output(details, options["format"], () => Formatter.Exception(details));
        return 0;
    }

    private int Check()
    {
        var options = Parse(new OptionParser().String("--name").String("--details-url", "details_url").String("--commit")
            .String("--deployment", "deployment_id")
            .String("--status", choices: new[] { "pass", "fail" })
            .String("--covers", defaultValue: ""));
        var name = options["name"];
        var status = options["status"];
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(status))
        {
            return UsageError("check needs --name and --status=pass|fail");
        }

        var commit = options["commit"];
        var deploymentId = options["deployment_id"];
        if (string.IsNullOrEmpty(deploymentId) && string.IsNullOrEmpty(commit))
        {
            commit = Or(GitHead(), CiEnvironment.Detect(_env)?.Commit);
        }
        var reference = !string.IsNullOrEmpty(deploymentId) ? deploymentId : !string.IsNullOrEmpty(commit) ? $"commit:{commit}" : null;
        if (reference is null)
        {
            return UsageError("check needs --deployment or --commit, or a git repository");
        }

        var covers = options["covers"]!.Split(',').Select(item => item.Trim()).Where(item => item.Length > 0).ToList();
        var result = Client().ReportCheck(reference, name, status, covers, options["details_url"]);
        Print($"Recorded {status} check \"{name}\" for deployment {JsonValues.Text(JsonValues.Get(result, "deployment_id"))}");
        return 0;
    }

    /// <summary>
    /// What this checkout will report and what DeployAngel has seen of the
    /// app. Warnings are advice, so only errors (a rejected token, an
    /// unreachable server, no agent in the project) fail it.
    /// </summary>
    private int Doctor()
    {
        var options = Parse(new OptionParser().String("--format", choices: new[] { "text", "json" }));
        var doctor = new Doctor(_root, DoctorClient(), _env);
        Output(doctor.ToJson(), options["format"], doctor.ToText);
        return doctor.Failed ? ExitCodes.Usage : 0;
    }

    /// <summary>
    /// Either token works: the setup endpoint answers any of the app's. The
    /// agent's own token is for running it where the app runs.
    /// </summary>
    internal IApiClient? DoctorClient()
    {
        if (_injectedClient is not null)
        {
            return _injectedClient;
        }
        var token = new[] { Env("DEPLOYANGEL_API_TOKEN"), Env("DEPLOYANGEL_TOKEN"), JsonValues.StringOf(Saved()["api_token"]) }
            .FirstOrDefault(value => !string.IsNullOrEmpty(value));
        return token is not null ? new ApiClient(token, Endpoint()) : null;
    }

    private int Mcp()
    {
        new McpServer(Client(), _stdin, _stdout, _stderr, _sleeper, _clock, GitHead).Run();
        return 0;
    }

    /// <summary>DEPLOYANGEL_API_TOKEN, else the token <c>deployangel setup</c> saved here.</summary>
    internal IApiClient Client()
    {
        if (_client is null)
        {
            var token = Or(Env("DEPLOYANGEL_API_TOKEN"), JsonValues.StringOf(Saved()["api_token"]));
            _client = new ApiClient(token, Endpoint());
        }
        return _client;
    }

    private string Endpoint() => Or(Or(Env("DEPLOYANGEL_URL"), JsonValues.StringOf(Saved()["endpoint"])), ToolInfo.DefaultEndpoint)!;

    private JsonObject Saved() => _savedCredentials ??= Credentials.Read(_root);

    /// <summary>
    /// Redeems a one-time code from "Set up with your coding agent" for the
    /// app's two tokens, and saves them without printing either, so they stay
    /// out of a coding agent's conversation.
    /// </summary>
    private int Setup()
    {
        var options = Parse(new OptionParser().String("--code"));
        if (string.IsNullOrEmpty(options["code"]))
        {
            return UsageError("setup needs --code (from \"Set up with your coding agent\" on the app's page)");
        }
        var redeemer = _injectedClient ?? new ApiClient(null, Endpoint(), requireToken: false);
        var result = redeemer.RedeemSetupCode(options["code"]!.Trim());
        var app = JsonValues.ObjectOf(JsonValues.Get(result, "app"));
        var file = Credentials.Write(_root, new JsonObject
        {
            ["app"] = JsonValues.Copy(app),
            ["app_token"] = JsonValues.Copy(JsonValues.Get(result, "app_token")),
            ["api_token"] = JsonValues.Copy(JsonValues.Get(result, "api_token")),
            ["endpoint"] = Endpoint(),
        });
        _savedCredentials = null;
        var relative = Path.GetRelativePath(_root, file);
        var command = ProjectCommand.NameFor(_root);
        Print($@"Set up DeployAngel for {JsonValues.Text(app["name"])} ({JsonValues.Text(app["environment"])}). Saved its app token and a ""CI deploys"" token to
{relative}, kept out of git and not shown here. The deployangel command and MCP server here use them.

The running app reads its token only from DEPLOYANGEL_TOKEN in its environment, so give it the app
token without printing it: $({command} token app) in the command that starts it or sets your
host's secrets, or {command} token app | <a command that reads a secret>. The same goes for CI and
the ""CI deploys"" token ({command} token api). Steps for each host:
https://www.deployangel.com/setup-for-agents.md".Replace("\r\n", "\n", StringComparison.Ordinal));
        return 0;
    }

    /// <summary>
    /// Prints a saved token, for piping into a host's or CI's secrets. Never
    /// run it bare where its output is shown, such as an agent's chat.
    /// </summary>
    private int Token()
    {
        var kind = _argv.Count > 0 ? Shift() : null;
        if (kind is not ("app" or "api"))
        {
            return UsageError("token needs app or api: deployangel token app");
        }
        var value = JsonValues.StringOf(Saved()[$"{kind}_token"]);
        if (string.IsNullOrEmpty(value))
        {
            return UsageError("no token saved here; run deployangel setup --code=<code> first");
        }
        _stdout.Write(value + (_stdoutIsTerminal ? "\n" : ""));
        _stdout.Flush();
        return 0;
    }

    private string? GitHead() => _gitHead();

    private static string? GitRevParseHead(string root)
    {
        try
        {
            using var git = new Process
            {
                StartInfo = new ProcessStartInfo("git", "rev-parse HEAD")
                {
                    WorkingDirectory = root,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                },
            };
            git.Start();
            var output = git.StandardOutput.ReadToEndAsync();
            _ = git.StandardError.ReadToEndAsync();
            if (!git.WaitForExit(10_000))
            {
                git.Kill();
                return null;
            }
            var head = output.Result.Trim();
            return git.ExitCode == 0 && head.Length > 0 ? head : null;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private static OptionParser TargetOptions() =>
        new OptionParser().String("--commit").String("--version").String("--deployment", "deployment_id");

    private ParsedOptions Parse(OptionParser parser) => parser.Parse(_argv);

    private Target? TargetFrom(ParsedOptions options)
    {
        if (!string.IsNullOrEmpty(options["deployment_id"]))
        {
            return new Target(DeploymentId: options["deployment_id"]);
        }
        if (!string.IsNullOrEmpty(options["version"]))
        {
            return new Target(Version: options["version"]);
        }
        var commit = Or(Or(options["commit"], GitHead()), CiEnvironment.Detect(_env)?.Commit);
        return string.IsNullOrEmpty(commit) ? null : new Target(Commit: commit.ToLowerInvariant());
    }

    private void Output(JsonNode data, string? format, Func<string> text)
    {
        format ??= _stdoutIsTerminal ? "text" : "json";
        Print(format == "json" ? JsonValues.Pretty(data) : text());
    }

    /// <summary>In GitHub Actions, the verdict also goes on the run's summary page.</summary>
    private void StepSummary(string markdown)
    {
        var path = Env("GITHUB_STEP_SUMMARY");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        try
        {
            File.AppendAllText(path, markdown.EndsWith('\n') ? markdown : markdown + "\n");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Error($"couldn't write the job summary ({error.Message})");
        }
    }

    private static void AddSkipped(List<string> lines, JsonArray skipped)
    {
        if (skipped.Count > 0)
        {
            lines.Add("Skipped:");
            lines.AddRange(skipped.Select(s => $"  {JsonValues.Text(JsonValues.Get(s, "key"))} ({JsonValues.Text(JsonValues.Get(s, "reason"))})"));
        }
    }

    private static int ParseDuration(string value)
    {
        var match = Duration.Match(value);
        if (!match.Success)
        {
            throw new UsageException($"invalid argument: --timeout={value}");
        }
        var unit = match.Groups[2].Value switch
        {
            "m" => 60,
            "h" => 3600,
            _ => 1,
        };
        return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * unit;
    }

    private string Shift()
    {
        var first = _argv[0];
        _argv.RemoveAt(0);
        return first;
    }

    private string? Env(string name) => _env.TryGetValue(name, out var value) ? value : null;

    private void Print(string text)
    {
        _stdout.Write(text + "\n");
        _stdout.Flush();
    }

    private void Error(string message) => _stderr.Write($"deployangel: {message}\n");

    private int UsageError(string message)
    {
        Error(message);
        _stderr.Write("Run `deployangel help` for usage.\n");
        return ExitCodes.Usage;
    }

    /// <summary>Python's <c>a or b</c> for strings: an empty string falls through.</summary>
    private static string? Or(string? first, string? second) => string.IsNullOrEmpty(first) ? second : first;

    private static Dictionary<string, string> ProcessEnvironment()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            env[(string)entry.Key] = entry.Value as string ?? "";
        }
        return env;
    }
}
