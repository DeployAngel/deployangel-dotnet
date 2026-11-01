using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;

namespace DeployAngel.Tool;

/// <summary>A read-only route to request, and how many times.</summary>
internal sealed class ExerciseTarget
{
    public ExerciseTarget(string key, string path, int count)
    {
        Key = key;
        Path = path;
        Count = count;
    }

    public string Key { get; }

    public string Path { get; }

    public int Count { get; set; }
}

/// <summary>
/// What was sent. Codes holds each route's exact status codes ("no answer"
/// when there was none), for display; Routes carries status classes, which
/// is what's recorded. Unsent: requests the plan wanted that went nowhere,
/// since no route answered.
/// </summary>
internal sealed record ExerciseResult(JsonArray Routes, JsonArray Skipped, int Sent, bool Unreachable,
    IReadOnlyDictionary<string, Dictionary<string, int>> Codes, int Unsent)
{
    /// <summary>
    /// Every answer said the page isn't served. Usually the wrong host, like
    /// the app's marketing site.
    /// </summary>
    public bool NothingServed
    {
        get
        {
            var answers = Codes.Values.SelectMany(codes => codes.Keys).ToList();
            return answers.Count > 0 && answers.All(code => Exerciser.NotServedCodes.Contains(code));
        }
    }
}

/// <summary>
/// <c>deployangel exercise</c>: sends a release's exercise plan's read-only
/// requests to production from the customer's side, then records what it
/// sent, so the release page says which routes were exercised that way. Only
/// GET routes not marked as changing data, and only those whose path
/// parameters the caller filled (--param id=42); the rest are skipped and
/// named, for the agent or a smoke test with a test account. The requests
/// count like any traffic through the app's agent.
/// Behaves like the Ruby gem's and the Python package's exerciser.
/// </summary>
internal sealed class Exerciser
{
    public const int MaxRequests = 200;
    public const int MaxConsecutiveErrors = 5;

    private const double Interval = 0.2; // about 5 requests a second
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // A route table can list pages an app doesn't serve, like the edit page
    // of a resource that has none. One answer like this is enough to know.
    private static readonly int[] NotServed = { 404, 405, 410 };

    internal static readonly string[] NotServedCodes = NotServed.Select(code => code.ToString(CultureInfo.InvariantCulture)).ToArray();

    // A path parameter, capturing its name: Rails' :id and *path, ASP.NET
    // Core's and FastAPI's {id}, {id:int}, and {*path}, Django's <int:pk>.
    private static readonly Regex Parameter = new(
        @"[:*]([A-Za-z_][A-Za-z0-9_]*)|\{\*{0,2}([A-Za-z_][A-Za-z0-9_]*)(?::[^}]*)?\??\}|<(?:[A-Za-z0-9_]+:)?([A-Za-z_][A-Za-z0-9_]*)>",
        RegexOptions.CultureInvariant);

    // One that can span segments, so its value keeps its slashes.
    private static readonly Regex Glob = new(@"\A(?:\*|\{\*|\{[A-Za-z0-9_]+:path\}|<path:)", RegexOptions.CultureInvariant);

    // "Name: value", as with curl -H: a header name, then a value on one line.
    private static readonly Regex Header = new(@"\A([!#$%&'*+.^_`|~0-9A-Za-z-]+):[ \t]*(\S[^\r\n]*)\z", RegexOptions.CultureInvariant);

