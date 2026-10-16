using System.Text.Json;
using DeployAngel.Core;
using DeployAngel.Integration;

namespace DeployAngel.Tests;

public sealed class AgentTests : IDisposable
{
    private const string Sha = "3f2a9c1d4e5b6a7980f1e2d3c4b5a69788f9e0d1";
    private readonly FakeClock _clock = new();
    private readonly FakeTransport _transport = new();

    public void Dispose() => AgentProcess.ResetForTests();

    private Agent NewAgent(DeployAngelOptions? options = null, IReadOnlyDictionary<string, string?>? env = null,
        Metadata? metadata = null, CapabilitySet? capabilities = null, string environment = "production")
    {
        options ??= new DeployAngelOptions { Token = "tok" };
        return new Agent(options, environment, Directory.CreateTempSubdirectory().FullName,
            new RuntimeInfo("dotnet", "10.0.12", "aspnetcore", "10.0.12"),
            env ?? Env.Of(("HEROKU_BUILD_COMMIT", Sha), ("HEROKU_RELEASE_VERSION", "v12"), ("DYNO", "web.1")),
            Maps.ForTests(), _transport, _clock.Func, capabilities: capabilities, metadata: metadata, http: _ => null);
    }

    private static Metadata NewMetadata(DeployAngelOptions options, SourceMap? map = null, params IMetadataSource[] sources) =>
        new(options, map ?? Maps.ForTests(), sources);

    [Fact]
    public void SendsCompletedMinutesAsProtocolV1()
    {
        var agent = NewAgent();
        agent.RecordRequest("GET /orders/{id}", 200, 84);
        agent.RecordRequest("GET /orders/{id}", 503, 1000, unhandled: true);
        _clock.Advance(60);
        Assert.Equal(1, agent.Flush());
        var payload = Assert.Single(_transport.Telemetry());
        Assert.Equal(1, payload.Num("protocol_version"));
        Assert.Equal("deployangel-dotnet", payload.GetProperty("agent").Str("name"));
        Assert.Equal("dotnet", payload.GetProperty("runtime").Str("language"));
        Assert.Equal("aspnetcore", payload.GetProperty("runtime").Str("framework"));
        var instance = payload.GetProperty("instance");
        Assert.Equal(("web.1", "web"), (instance.Str("host"), instance.Str("process_type")));
        Assert.StartsWith("web.1:", instance.Str("id"));
        Assert.Equal(("v12", Sha, "heroku_dyno_metadata"),
            (payload.GetProperty("release").Str("version"), payload.GetProperty("release").Str("commit"),
                payload.GetProperty("release").Str("source")));
        Assert.Equal(new[] { "http", "exceptions" }, payload.Array("capabilities").Select(c => c.GetString()));
        Assert.Equal("2026-10-16T12:00:00Z", payload.GetProperty("period").Str("started_at"));
        Assert.Equal(60, payload.GetProperty("period").Num("duration_seconds"));
        var http = payload.GetProperty("http");
        Assert.Equal(2, http.Num("requests"));
        Assert.Equal(new Dictionary<string, long> { ["503"] = 1 }, http.Counts("status_counts"));
        Assert.Equal(1, http.Num("unhandled_exceptions"));
        Assert.Equal(new Dictionary<string, long> { ["47"] = 1, ["73"] = 1 },
            http.GetProperty("latency_histogram").Counts("counts"));
        var route = Assert.Single(payload.Array("routes"));
        Assert.Equal(("GET /orders/{id}", 2L), (route.Str("key"), route.Num("requests")));
        Assert.Equal(0, payload.GetProperty("jobs").Num("processed"));
        Assert.False(payload.GetProperty("jobs").TryGetProperty("running", out _));
        Assert.Empty(payload.Array("exceptions"));
        Assert.Empty(payload.Array("job_classes"));
        Assert.Empty(payload.Array("checkpoints"));
    }

