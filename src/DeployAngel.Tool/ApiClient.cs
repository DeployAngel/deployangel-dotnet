using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeployAngel.Tool;

internal class ApiException : Exception
{
    public ApiException(string message, int? status = null) : base(message)
    {
        Status = status;
    }

    public int? Status { get; }
}

internal sealed class NotFoundException : ApiException
{
    public NotFoundException(string message, int? status = 404) : base(message, status)
    {
    }
}

internal sealed class UnauthorizedException : ApiException
{
    public UnauthorizedException(string message, int? status = null) : base(message, status)
    {
    }
}

/// <summary>DeployAngel's read and registration API, as the command and its MCP server use it.</summary>
internal interface IApiClient
{
    JsonObject TokenInfo();

    JsonObject Setup();

    JsonObject LatestDeployment();

    JsonArray Deployments(string? commit = null, string? version = null, int? limit = null);

    JsonObject Exception(string fingerprint);

    JsonArray LateRegressions(string? since = null, int? limit = null);

    JsonObject Verification(string deploymentId, bool allFindings = false);

    /// <summary>The app's token and a "CI deploys" token, for a one-time setup code.</summary>
    JsonObject RedeemSetupCode(string code);

    JsonObject RegisterDeployment(string? commit = null, string? version = null, string? kind = null,
        string? provider = null, string? sourceUrl = null);

    JsonObject ReportCheck(string reference, string name, string status, IReadOnlyList<string> covers, string? detailsUrl = null);

    /// <summary>
    /// What <c>deployangel exercise</c> sent (spec §16, Exercise records). A
    /// "CLI &amp; coding agents" token may record one: it only labels the release.
    /// </summary>
    JsonObject RecordExercise(string deploymentId, JsonArray routes, JsonArray skipped, string ranAt);
}

/// <summary>
/// Unlike the agent's transport, errors throw, because a person or a coding
/// agent is waiting on the answer. Same requests as the Python and Ruby
/// commands' clients.
/// </summary>
internal sealed class ApiClient : IApiClient, IDisposable
{
    private readonly HttpClient _http;

    public ApiClient(string? token, string endpoint = ToolInfo.DefaultEndpoint, TimeSpan? timeout = null,
        bool requireToken = true, HttpMessageHandler? handler = null)
    {
        // No token only for redeeming a setup code, which is its own credential.
        if (requireToken && string.IsNullOrEmpty(token))
        {
            throw new UnauthorizedException("DEPLOYANGEL_API_TOKEN is not set, and no token was saved here by `deployangel setup`");
        }
        Token = token;
        Endpoint = endpoint.TrimEnd('/');
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = timeout ?? TimeSpan.FromSeconds(15);
    }

    public string? Token { get; }

    public string Endpoint { get; }

    public JsonObject TokenInfo() => ObjectFrom(Get("/api/v1/token"));

    public JsonObject Setup() => ObjectFrom(Get("/api/v1/setup"));

    public JsonObject LatestDeployment() => ObjectFrom(Get("/api/v1/deployments/latest"));

    public JsonArray Deployments(string? commit = null, string? version = null, int? limit = null) =>
        ArrayIn(Get("/api/v1/deployments", ("commit", commit), ("version", version), ("limit", Number(limit))), "deployments");

    public JsonObject Exception(string fingerprint) => ObjectFrom(Get($"/api/v1/exceptions/{Uri.EscapeDataString(fingerprint)}"));

    public JsonArray LateRegressions(string? since = null, int? limit = null) =>
        ArrayIn(Get("/api/v1/late_regressions", ("since", since), ("limit", Number(limit))), "late_regressions");

    public JsonObject Verification(string deploymentId, bool allFindings = false) =>
        ObjectFrom(Get($"/api/v1/deployments/{deploymentId}/verification", ("findings", allFindings ? "all" : null)));

    public JsonObject RedeemSetupCode(string code) => ObjectFrom(Post("/api/v1/setup_codes/redeem", new JsonObject { ["code"] = code }));

