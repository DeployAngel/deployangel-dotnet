using System.Text.Json.Nodes;

namespace DeployAngel.Tool.Tests;

internal sealed class Clock
{
    public const double Start = 1790776800.0; // 2026-09-30 14:00 UTC

    public double Now { get; set; } = Start;

    public double Read() => Now;

    public void Advance(double seconds) => Now += seconds;
}

/// <summary>One request the fake client answered: its name and arguments, as JSON for comparing.</summary>
internal sealed record Call(string Name, string Args)
{
    public Call(string name, JsonObject? args = null) : this(name, args?.ToJsonString() ?? "{}")
    {
    }
}

/// <summary>Each verification call returns the next document, then keeps returning the last one.</summary>
internal class FakeClient : IApiClient
{
    public FakeClient(IEnumerable<JsonObject>? documents = null, JsonArray? deployments = null, params string[] scopes)
    {
        VerificationDocuments = documents?.ToList() ?? new List<JsonObject>();
        DeploymentsList = deployments ?? new JsonArray(new JsonObject { ["id"] = 42 });
        Scopes = scopes.Length > 0 ? scopes.ToList() : new List<string> { "verifications:read" };
    }

    public List<JsonObject> VerificationDocuments { get; }

    public JsonArray DeploymentsList { get; set; }

    public List<string> Scopes { get; set; }

    public List<Call> Calls { get; } = new();

    /// <summary>A document to answer with, an exception to throw, or null for a server without doctor.</summary>
    public object? SetupDocument { get; set; }

    public JsonObject TokenInfo()
    {
        Calls.Add(new Call("token_info"));
        return new JsonObject { ["scopes"] = new JsonArray(Scopes.Select(scope => (JsonNode?)scope).ToArray()) };
    }

    public JsonObject Setup()
    {
        Calls.Add(new Call("setup"));
        return SetupDocument switch
        {
            Exception error => throw error,
            JsonObject document => (JsonObject)document.DeepClone(),
            _ => throw new NotFoundException("not found"),
        };
    }

    public JsonObject LatestDeployment()
    {
        Calls.Add(new Call("latest"));
        if (DeploymentsList.Count == 0)
        {
            throw new NotFoundException("not found");
        }
        return (JsonObject)DeploymentsList[0]!.DeepClone();
    }

    public JsonArray Deployments(string? commit = null, string? version = null, int? limit = null)
    {
        Calls.Add(new Call("deployments", new JsonObject { ["commit"] = commit, ["version"] = version, ["limit"] = limit }));
        return (JsonArray)DeploymentsList.DeepClone();
    }

    public JsonObject Exception(string fingerprint) =>
        new() { ["fingerprint"] = fingerprint, ["exception_class"] = "NoMethodError", ["backtrace"] = new JsonArray() };

    public JsonArray LateRegressions(string? since = null, int? limit = null) => new();

    public JsonObject Verification(string deploymentId, bool allFindings = false)
    {
        Calls.Add(new Call("verification", new JsonObject { ["id"] = deploymentId }));
        var document = VerificationDocuments[0];
        if (VerificationDocuments.Count > 1)
        {
            VerificationDocuments.RemoveAt(0);
        }
        return document;
    }

    public virtual JsonObject RedeemSetupCode(string code) => throw new NotSupportedException();

    public JsonObject RegisterDeployment(string? commit = null, string? version = null, string? kind = null, string? provider = null,
        string? sourceUrl = null)
    {
        Calls.Add(new Call("register", new JsonObject
        {
            ["commit"] = commit, ["version"] = version, ["kind"] = kind, ["provider"] = provider, ["source_url"] = sourceUrl,
        }));
        return new JsonObject { ["id"] = 43, ["version"] = version, ["commit"] = commit, ["state"] = "pending" };
    }

    public JsonObject ReportCheck(string reference, string name, string status, IReadOnlyList<string> covers, string? detailsUrl = null)
    {
        Calls.Add(new Call("check", new JsonObject
        {
            ["reference"] = reference, ["name"] = name, ["status"] = status,
            ["covers"] = new JsonArray(covers.Select(cover => (JsonNode?)cover).ToArray()), ["details_url"] = detailsUrl,
        }));
        return new JsonObject { ["id"] = 1, ["status"] = status, ["deployment_id"] = 42 };
    }

    public JsonObject RecordExercise(string deploymentId, JsonArray routes, JsonArray skipped, string ranAt)
    {
        Calls.Add(new Call("exercise", new JsonObject
        {
            ["deployment_id"] = deploymentId, ["routes"] = routes.DeepClone(), ["skipped"] = skipped.DeepClone(), ["ran_at"] = ranAt,
        }));
        return new JsonObject { ["id"] = 1, ["deployment_id"] = deploymentId };
    }
}

internal static class Documents
{
    public static JsonObject Verdict(string state, string? verdict = null, JsonObject? initialCheck = null, int poll = 60) => new()
    {
        ["schema_version"] = 1,
        ["deployment"] = new JsonObject { ["id"] = 42, ["version"] = "v184" },
        ["verification"] = new JsonObject
        {
            ["state"] = state, ["verdict"] = verdict, ["initial_check"] = initialCheck, ["confidence"] = "high", ["coverage"] = 1.0,
        },
        ["clearance"] = new JsonObject
        {
            ["statement"] = $"v184 {verdict ?? "not cleared yet"}",
            ["missing_evidence"] = new JsonArray(),
            ["not_observable"] = new JsonArray(),
            ["still_watching"] = new JsonArray(),
        },
        ["findings"] = new JsonArray(),
        ["poll_after_seconds"] = poll,
        ["dashboard_url"] = "https://app.deployangel.com/apps/1/deployments/42",
    };

    public static JsonObject ExercisePlan(string status = "exercisable") => Json($$$"""
        {"status": "{{{status}}}", "summary": "Not cleared yet. Exercising these items against production would let it clear sooner.",
         "shortfall": {"rule": "low_volume", "requests": {"have": 12, "need": 30},
                       "routes_run_3_times": {"have": 1, "need": 3, "of": 3}},
         "items": [
           {"kind": "route", "key": "GET /orders/{id}", "reason": "normally_active", "runs": 1, "runs_needed": 3, "mutating": false,
            "needed": true},
           {"kind": "route", "key": "POST /password_resets", "reason": "changed_in_release", "runs": 0, "mutating": true, "needed": false},
           {"kind": "job_class", "key": "SendInvoiceJob", "reason": "normally_active", "runs": 0, "triggered_by": "app_behavior",
            "needed": true}
         ],
         "report_with": "deployangel check --name=\"exercise plan\" --status=pass --covers=\"GET /orders/{id},POST /password_resets,SendInvoiceJob\""}
        """);

    public static JsonObject Json(string text) => (JsonObject)JsonNode.Parse(text)!;

    public static JsonObject With(this JsonObject document, string key, JsonNode? value)
    {
        document[key] = value;
        return document;
    }
}

/// <summary>A directory of its own for each test, removed afterwards.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "deployangel-tool-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string this[string relative] => System.IO.Path.Combine(Path, relative);

    public void Write(string relative, string content)
    {
        var path = this[relative];
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public string Read(string relative) => File.ReadAllText(this[relative]);

    public bool Exists(string relative) => File.Exists(this[relative]) || Directory.Exists(this[relative]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