    private static readonly Regex Param = new(@"\A([A-Za-z_][A-Za-z0-9_]*)=(.+)\z", RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Lazy<HttpClient> DefaultHttp = new(() =>
    {
        // Redirects aren't followed: the request already reached the app.
        // Without a cookie container, a Cookie header is sent as given.
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            $"DeployAngel-Exercise/{ToolInfo.Version} (+https://www.deployangel.com/docs#exercise)");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/json;q=0.9,*/*;q=0.8");
        return http;
    });

    private readonly string _scheme;
    private readonly string _host;
    private readonly string _basePath;
    private readonly int _maxRequests;
    private readonly Action<double> _sleeper;
    private readonly Func<string, IReadOnlyDictionary<string, string>, int?> _requester;
    private readonly IReadOnlyDictionary<string, string> _headers;
    private readonly IReadOnlyDictionary<string, string> _parameters;

    private Dictionary<string, JsonObject> _statuses = new();
    private Dictionary<string, Dictionary<string, int>> _codes = new();
    private int _sent;
    private int _errorsInARow;

    /// <summary>Headers go only to baseUrl's host: redirects aren't followed.</summary>
    public Exerciser(string baseUrl, int maxRequests = MaxRequests, Action<double>? sleeper = null,
        Func<string, IReadOnlyDictionary<string, string>, int?>? requester = null,
        IReadOnlyDictionary<string, string>? headers = null, IReadOnlyDictionary<string, string>? parameters = null)
    {
        _headers = headers ?? new Dictionary<string, string>();
        _parameters = parameters ?? new Dictionary<string, string>();
        var parts = Regex.Match(baseUrl, @"\A(?:([^:/?#]+):)?(?://([^/?#]*))?([^?#]*)");
        _scheme = parts.Groups[1].Value;
        _host = parts.Groups[2].Value;
        _basePath = parts.Groups[3].Value;
        _maxRequests = maxRequests;
        _sleeper = sleeper ?? (seconds => Thread.Sleep(TimeSpan.FromSeconds(seconds)));
        _requester = requester ?? Get;
    }

    /// <summary>What would be sent: the targets, and what's skipped and why.</summary>
    public (List<ExerciseTarget> Targets, JsonArray Skipped) Plan(JsonObject exercisePlan)
    {
        var targets = new List<ExerciseTarget>();
        var skipped = new JsonArray();
        foreach (var item in JsonValues.ItemsOf(JsonValues.Get(exercisePlan, "items")))
        {
            if (JsonValues.StringOf(JsonValues.Get(item, "kind")) != "route")
            {
                continue;
            }
            var key = JsonValues.Has(item, "key") ? JsonValues.Text(JsonValues.Get(item, "key")) : "";
            var space = key.IndexOf(' ', StringComparison.Ordinal);
            var method = space < 0 ? key : key[..space];
            var path = space < 0 ? "" : key[(space + 1)..];
            if (JsonValues.Truthy(JsonValues.Get(item, "mutating")) || method != "GET")
            {
                skipped.Add(new JsonObject { ["key"] = JsonValues.Copy(JsonValues.Get(item, "key")), ["reason"] = "changes data" });
                continue;
            }
            var (filled, missing) = path.Length == 0 ? (path, new List<string>()) : Fill(path);
            if (path.Length == 0 || missing.Count > 0)
            {
                skipped.Add(new JsonObject
                {
                    ["key"] = JsonValues.Copy(JsonValues.Get(item, "key")),
                    ["reason"] = "needs a path parameter",
                    ["missing"] = new JsonArray(missing.Select(name => (JsonNode?)name).ToArray()),
                });
                continue;
            }
            path = filled;
            var needed = JsonValues.Truthy(JsonValues.Get(item, "runs_needed"))
                ? JsonValues.ToInteger(JsonValues.Get(item, "runs_needed")) - RunsOf(item)
                : 1;
            targets.Add(new ExerciseTarget(key, path, (int)Math.Max(needed, 1)));
        }
        SpreadShortfall(targets, JsonValues.Get(JsonValues.Get(exercisePlan, "shortfall"), "requests"));
        while (targets.Sum(target => target.Count) > _maxRequests)
        {
            targets.Aggregate((most, target) => target.Count > most.Count ? target : most).Count -= 1;
        }
        return (targets, skipped);
    }

    /// <summary>
    /// A route whose first request says it isn't served gets no more; its
    /// share goes to the routes that answered.
    /// </summary>
    public ExerciseResult Run(JsonObject exercisePlan)
    {
        var (targets, skipped) = Plan(exercisePlan);
        _statuses = new Dictionary<string, JsonObject>();
        _codes = new Dictionary<string, Dictionary<string, int>>();
        _sent = 0;
        _errorsInARow = 0;
        var leftover = 0;
        var answered = new List<ExerciseTarget>();
        foreach (var target in targets)
        {
            for (var i = 0; i < target.Count; i++)
            {
                if (IsUnreachable)
                {
                    break;
                }
                var status = Send(target);
                if (i == 0 && status is not null && NotServed.Contains(status.Value))
                {
                    leftover += target.Count - 1;
                    break;
                }
            }
            if (_statuses.TryGetValue(target.Key, out var statuses) && (statuses.ContainsKey("2xx") || statuses.ContainsKey("3xx")))
            {
                answered.Add(target);
            }
        }
        if (answered.Count > 0)
        {
            for (var i = 0; i < leftover; i++)
            {
                if (IsUnreachable)
                {
                    break;
                }
                Send(answered[i % answered.Count]);
            }
        }
        var unsent = answered.Count > 0 ? 0 : leftover;
        var routes = new JsonArray();
        foreach (var target in targets.Where(target => _statuses.ContainsKey(target.Key)))
        {
            var statuses = _statuses[target.Key];
            routes.Add(new JsonObject
            {
                ["key"] = target.Key,
                ["requests"] = statuses.Sum(pair => (int)pair.Value!),
                ["statuses"] = statuses.DeepClone(),
            });
        }
        return new ExerciseResult(routes, skipped, _sent, IsUnreachable,
            targets.Where(target => _codes.ContainsKey(target.Key)).ToDictionary(target => target.Key, target => _codes[target.Key]), unsent);
    }

    /// <summary>
    /// The path with the given parameters filled in, and the names of any left
    /// unfilled.
    /// </summary>
    public (string Path, List<string> Missing) Fill(string path)
    {
        var missing = new List<string>();
        var filled = Parameter.Replace(path, match =>
        {
            var name = new[] { match.Groups[1], match.Groups[2], match.Groups[3] }.First(group => group.Success).Value;
            if (!_parameters.TryGetValue(name, out var value))
            {
                if (!missing.Contains(name))
                {
                    missing.Add(name);
                }
                return match.Value;
            }
            var parts = Glob.IsMatch(match.Value) ? value.Split('/') : new[] { value };
            return string.Join("/", parts.Select(Uri.EscapeDataString));
        });
        return (filled, missing);
    }

    /// <summary>
    /// Headers to send with every request, such as a token or a test account's
    /// session cookie, from "Name: value" lines. Throws naming the line, never
    /// its value.
    /// </summary>
    public static Dictionary<string, string> ParseHeaders(IEnumerable<string> lines)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var line in lines.Select(line => line.Trim()).Where(line => line.Length > 0))
        {
            index++;
            var match = Header.Match(line);
            if (!match.Success)
            {
                throw new UsageException($"header {index} must look like \"Name: value\"");
            }
            headers[match.Groups[1].Value] = match.Groups[2].Value.TrimEnd();
        }
        return headers;
    }

    /// <summary>
    /// Path parameter values, from "name=value" (--param id=42). Each fills
    /// that name in every route that has it.
    /// </summary>
    public static Dictionary<string, string> ParseParameters(IEnumerable<string> pairs)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in pairs)
        {
            var match = Param.Match(pair);
            if (!match.Success)
            {
                throw new UsageException($"--param must look like name=value, such as --param id=42 (got '{pair}')");
            }
            parameters[match.Groups[1].Value] = match.Groups[2].Value;
        }
        return parameters;
    }

    public string UrlFor(string path) => $"{_scheme}://{_host}{_basePath.TrimEnd('/')}{path}";

    private bool IsUnreachable => _errorsInARow >= MaxConsecutiveErrors;

    private int? Send(ExerciseTarget target)
    {
        if (_sent > 0)
        {
            _sleeper(Interval);
        }
        var status = _requester(UrlFor(target.Path), _headers);
        _sent++;
        var kind = status is > 0 ? $"{status.Value / 100}xx" : "error";
        if (!_statuses.TryGetValue(target.Key, out var statuses))
        {
            _statuses[target.Key] = statuses = new JsonObject();
        }
        statuses[kind] = (statuses.TryGetPropertyValue(kind, out var count) ? (int)count! : 0) + 1;
        if (!_codes.TryGetValue(target.Key, out var codes))
        {
            _codes[target.Key] = codes = new Dictionary<string, int>();
        }
        var code = status is > 0 ? status.Value.ToString(CultureInfo.InvariantCulture) : "no answer";
        codes[code] = codes.GetValueOrDefault(code) + 1;
        _errorsInARow = kind == "error" ? _errorsInARow + 1 : 0;
        return status;
    }

    /// <summary>
    /// Requests the release is short of overall, spread across the routes, or
    /// sent to the home page when the plan names no route to send them to.
    /// </summary>
    private static void SpreadShortfall(List<ExerciseTarget> targets, JsonNode? requests)
    {
        if (!JsonValues.Truthy(requests))
        {
            return;
        }
        var extra = IntegerOr0(JsonValues.Get(requests, "need")) - IntegerOr0(JsonValues.Get(requests, "have")) - targets.Sum(target => target.Count);
        if (extra <= 0)
        {
            return;
        }
        if (targets.Count == 0)
        {
            targets.Add(new ExerciseTarget("GET /", "/", 0));
        }
        for (var i = 0; i < extra; i++)
        {
            targets[i % targets.Count].Count += 1;
        }
    }

    private static long RunsOf(JsonNode? item) => IntegerOr0(JsonValues.Get(item, "runs"));

    private static long IntegerOr0(JsonNode? value) => JsonValues.Truthy(value) ? JsonValues.ToInteger(value) : 0;

    /// <summary>The status code, or null when there was no answer.</summary>
    private static int? Get(string url, IReadOnlyDictionary<string, string> headers)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            foreach (var (name, value) in headers)
            {
                request.Headers.Remove(name);
                request.Headers.TryAddWithoutValidation(name, value);
            }
            using var response = DefaultHttp.Value.Send(request);
            return (int)response.StatusCode;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException
                                          or InvalidOperationException or UriFormatException)
        {
            return null;
        }
    }
}
