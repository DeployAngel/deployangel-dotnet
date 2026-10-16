using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using static DeployAngel.Tool.JsonValues;

namespace DeployAngel.Tool;

/// <summary>One line of doctor's report: ok, warn, error, or info. Only errors fail the command.</summary>
internal sealed record Check(string Status, string Message, string? Fix = null)
{
    public JsonObject ToJson()
    {
        var json = new JsonObject { ["status"] = Status, ["message"] = Message };
        if (Fix is not null)
        {
            json["fix"] = Fix;
        }
        return json;
    }
}

/// <summary>
/// <c>deployangel doctor</c>: what this checkout will report, and what
/// DeployAngel has seen of the app so far (GET /api/v1/setup), as checks with
/// what to do about each. It never builds or loads the app, so the schedules
/// the agent reads (Hangfire's recurring jobs, the RecurringJobs setting) are
/// left to the running agent. Behaves like the Ruby gem's and the Python
/// package's doctor.
/// </summary>
internal sealed class Doctor
{
    public const string AgentPackage = "DeployAngel";

    private const string TokenHelp = "Set DEPLOYANGEL_API_TOKEN (a \"CLI & coding agents\" token from the app's Settings) or DEPLOYANGEL_TOKEN " +
                                     "(the app's agent token), then run this again.";
    private const string StartHint = "add builder.Services.AddDeployAngel(); to Program.cs";
    private const int MaxHosts = 5;

    // The schedules the agent reads when the app runs, by the packages that
    // keep them; RecurringJobs is the agent's own setting.
    private static readonly (string[] Packages, string Label)[] Schedulers =
    {
        (new[] { "DeployAngel.Hangfire", "Hangfire", "Hangfire.Core", "Hangfire.AspNetCore" }, "Hangfire's recurring jobs"),
    };

    private static readonly string[] Months = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

    private readonly string _root;
    private readonly IApiClient? _client;
    private readonly IReadOnlyDictionary<string, string> _env;
    private ProjectFiles? _project;
    private List<Check>? _checks;

    /// <param name="client">Null when no token is set; the local checks still run.</param>
    public Doctor(string root, IApiClient? client, IReadOnlyDictionary<string, string> env)
    {
        _root = root;
        _client = client;
        _env = env;
    }

    public JsonObject? SetupDocument { get; private set; }

    public IReadOnlyList<Check> Checks
    {
        get
        {
            if (_checks is null)
            {
                var server = ServerChecks();
                var checks = new List<Check?>();
                checks.AddRange(AgentChecks());
                checks.Add(HereCheck());
                checks.Add(ScheduleCheck());
                checks.AddRange(server);
                checks.Add(ReleaseCheck());
                _checks = checks.Where(check => check is not null).Select(check => check!).ToList();
            }
            return _checks;
        }
    }

    public bool Failed => Checks.Any(check => check.Status == "error");

    public JsonObject ToJson() => new()
    {
        ["checks"] = new JsonArray(Checks.Select(check => (JsonNode?)check.ToJson()).ToArray()),
        ["setup"] = Copy(SetupDocument),
    };

    /// <summary>The report as the command prints it.</summary>
    public string ToText()
    {
        var lines = new List<string> { "DeployAngel doctor" };
        foreach (var check in Checks)
        {
            var mark = check.Status switch
            {
                "ok" => "✓",
                "warn" => "!",
                "error" => "✗",
                _ => "·",
            };
            lines.Add($"  {mark} {check.Message}");
            if (check.Fix is not null)
            {
                lines.Add($"      {check.Fix}");
            }
        }
        return string.Join("\n", lines);
    }

    private ProjectFiles Project => _project ??= ProjectFiles.Scan(_root);

    /// <summary>
    /// The agent package in the projects' references, and the call that
    /// starts it: without AddDeployAngel the package is there but nothing runs.
    /// </summary>
    private List<Check> AgentChecks()
    {
        if (Project.Dependencies.Count == 0)
        {
            return new List<Check>();
        }
        var found = Project.Find(AgentPackage);
        if (found is null)
        {
            return new List<Check> { new("error", "The agent isn't in the project's dependencies", InstallFix()) };
        }
        var (file, version) = found.Value;
        var checks = new List<Check>
        {
            new("ok", version is not null ? $"Agent: {AgentPackage} {version} in {file}" : $"Agent: {AgentPackage} in {file}"),
        };
        if (!Project.Calls("AddDeployAngel"))
        {
            checks.Add(new Check("error", "The agent is installed but never started: nothing here calls AddDeployAngel()",
                $"In the app's startup code, {StartHint}, before builder.Build()."));
        }
        return checks;
    }