    [Fact]
    public void DoesNothingWithoutAToken()
    {
        var agent = NewAgent(new DeployAngelOptions());
        Assert.False(agent.Active);
        agent.RecordRequest("GET /", 200, 1);
        _clock.Advance(60);
        Assert.Equal(0, agent.Flush());
        Assert.Empty(_transport.Posts);
    }

    [Fact]
    public void DoesNothingOutsideTheListedEnvironments()
    {
        Assert.False(NewAgent(environment: "development").Active);
    }

    [Fact]
    public void KeepsPayloadsForLaterWhenTheCloudIsUnreachable()
    {
        var agent = NewAgent();
        _transport.Respond = _ => new TransportResult(TransportOutcome.Retry);
        _clock.Advance(120);
        Assert.Equal(0, agent.Flush());
        Assert.Equal(2, agent.Buffered);
        _transport.Respond = null;
        Assert.Equal(2, agent.Flush());
        Assert.Equal(0, agent.Buffered);
    }

    [Fact]
    public void PausesAfterRateLimiting()
    {
        var agent = NewAgent();
        _transport.Results.Enqueue(new TransportResult(TransportOutcome.Drop, 429, RetryAfter: 120));
        _clock.Advance(120);
        Assert.Equal(0, agent.Flush());
        Assert.Single(_transport.Posts);
        Assert.Equal(1, agent.Buffered);
        _clock.Advance(60);
        agent.Flush();
        Assert.Single(_transport.Posts);
        _clock.Advance(61);
        Assert.Equal(3, agent.Flush());
    }

    [Fact]
    public void RecordsAnExceptionOnceWithItsSource()
    {
        var agent = NewAgent();
        var error = new InvalidOperationException("Order 42 failed");
        agent.RecordException(error, "route:POST /orders");
        agent.RecordException(error, "route:POST /orders");
        agent.RecordException(new InvalidOperationException("Order 43 failed"), handled: true);
        _clock.Advance(60);
        agent.Flush();
        var exception = Assert.Single(Assert.Single(_transport.Telemetry()).Array("exceptions"));
        Assert.Equal(("System.InvalidOperationException", "Order <n> failed"), (exception.Str("exception_class"), exception.Str("message")));
        Assert.Equal((1L, 1L), (exception.Num("count"), exception.Num("handled_count")));
        Assert.Equal(new Dictionary<string, long> { ["route:POST /orders"] = 1 }, exception.Counts("sources"));
        Assert.Equal(1, exception.Num("fingerprint_version"));
    }

    [Fact]
    public void LeavesMessagesOutWhenTheyAreTurnedOff()
    {
        var agent = NewAgent(new DeployAngelOptions { Token = "t", ExceptionMessages = false });
        agent.RecordException(new InvalidOperationException("Patient Jane Doe"));
        _clock.Advance(60);
        agent.Flush();
        var exception = Assert.Single(Assert.Single(_transport.Telemetry()).Array("exceptions"));
        Assert.False(exception.TryGetProperty("message", out _));
    }

    [Fact]
    public void ChecksAreCountedAndAnnounced()
    {
        var capabilities = new CapabilitySet();
        var agent = NewAgent(capabilities: capabilities);
        capabilities.Add("checkpoints");
        agent.RecordCheckpoint("order.fulfilled", 2);
        _clock.Advance(60);
        agent.Flush();
        var payload = Assert.Single(_transport.Telemetry());
        Assert.Contains("checkpoints", payload.Array("capabilities").Select(c => c.GetString()));
        var checkpoint = Assert.Single(payload.Array("checkpoints"));
        Assert.Equal(("order.fulfilled", 2L, 0L, 0L), (checkpoint.Str("key"), checkpoint.Num("count"), checkpoint.Num("http"), checkpoint.Num("job")));
    }

