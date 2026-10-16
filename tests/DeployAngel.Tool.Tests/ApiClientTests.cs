using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace DeployAngel.Tool.Tests;

/// <summary>Answers like DeployAngel's API, from a table of paths, and keeps every request.</summary>
internal sealed class FakeApi : HttpMessageHandler
{
    public Dictionary<string, (int Status, string Body)> Routes { get; } = new();

    public List<(string Method, string PathAndQuery, Dictionary<string, string> Headers, string Body)> Requests { get; } = new();

    /// <summary>Thrown instead of answering, as a refused connection or a timeout is.</summary>
    public Exception? Failure { get; set; }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Failure is not null)
        {
            throw Failure;
        }
        var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(header => header.Key, header => string.Join(", ", header.Value), StringComparer.OrdinalIgnoreCase);
        var body = request.Content is null ? "" : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
        Requests.Add((request.Method.Method, request.RequestUri!.PathAndQuery, headers, body));
        var (status, answer) = Routes.TryGetValue(request.RequestUri.AbsolutePath, out var route) ? route : (404, """{"error": "deployment not found"}""");
        return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(Send(request, cancellationToken));
}

/// <summary>
/// The command's API client, through a fake HTTP layer, including its failure
/// paths: errors throw, because someone is waiting.
/// </summary>
public sealed class ApiClientTests
{
    private const string Endpoint = "https://api.example.test";
    private readonly FakeApi _api = new();

    private ApiClient Client(string? token = "t", bool requireToken = true) => new(token, Endpoint, requireToken: requireToken, handler: _api);

    [Fact]
    public void ReadsJsonAndSendsTheToken()
    {
        _api.Routes["/api/v1/deployments"] = (200, """{"deployments": [{"id": 42}]}""");
        var client = Client("da_test_token");

        Assert.Equal("""[{"id":42}]""", client.Deployments(commit: "abc1234", version: null, limit: 1).ToJsonString());
        var (_, path, headers, _) = _api.Requests[^1];
        Assert.Equal("/api/v1/deployments?commit=abc1234&limit=1", path);
        Assert.Equal("Bearer da_test_token", headers["Authorization"]);
        Assert.StartsWith("deployangel-cli-dotnet/", headers["User-Agent"]);
        Assert.Contains(" dotnet/", headers["User-Agent"]);
    }

    [Fact]
    public void ReadsTheSetupDocumentForDoctor()
    {
        _api.Routes["/api/v1/setup"] = (200, """{"app": {"name": "shop"}, "gaps": []}""");

        Assert.Equal("""{"name":"shop"}""", Client("da_live_agent").Setup()["app"]!.ToJsonString());
        Assert.Equal(("GET", "/api/v1/setup"), (_api.Requests[^1].Method, _api.Requests[^1].PathAndQuery));
    }

    [Fact]
    public void PostsChecksAsJson()
    {
        _api.Routes["/api/v1/deployments/commit%3Aabc1234/checks"] = (201, """{"id": 1, "deployment_id": 42}""");
        var result = Client().ReportCheck("commit:abc1234", "smoke", "pass", new[] { "GET /" });

        Assert.Equal(42, (int)result["deployment_id"]!);
        var request = _api.Requests[^1];
        Assert.Equal("/api/v1/deployments/commit%3Aabc1234/checks", request.PathAndQuery);
        Assert.Equal("""{"name":"smoke","status":"pass","covers":["GET /"]}""", JsonNode.Parse(request.Body)!.ToJsonString());
        Assert.Equal("application/json", request.Headers["Content-Type"]);
    }

    [Fact]
    public void RaisesNotFoundUnauthorizedAndOtherErrors()
    {
        var client = Client();
        Assert.Contains("deployment not found", Assert.Throws<NotFoundException>(() => client.Verification("99")).Message);

        _api.Routes["/api/v1/token"] = (401, """{"error": "invalid token"}""");
        Assert.Contains("invalid token", Assert.Throws<UnauthorizedException>(() => client.TokenInfo()).Message);

        _api.Routes["/api/v1/deployments/latest"] = (502, "<html>bad gateway</html>");
        Assert.Equal("unexpected response (HTTP 502)", Assert.Throws<ApiException>(() => client.LatestDeployment()).Message);
    }

    [Fact]
    public void RaisesWhenTheServerCantBeReached()
    {
        _api.Failure = new HttpRequestException("Connection refused (127.0.0.1:9)");
        Assert.Contains("could not reach", Assert.Throws<ApiException>(() => Client().TokenInfo()).Message);

        _api.Failure = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 2 seconds elapsing.");
        Assert.StartsWith($"could not reach {Endpoint}: TaskCanceledException:", Assert.Throws<ApiException>(() => Client().TokenInfo()).Message);
    }

    [Fact]
    public void RedeemsASetupCodeWithNoToken()
    {
        _api.Routes["/api/v1/setup_codes/redeem"] = (201, """{"app_token": "a", "api_token": "b"}""");
        var result = Client(null, requireToken: false).RedeemSetupCode("da_setup_x");

        Assert.Equal("""{"app_token":"a","api_token":"b"}""", result.ToJsonString());
        var (method, path, headers, body) = _api.Requests[^1];
        Assert.Equal(("POST", "/api/v1/setup_codes/redeem", """{"code":"da_setup_x"}"""), (method, path, JsonNode.Parse(body)!.ToJsonString()));
        Assert.False(headers.ContainsKey("Authorization"));
    }

    [Fact]
    public void NeedsAToken()
    {
        Assert.Contains("DEPLOYANGEL_API_TOKEN is not set", Assert.Throws<UnauthorizedException>(() => new ApiClient(null)).Message);
    }

    [Fact]
    public void QuotesQueryValuesAsPythonsUrlencodeDoes()
    {
        _api.Routes["/api/v1/late_regressions"] = (200, """{"late_regressions": []}""");
        Client().LateRegressions(since: "2026-10-01T00:00:00Z next", limit: 10);
        Assert.Equal("/api/v1/late_regressions?since=2026-10-01T00%3A00%3A00Z+next&limit=10", _api.Requests[^1].PathAndQuery);
    }
}
