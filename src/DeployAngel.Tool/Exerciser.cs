using System;
using System.Collections.Generic;
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

internal sealed record ExerciseResult(JsonArray Routes, JsonArray Skipped, int Sent, bool Unreachable);

/// <summary>
/// <c>deployangel exercise</c>: sends a release's exercise plan's read-only
/// requests to production from the customer's side, then records what it
/// sent, so the release page says which routes were exercised that way. Only
/// GET routes with no path parameters and not marked as changing data; the
/// rest are skipped and named, for the agent or a smoke test with a test
/// account. The requests count like any traffic through the app's agent.
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

    // Rails' :id and *path, Django's <int:pk>, ASP.NET Core's and FastAPI's {id}.
    private static readonly Regex Parameter = new(@"/[:*{<]", RegexOptions.CultureInvariant);

    private static readonly Lazy<HttpClient> DefaultHttp = new(() =>
    {
        // Redirects aren't followed: the request already reached the app.
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout };
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
    private readonly Func<string, int?> _requester;

    private Dictionary<string, JsonObject> _statuses = new();
    private int _sent;
    private int _errorsInARow;

    public Exerciser(string baseUrl, int maxRequests = MaxRequests, Action<double>? sleeper = null, Func<string, int?>? requester = null)
    {
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
            string? reason = null;
            if (JsonValues.Truthy(JsonValues.Get(item, "mutating")) || method != "GET")
            {
                reason = "changes data";
            }
            else if (path.Length == 0 || Parameter.IsMatch(path))
            {
                reason = "needs a path parameter";
            }
            if (reason is not null)
            {
                skipped.Add(new JsonObject { ["key"] = JsonValues.Copy(JsonValues.Get(item, "key")), ["reason"] = reason });
                continue;
            }
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
        return new ExerciseResult(routes, skipped, _sent, IsUnreachable);
    }

    public string UrlFor(string path) => $"{_scheme}://{_host}{_basePath.TrimEnd('/')}{path}";

    private bool IsUnreachable => _errorsInARow >= MaxConsecutiveErrors;

    private int? Send(ExerciseTarget target)
    {
        if (_sent > 0)
        {
            _sleeper(Interval);
        }
        var status = _requester(UrlFor(target.Path));
        _sent++;
        var kind = status is > 0 ? $"{status.Value / 100}xx" : "error";
        if (!_statuses.TryGetValue(target.Key, out var statuses))
        {
            _statuses[target.Key] = statuses = new JsonObject();
        }
        statuses[kind] = (statuses.TryGetPropertyValue(kind, out var count) ? (int)count! : 0) + 1;
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
    private static int? Get(string url)
    {
        try
        {
            using var response = DefaultHttp.Value.Send(new HttpRequestMessage(HttpMethod.Get, url));
            return (int)response.StatusCode;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException
                                          or InvalidOperationException or UriFormatException)
        {
            return null;
        }
    }
}