    [Fact]
    public async Task CheckpointsReportWhetherTheyWereRecordedInARequestOrAJob()
    {
        var agent = NewAgent();
        await Task.Run(() =>
        {
            var unit = UnitOfWork.Begin(UnitOfWork.Http);
            agent.RecordCheckpoint("order.placed");
            UnitOfWork.End(unit);
        });
        using (Instrumentation.BeginJob()) agent.RecordCheckpoint("order.placed");
        agent.RecordCheckpoint("order.placed");
        _clock.Advance(60);
        agent.Flush();
        var checkpoint = Assert.Single(Assert.Single(_transport.Telemetry()).Array("checkpoints"));
        Assert.Equal((3L, 1L, 1L), (checkpoint.Num("count"), checkpoint.Num("http"), checkpoint.Num("job")));
    }

    [Fact]
    public void SendsMetadataOnceAndFilesOnlyWhenTheCloudAsks()
    {
        var options = new DeployAngelOptions
        {
            Token = "t",
            CriticalFlows = { ["checkout"] = new[] { "POST /orders" } },
            IgnoredRoutes = { "GET /status" },
        };
        var source = new StaticSource(
            new[] { new RouteInfo("GET /orders/{id}", "Shop.OrdersController", "Show", new[] { "src/Shop/OrdersController.cs" }),
                    new RouteInfo("GET /status"), new RouteInfo("GET /orders/{id}") },
            new[] { "Shop.Jobs.Invoice.Send" });
        var agent = NewAgent(options, metadata: NewMetadata(options, null, source));
        _transport.Results.Enqueue(new TransportResult(TransportOutcome.Ok, 202,
            Body: JsonDocument.Parse("""{"accepted":true,"manifest_needed":true}""").RootElement));
        agent.SendMetadata();
        agent.SendMetadata();
        var posts = _transport.Metadata();
        Assert.Equal(2, posts.Count);
        var first = posts[0];
        Assert.Equal(new[] { "GET /orders/{id}" }, first.Array("routes").Select(r => r.Str("key")));
        var route = first.Array("routes").Single();
        Assert.Equal(("Shop.OrdersController", "Show"), (route.Str("controller"), route.Str("action")));
        Assert.Equal(new[] { "Shop.Jobs.Invoice.Send" }, first.Array("job_classes").Select(j => j.GetString()));
        Assert.Equal(new[] { "POST /orders" }, first.GetProperty("critical_flows").Array("checkout").Select(i => i.GetString()));
        Assert.False(first.TryGetProperty("files", out _));
        var manifest = first.GetProperty("file_manifest");
        Assert.True(manifest.Num("count") > 0);
        Assert.Matches("^[0-9a-f]{64}$", manifest.Str("hash"));
        var files = posts[1].GetProperty("files").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
        Assert.Contains("tests/DeployAngel.Tests/AgentTests.cs", files.Keys);
        Assert.Matches("^[0-9a-f]{16}$", files["tests/DeployAngel.Tests/AgentTests.cs"]);
        Assert.DoesNotContain(files.Keys, path => path.Contains("/obj/"));
    }

    [Fact]
    public void RetriesMetadataUntilItIsAccepted()
    {
        var options = new DeployAngelOptions { Token = "t" };
        var agent = NewAgent(options, metadata: NewMetadata(options));
        _transport.Results.Enqueue(new TransportResult(TransportOutcome.Retry));
        agent.SendMetadata();
        agent.SendMetadata();
        agent.SendMetadata();
        Assert.Equal(2, _transport.Metadata().Count);
    }

    [Fact]
    public void FingerprintsTheCodeBeforeTheFirstSendNotAtBoot()
    {
        var options = new DeployAngelOptions { Token = "t" };
        var agent = NewAgent(options, Env.Empty, NewMetadata(options));
        Assert.True(agent.Release.Pending);
        _clock.Advance(60);
        agent.Flush();
        var release = Assert.Single(_transport.Telemetry()).GetProperty("release");
        Assert.Equal("code_fingerprint", release.Str("source"));
        Assert.StartsWith("code:", release.Str("version"));
        Assert.False(agent.Release.Pending);
    }

