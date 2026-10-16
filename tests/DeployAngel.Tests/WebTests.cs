using System.Net;
using System.Text.Json;
using DeployAngel.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace DeployAngel.Tests;

public sealed class WebTests : IAsyncLifetime
{
    private readonly FakeClock _clock = new();
    private readonly FakeTransport _transport = new();
    private WebApplication? _app;
    private HttpClient? _client;

    public Task InitializeAsync()
    {
        AgentProcess.ResetForTests();
        AgentProcess.TransportForTests = _ => _transport;
        AgentProcess.ClockForTests = _clock.Func;
        AgentProcess.SourceMapForTests = Maps.ForTests();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        AgentProcess.ResetForTests();
    }

    private async Task Start(bool exceptionHandler = false, bool enabled = true)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddDeployAngel(options =>
        {
            options.Token = enabled ? "tok" : null;
            options.Revision = "abcdef1";
            options.IgnoredRoutes.Add("GET /ignored");
        });
        builder.Services.AddControllers().AddApplicationPart(typeof(WebTests).Assembly);
        builder.Services.AddHealthChecks();
        var app = builder.Build();
        if (exceptionHandler) app.UseExceptionHandler("/error");
        app.MapGet("/orders/{id:int}", (int id) => Results.Ok(new { id }));
        app.MapGet("/boom", string () => throw new InvalidOperationException("Order 42 exploded"));
        app.MapGet("/error", () => Results.Problem());
        app.MapGet("/ignored", () => "ignored");
        app.MapGet("/checkout", () =>
        {
            DeployAngelAgent.Checkpoint("order.placed");
            return "ok";
        });
        app.MapHealthChecks("/healthz");
        app.MapGet("/api/livez", () => "alive");
        app.MapControllers();
        app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
        await app.StartAsync();
        _app = app;
        _client = app.GetTestClient();
    }

    private async Task<JsonElement> Stopped()
    {
        await _app!.StopAsync();
        return Assert.Single(_transport.Telemetry());
    }

    private static JsonElement Route(JsonElement payload, string key) =>
        payload.Array("routes").Single(r => r.Str("key") == key);

    [Fact]
    public async Task RecordsRequestsByTheirRoutePattern()
    {
        await Start();
        Assert.Equal(HttpStatusCode.OK, (await _client!.GetAsync("/orders/5")).StatusCode);
        await _client.GetAsync("/orders/6");
        await _client.GetAsync("/api/widgets/7");
        await _client.GetAsync("/Home/About");
        var payload = await Stopped();
        Assert.Equal(2, Route(payload, "GET /orders/{id}").Num("requests"));
        Assert.Equal(1, Route(payload, "GET /api/widgets/{id}").Num("requests"));
        Assert.Equal(1, Route(payload, "GET /Home/About/{id?}").Num("requests"));
        Assert.Equal(4, payload.GetProperty("http").Num("requests"));
        Assert.Equal("aspnetcore", payload.GetProperty("runtime").Str("framework"));
        Assert.Equal("abcdef1", payload.GetProperty("release").Str("commit"));
    }

    [Fact]
    public async Task RecordsUnmatchedErrorsOutsideTheTotals()
    {
        await Start();
        Assert.Equal(HttpStatusCode.NotFound, (await _client!.GetAsync("/wp-admin")).StatusCode);
        await _client.GetAsync("/orders/1");
        var payload = await Stopped();
        Assert.Equal(1, payload.GetProperty("http").Num("requests"));
        Assert.Empty(payload.GetProperty("http").Counts("status_counts"));
        Assert.Equal(new Dictionary<string, long> { ["404"] = 1 }, Route(payload, "GET unmatched").Counts("status_counts"));
    }

    [Fact]
    public async Task RecordsAnUnhandledExceptionAgainstItsRoute()
    {
        await Start();
        await Assert.ThrowsAnyAsync<Exception>(() => _client!.GetAsync("/boom"));
        var payload = await Stopped();
        Assert.Equal(1, payload.GetProperty("http").Num("unhandled_exceptions"));
        Assert.Equal(new Dictionary<string, long> { ["500"] = 1 }, Route(payload, "GET /boom").Counts("status_counts"));
        var exception = Assert.Single(payload.Array("exceptions"));
        Assert.Equal("System.InvalidOperationException", exception.Str("exception_class"));
        Assert.Equal("Order <n> exploded", exception.Str("message"));
        Assert.Equal("tests/DeployAngel.Tests/WebTests.cs#Start", exception.Str("top_frame"));
        Assert.True(exception.GetProperty("app_frame").GetBoolean());
        Assert.Equal(new Dictionary<string, long> { ["route:GET /boom"] = 1 }, exception.Counts("sources"));
    }

    [Fact]
    public async Task KeepsTheRouteThatFailedWhenTheExceptionHandlerRendersTheError()
    {
        await Start(exceptionHandler: true);
        Assert.Equal(HttpStatusCode.InternalServerError, (await _client!.GetAsync("/boom")).StatusCode);
        var payload = await Stopped();
        Assert.Equal(new Dictionary<string, long> { ["500"] = 1 }, Route(payload, "GET /boom").Counts("status_counts"));
        Assert.DoesNotContain(payload.Array("routes"), r => r.Str("key") == "GET /error");
        Assert.Equal(1, payload.GetProperty("http").Num("unhandled_exceptions"));
        Assert.Equal("route:GET /boom", Assert.Single(payload.Array("exceptions")).Counts("sources").Keys.Single());
    }

    [Fact]
    public async Task LeavesOutHealthChecksAndIgnoredRoutes()
    {
        await Start();
        await _client!.GetAsync("/healthz");
        await _client.GetAsync("/api/livez");
        await _client.GetAsync("/ignored");
        var payload = await Stopped();
        Assert.Empty(payload.Array("routes"));
        Assert.Equal(0, payload.GetProperty("http").Num("requests"));
    }

    [Fact]
    public async Task CheckpointsInARequestSaySo()
    {
        await Start();
        await _client!.GetAsync("/checkout");
        var payload = await Stopped();
        var checkpoint = Assert.Single(payload.Array("checkpoints"));
        Assert.Equal((1L, 1L, 0L), (checkpoint.Num("count"), checkpoint.Num("http"), checkpoint.Num("job")));
        Assert.Contains("checkpoints", payload.Array("capabilities").Select(c => c.GetString()));
    }

    [Fact]
    public async Task ListsTheAppsRoutesWithTheFilesThatImplementThem()
    {
        await Start();
        var agent = AgentProcess.Agent!;
        agent.SendMetadata();
        var metadata = Assert.Single(_transport.Metadata());
        var routes = metadata.Array("routes").ToDictionary(r => r.Str("key"));
        Assert.Equal(new[] { "tests/DeployAngel.Tests/WebTests.cs" }, routes["GET /orders/{id}"].Array("files").Select(f => f.GetString()));
        var widget = routes["GET /api/widgets/{id}"];
        Assert.Equal(("DeployAngel.Tests.WidgetsController", "Show"), (widget.Str("controller"), widget.Str("action")));
        Assert.Equal(new[] { "tests/DeployAngel.Tests/WebTests.cs" }, widget.Array("files").Select(f => f.GetString()));
        Assert.Contains("ANY /Home/About/{id?}", routes.Keys);
        Assert.Contains("POST /api/widgets", routes.Keys);
        Assert.DoesNotContain("GET /healthz", routes.Keys);
        Assert.DoesNotContain("GET /api/livez", routes.Keys);
        Assert.DoesNotContain("GET /ignored", routes.Keys);
        Assert.Equal("dotnet", metadata.GetProperty("runtime").Str("language"));
    }

    [Fact]
    public async Task DoesNothingWithoutAToken()
    {
        await Start(enabled: false);
        await _client!.GetAsync("/orders/5");
        await _app!.StopAsync();
        Assert.Empty(_transport.Posts);
    }
}

[ApiController]
[Route("api/widgets")]
public sealed class WidgetsController : ControllerBase
{
    [HttpGet("{id}")]
    public IActionResult Show(int id) => Ok(new { id });

    [HttpPost]
    public IActionResult Create() => Ok();
}

public sealed class HomeController : Controller
{
    public IActionResult About() => Content("about");
}