    public JsonObject RegisterDeployment(string? commit = null, string? version = null, string? kind = null,
        string? provider = null, string? sourceUrl = null) =>
        ObjectFrom(Post("/api/v1/deployments", Compact(("commit", commit), ("version", version), ("kind", kind),
            ("provider", provider), ("source_url", sourceUrl))));

    public JsonObject ReportCheck(string reference, string name, string status, IReadOnlyList<string> covers, string? detailsUrl = null)
    {
        var body = Compact(("name", name), ("status", status));
        body["covers"] = new JsonArray(covers.Select(cover => (JsonNode?)cover).ToArray());
        if (detailsUrl is not null)
        {
            body["details_url"] = detailsUrl;
        }
        return ObjectFrom(Post($"/api/v1/deployments/{Uri.EscapeDataString(reference)}/checks", body));
    }

    public JsonObject RecordExercise(string deploymentId, JsonArray routes, JsonArray skipped, string ranAt) =>
        ObjectFrom(Post($"/api/v1/deployments/{deploymentId}/exercises", new JsonObject
        {
            ["routes"] = JsonValues.Copy(routes),
            ["skipped"] = JsonValues.Copy(skipped),
            ["ran_at"] = ranAt,
        }));

    public void Dispose() => _http.Dispose();

    private JsonNode? Get(string path, params (string Key, string? Value)[] parameters)
    {
        var url = Endpoint + path;
        var query = string.Join("&", parameters.Where(p => p.Value is not null)
            .Select(p => $"{QueryEscape(p.Key)}={QueryEscape(p.Value!)}"));
        if (query.Length > 0)
        {
            url += "?" + query;
        }
        return Send(new HttpRequestMessage(HttpMethod.Get, url));
    }

    private JsonNode? Post(string path, JsonObject body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Endpoint + path)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(JsonValues.OneLine(body))),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return Send(request);
    }

    private JsonNode? Send(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(Token))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Token}");
        }
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", ToolInfo.UserAgent);
        int status;
        byte[] raw;
        try
        {
            using var response = _http.Send(request);
            status = (int)response.StatusCode;
            using var stream = response.Content.ReadAsStream();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            raw = buffer.ToArray();
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException
                                          or InvalidOperationException or UriFormatException or NotSupportedException)
        {
            throw new ApiException($"could not reach {Endpoint}: {error.GetType().Name}: {error.Message}");
        }
        finally
        {
            request.Dispose();
        }
        return Handle(status, raw);
    }

    internal static JsonNode? Handle(int status, byte[] raw)
    {
        JsonNode? body;
        try
        {
            var text = Encoding.UTF8.GetString(raw);
            body = text.Trim().Length == 0 ? new JsonObject() : JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            throw new ApiException($"unexpected response (HTTP {status})", status);
        }
        var error = JsonValues.Get(body, "error");
        var message = JsonValues.Truthy(error) ? JsonValues.Text(error) : null;
        if (status is >= 200 and < 300)
        {
            return body;
        }
        if (status == 404)
        {
            throw new NotFoundException(message ?? "not found", 404);
        }
        if (status is 401 or 403)
        {
            throw new UnauthorizedException(message ?? "unauthorized", status);
        }
        throw new ApiException(message ?? $"HTTP {status}", status);
    }

    private static JsonObject ObjectFrom(JsonNode? body) =>
        body as JsonObject ?? throw new ApiException("unexpected response");

    private static JsonArray ArrayIn(JsonNode? body, string key) =>
        JsonValues.Get(body, key) as JsonArray ?? throw new ApiException($"unexpected response: no {key}");

    private static JsonObject Compact(params (string Key, string? Value)[] values)
    {
        var body = new JsonObject();
        foreach (var (key, value) in values)
        {
            if (value is not null)
            {
                body[key] = value;
            }
        }
        return body;
    }

    private static string? Number(int? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // As Python's urlencode quotes: spaces become "+".
    private static string QueryEscape(string value) => Uri.EscapeDataString(value).Replace("%20", "+", StringComparison.Ordinal);
}