    [Fact]
    public void WithoutFileDigestsTheReleaseIsUnknown()
    {
        var options = new DeployAngelOptions { Token = "t", FileDigests = false };
        var agent = NewAgent(options, Env.Empty, NewMetadata(options));
        _clock.Advance(60);
        agent.Flush();
        Assert.Equal("unknown", Assert.Single(_transport.Telemetry()).GetProperty("release").Str("source"));
    }

    [Fact]
    public void WithoutDebugSymbolsTheReleaseIsUnknown()
    {
        var options = new DeployAngelOptions { Token = "t" };
        var agent = NewAgent(options, Env.Empty, NewMetadata(options, new SourceMap(new[] { "NoSuchAssembly" }, "/build")));
        _clock.Advance(60);
        agent.Flush();
        Assert.Equal("unknown", Assert.Single(_transport.Telemetry()).GetProperty("release").Str("source"));
    }

    [Fact]
    public void ANamedReleaseSkipsTheFingerprint()
    {
        var options = new DeployAngelOptions { Token = "t", Revision = Sha };
        var agent = NewAgent(options, Env.Empty, NewMetadata(options));
        Assert.False(agent.Release.Pending);
        Assert.Equal("config", agent.ResolvedRelease().Source);
    }

    [Fact]
    public void ReportsJobsStillRunningOnTheLatestMinuteOnly()
    {
        var agent = NewAgent();
        var token = agent.JobStarted("Shop.Jobs.Import.Run");
        _clock.Advance(130);
        agent.Flush();
        var payloads = _transport.Telemetry();
        Assert.Equal(2, payloads.Count);
        Assert.False(payloads[0].GetProperty("jobs").TryGetProperty("running", out _));
        var jobs = payloads[1].GetProperty("jobs");
        Assert.Equal((1L, 110_000L), (jobs.Num("running"), jobs.Num("oldest_running_ms")));
        var jobClass = Assert.Single(payloads[1].Array("job_classes"));
        Assert.Equal(("Shop.Jobs.Import.Run", 0L, 1L), (jobClass.Str("key"), jobClass.Num("processed"), jobClass.Num("running")));
        agent.JobFinished(token);
        _clock.Advance(60);
        agent.Flush();
        Assert.False(_transport.Telemetry()[2].GetProperty("jobs").TryGetProperty("running", out _));
    }

    [Fact]
    public void ShutdownSendsTheMinuteInProgress()
    {
        var agent = NewAgent();
        agent.RecordRequest("GET /", 200, 3);
        agent.Shutdown();
        Assert.Equal(1, Assert.Single(_transport.Telemetry()).GetProperty("http").Num("requests"));
    }

    [Fact]
    public void ShutdownGivesUpAtItsDeadline()
    {
        var agent = NewAgent();
        agent.RecordRequest("GET /", 200, 3);
        agent.Shutdown(TimeSpan.Zero);
        Assert.Empty(_transport.Telemetry());
    }

    [Fact]
    public void WarnsOnceWhenTheTokenIsRejected()
    {
        var logger = new ListLogger();
        var agent = NewAgent(new DeployAngelOptions { Token = "t", Logger = logger });
        _transport.Respond = _ => new TransportResult(TransportOutcome.Drop, 401);
        _clock.Advance(180);
        agent.Flush();
        Assert.Single(logger.Messages, m => m.Contains("rejected the token"));
        Assert.Equal(0, agent.Buffered);
    }

    private sealed class StaticSource : IMetadataSource
    {
        private readonly RouteInfo[] _routes;
        private readonly string[] _jobs;

        public StaticSource(RouteInfo[] routes, string[] jobs)
        {
            _routes = routes;
            _jobs = jobs;
        }

        public IEnumerable<RouteInfo> Routes() => _routes;
        public IEnumerable<string> JobClasses() => _jobs;
    }
}

internal sealed class ListLogger : Microsoft.Extensions.Logging.ILogger
{
    public List<string> Messages { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
        TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Messages) Messages.Add(formatter(state, exception));
    }
}