    private string InstallFix()
    {
        if (Project.UsesPaket)
        {
            return $"Run paket add {AgentPackage}, then {StartHint}.";
        }
        var projects = Project.ProjectPaths.ToList();
        var where = projects.Count == 1 ? $"dotnet add {projects[0]} package {AgentPackage}" : $"dotnet add package {AgentPackage} in the app's project";
        return $"Run {where}, then {StartHint}.";
    }

    private Check ScheduleCheck()
    {
        var later = Schedulers.Where(scheduler => scheduler.Packages.Any(package => Project.Find(package) is not null))
            .Select(scheduler => scheduler.Label)
            .Append("RecurringJobs")
            .ToList();
        var listed = later.Count switch
        {
            1 => later[0],
            2 => string.Join(" and ", later),
            _ => string.Join(", ", later.Take(later.Count - 1)) + ", and " + later[^1],
        };
        return new Check("info", $"This checkout's scheduled jobs aren't checked here. {listed} {(later.Count == 1 ? "is" : "are")} " +
                                 "read when the app runs.");
    }

    /// <summary>The token's app and what DeployAngel has seen of it.</summary>
    private List<Check> ServerChecks()
    {
        if (_client is null)
        {
            return new List<Check> { new("warn", "No token, so only this checkout was checked", TokenHelp) };
        }
        JsonObject setup;
        try
        {
            setup = _client.Setup();
        }
        catch (UnauthorizedException error)
        {
            return new List<Check> { new("error", $"DeployAngel rejected the token ({error.Message})", TokenHelp) };
        }
        catch (NotFoundException)
        {
            return new List<Check> { new("warn", "This DeployAngel server doesn't support doctor yet; only this checkout was checked") };
        }
        catch (ApiException error)
        {
            return new List<Check> { new("error", $"Couldn't reach DeployAngel: {error.Message}") };
        }
        SetupDocument = setup;
        var checks = new List<Check?> { AppCheck(setup), AgentCheck(setup), MetadataCheck(setup) };
        checks.AddRange(GapChecks(setup));
        checks.Add(ProgressCheck(setup));
        return checks.Where(check => check is not null).Select(check => check!).ToList();
    }

    /// <summary>
    /// Run where the app runs (<c>heroku run</c>, a container shell), the
    /// release this process would report. On a developer machine it would
    /// only be the checkout's git HEAD, which says nothing about production,
    /// so it's left out there. When nothing in the environment names the
    /// release, the agent reads the commit the .NET SDK stamped in the app's
    /// assembly (when it was built in a git checkout), else falls back to a
    /// fingerprint of the app's files; only the running agent can see either.
    /// </summary>
    private Check? HereCheck()
    {
        try
        {
            var root = Env("DEPLOYANGEL_ROOT") is { Length: > 0 } configured ? configured : _root;
            var release = Releases.Resolve(_env, root);
            if (release.Unknown)
            {
                if (!WhereTheAppRuns())
                {
                    return null;
                }
                if (FileDigestsOn())
                {
                    return new Check("warn", "Here, nothing in the environment names the release: the agent would report the commit the app was built from (from assembly_version), else a code fingerprint (from code_fingerprint), with no commit",
                        "Set DEPLOYANGEL_REVISION to the deployed commit to be sure each release shows its commits and pull requests.");
                }
                return new Check("warn", "Here, the agent can't tell which release is running",
                    "Set DEPLOYANGEL_REVISION to the deployed commit, or see https://deployangel.com/docs#containers.");
            }
            if (release.Source != "git_head")
            {
                return new Check("ok", $"Here, the agent would report release {release.Version ?? release.Commit} (from {release.Source})");
            }
        }
        catch (Exception)
        {
            return null;
        }
        return null;
    }

    private bool WhereTheAppRuns() => !string.IsNullOrEmpty(Env("DEPLOYANGEL_TOKEN")) && string.IsNullOrEmpty(Env("DEPLOYANGEL_API_TOKEN"));

    // On unless DEPLOYANGEL_FILE_DIGESTS says otherwise, as the agent reads it.
    private bool FileDigestsOn()
    {
        var value = Env("DEPLOYANGEL_FILE_DIGESTS");
        return string.IsNullOrWhiteSpace(value) || value.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";
    }

    /// <summary>
    /// How production will know the release, judged from what's reporting
    /// once anything is, and from this checkout's deploy files before then.
    /// </summary>
    private Check? ReleaseCheck()
    {
        if (Truthy(Get(Get(SetupDocument, "agent"), "processes")))
        {
            return null;
        }
        if (IsFile(Path.Combine("config", "deploy.yml")))
        {
            return new Check("ok", "Kamal deploys: the agent reads the release from KAMAL_VERSION");
        }
        if (IsFile("render.yaml"))
        {
            return new Check("ok", "Render deploys: the agent reads the commit from RENDER_GIT_COMMIT");
        }
        if ((Read(Path.Combine(".do", "app.yaml")) ?? "").Contains("DEPLOYANGEL_REVISION", StringComparison.Ordinal))
        {
            return new Check("ok", "The DigitalOcean app spec passes the commit in DEPLOYANGEL_REVISION");
        }
        var dockerfile = Read("Dockerfile");
        if (dockerfile is not null)
        {
            if (dockerfile.Contains("DEPLOYANGEL_REVISION", StringComparison.Ordinal))
            {
                return new Check("ok", "The Dockerfile passes the commit in DEPLOYANGEL_REVISION");
            }
            // Platforms can pass it another way, so this is advice until the
            // agent reports: then the release it names settles it.
            return new Check("info", "The Dockerfile doesn't set DEPLOYANGEL_REVISION; unless your platform passes the commit, " +
                                     "releases won't be named by commit",
                "Run `deployangel install docker`, then build with --build-arg GIT_SHA=$(git rev-parse HEAD).");
        }
        if (IsFile("Procfile") || IsFile("app.json"))
        {
            return new Check("info", "On Heroku, turn on dyno metadata so the agent knows the commit",
                "heroku labs:enable runtime-dyno-metadata && heroku labs:enable runtime-dyno-build-metadata");
        }
        return null;
    }

    private static Check AppCheck(JsonObject setup)
    {
        var app = Get(setup, "app");
        return new Check("ok", $"App: {TextOrEmpty(Get(app, "name"))} ({TextOrEmpty(Get(app, "environment"))})");
    }

    private Check AgentCheck(JsonObject setup)
    {
        var agent = Get(setup, "agent");
        var processes = ItemsOf(Get(agent, "processes"));
        if (Truthy(Get(agent, "reporting")))
        {
            var releases = processes.Select(process => Get(process, "release")).Select(release => release is null ? null : OneLine(release))
                .Distinct().ToList();
            var only = releases.Count == 1 ? processes.Select(process => Get(process, "release")).First() : null;
            var release = only is not null ? $", release {Text(only)}" : "";
            var where = processes.Count > 0 ? $" from {Hosts(processes)}" : "";
            return new Check("ok", $"Agent reporting{where}{release}");
        }
        if (Truthy(Get(agent, "last_received_at")))
        {
            return new Check("warn", $"The agent last reported {Time(Get(agent, "last_received_at"))}, and nothing since",
                "Check the app is running, and that DEPLOYANGEL_TOKEN is still set on its processes.");
        }
        return new Check("warn", "The agent hasn't reported yet", TokenFix());
    }

    /// <summary>
    /// The running app reads its token only from DEPLOYANGEL_TOKEN in its
    /// environment, never from the file <c>deployangel setup</c> saves.
    /// </summary>
    private string TokenFix()
    {
        if (Credentials.Find(_root) is not null)
        {
            return "Start or deploy the app with DEPLOYANGEL_TOKEN in its processes' environment. The app token is saved here, " +
                   $"so pass it without printing it: $({ProjectCommand.NameFor(_root)} token app) in the start command or your host's secrets command.";
        }
        return "Start or deploy the app with DEPLOYANGEL_TOKEN in its processes' environment, set to its app token " +
               "(\"Deployed application\" in the app's Settings, under API tokens).";
    }

    private static Check? MetadataCheck(JsonObject setup)
    {
        var metadata = Get(setup, "metadata");
        if (!Truthy(metadata))
        {
            return null;
        }
        var problems = ItemsOf(Get(setup, "scheduled_jobs"))
            .Where(job => StringOf(Get(job, "status")) is "overdue" or "failed")
            .ToList();
        var problemText = problems.Count > 0
            ? $", {problems.Count} needing attention (" +
              string.Join(", ", problems.Take(3).Select(job => $"{Text(Get(job, "label"))} {Text(Get(job, "status"))}")) + ")"
            : "";
        return new Check(problems.Count > 0 ? "warn" : "ok",
            $"Reported: {Count(Get(metadata, "routes"), "route")}, {Count(Get(metadata, "job_classes"), "job class", "job classes")}, " +
            $"{Count(Get(metadata, "schedules"), "scheduled job")}{problemText}",
            problems.Count > 0 ? "See them on the app page, under Scheduled jobs." : null);
    }

    private static IEnumerable<Check> GapChecks(JsonObject setup) =>
        ItemsOf(Get(setup, "gaps")).Select(gap => new Check("warn", Text(Get(gap, "title")), Get(gap, "fix") is { } fix ? Text(fix) : null));

    private static Check? ProgressCheck(JsonObject setup)
    {
        var deployments = Get(setup, "deployments");
        var latest = Get(deployments, "latest");
        string message;
        if (Truthy(Get(setup, "warm_up_ends_at")))
        {
            message = $"Learning what's normal for this app until {Time(Get(setup, "warm_up_ends_at"))}; " +
                      "deploys before then are checked, not cleared";
        }
        else if (Truthy(latest))
        {
            message = $"{Count(Get(deployments, "count"), "deploy")} so far; the latest, {Text(Get(latest, "label"))}, " +
                      $"is {Text(Or(Get(latest, "verdict"), Get(latest, "state")))}";
        }
        else if (Truthy(Get(Get(setup, "agent"), "last_received_at")))
        {
            message = "No deploys yet: your next deploy gets the first verdict";
        }
        else
        {
            return null;
        }
        return new Check("info", message);
    }

    private static string Hosts(IReadOnlyList<JsonNode?> processes)
    {
        var names = processes.Select(process => IntegerOr0(Get(process, "processes")) > 1
            ? $"{TextOrEmpty(Get(process, "host"))} ×{Text(Get(process, "processes"))}"
            : TextOrEmpty(Get(process, "host"))).ToList();
        var total = processes.Sum(process => IntegerOr0(Get(process, "processes")));
        var listed = string.Join(", ", names.Take(MaxHosts)) + (names.Count > MaxHosts ? ", and others" : "");
        return $"{Count(total, "process", "processes")} ({listed})";
    }

    private static string Time(JsonNode? iso)
    {
        var text = Text(iso);
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var moment))
        {
            return text;
        }
        var utc = moment.UtcDateTime;
        return $"{Months[utc.Month - 1]} {utc.Day}, {utc:HH:mm} UTC";
    }

    private static string Count(JsonNode? number, string singular, string? plural = null) => Count(IntegerOr0(number), singular, plural);

    private static string Count(long number, string singular, string? plural = null) =>
        $"{number} {(number == 1 ? singular : plural ?? singular + "s")}";

    private static long IntegerOr0(JsonNode? value)
    {
        try
        {
            return Truthy(value) ? ToInteger(value) : 0;
        }
        catch (FormatException)
        {
            return 0;
        }
    }

    private static string TextOrEmpty(JsonNode? value) => Truthy(value) ? Text(value) : "";

    private string? Env(string name) => _env.TryGetValue(name, out var value) ? value : null;

    private bool IsFile(string relative) => File.Exists(Path.Combine(_root, relative));

    private string? Read(string relative)
    {
        try
        {
            return File.ReadAllText(Path.Combine(_root, relative));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
