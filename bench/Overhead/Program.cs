// Time and memory the agent adds: a request through its middleware, a job,
// fingerprinting an exception, and a minute with every list at its cap.
// Run with: dotnet run -c Release --project bench/Overhead
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using DeployAngel;
using DeployAngel.AspNetCore;
using DeployAngel.Core;
using DeployAngel.Integration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

const int N = 200_000;
var minute = 1_790_776_800.0;
AgentProcess.TransportForTests = _ => new Unreachable();
AgentProcess.ClockForTests = () => minute;
AgentProcess.Start(new DeployAngelOptions { Token = "bench", Enabled = true, Revision = "abc1234", FileDigests = false },
    "production", eager: false);
var agent = AgentProcess.Agent!;

var endpoint = new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse("/products/{id:int}"), 0,
    EndpointMetadataCollection.Empty, "products");
var middleware = new DeployAngelMiddleware(context =>
{
    context.SetEndpoint(endpoint);
    return Task.CompletedTask;
});
var bare = new DeployAngelMiddleware(context =>
{
    context.SetEndpoint(endpoint);
    return Task.CompletedTask;
});

DefaultHttpContext NewContext()
{
    var context = new DefaultHttpContext();
    context.Request.Method = "GET";
    context.Request.Host = new HostString("shop.example.com");
    return context;
}

// The same pipeline with the agent off, so only the agent's share is counted.
double PerRequest(DeployAngelMiddleware pipeline)
{
    for (var i = 0; i < 10_000; i++) pipeline.InvokeAsync(NewContext()).GetAwaiter().GetResult();
    var started = Stopwatch.GetTimestamp();
    for (var i = 0; i < N; i++) pipeline.InvokeAsync(NewContext()).GetAwaiter().GetResult();
    return Stopwatch.GetElapsedTime(started).TotalMicroseconds / N;
}

var with = PerRequest(middleware);
var options = AgentProcess.Options!;
options.Enabled = false;
AgentProcess.ResetForTests();
AgentProcess.TransportForTests = _ => new Unreachable();
AgentProcess.ClockForTests = () => minute;
var without = PerRequest(bare);
AgentProcess.Start(new DeployAngelOptions { Token = "bench", Enabled = true, Revision = "abc1234", FileDigests = false },
    "production", eager: false);
agent = AgentProcess.Agent!;
Console.WriteLine($"recording a request: {with - without:F2} µs ({with:F2} µs with the agent, {without:F2} µs without)");

var jobStarted = Stopwatch.GetTimestamp();
for (var i = 0; i < N; i++) Instrumentation.RecordJob("Shop.Jobs.Sync.Run", 12.5, queueLatencyMs: 40);
Console.WriteLine($"recording a job: {Stopwatch.GetElapsedTime(jobStarted).TotalMicroseconds / N:F2} µs");

Exception error;
try
{
    throw new InvalidOperationException("order 42 missing");
}
catch (InvalidOperationException caught)
{
    error = caught;
}
var map = AgentProcess.SourceMap;
var fingerprintStarted = Stopwatch.GetTimestamp();
for (var i = 0; i < 10_000; i++) Fingerprint.ForException(error, map);
Console.WriteLine($"fingerprinting an exception: {Stopwatch.GetElapsedTime(fingerprintStarted).TotalMicroseconds / 10_000:F1} µs");

// A fresh agent, so the memory counted is what it holds.
AgentProcess.ResetForTests();
AgentProcess.TransportForTests = _ => new Unreachable();
AgentProcess.ClockForTests = () => minute;
GC.Collect();
GC.WaitForPendingFinalizers();
var before = GC.GetTotalMemory(forceFullCollection: true);
AgentProcess.Start(new DeployAngelOptions { Token = "bench", Enabled = true, Revision = "abc1234", FileDigests = false },
    "production", eager: false);
agent = AgentProcess.Agent!;
for (var m = 0; m < 10; m++)
{
    minute = 1_790_776_800 + m * 60;
    for (var r = 0; r < 150; r++) agent.RecordRequest($"GET /r{r}", 500, 3);
    for (var j = 0; j < 150; j++) agent.RecordJob($"job{j}", 5);
    for (var c = 0; c < 120; c++) DeployAngelAgent.Checkpoint($"c{c}");
    for (var e = 0; e < 30; e++)
        agent.RecordExceptionDetails(new ExceptionDetails($"f{m}-{e}", $"E{e}", "x", "src/App.cs#Run", true),
            new[] { "src/App.cs#Run" });
    agent.Flush(includeCurrent: true);
}
var after = GC.GetTotalMemory(forceFullCollection: true);
GC.KeepAlive(agent);
Console.WriteLine($"memory with every list at its cap and {agent.Buffered} unsent minutes: {(after - before) / 1e6:F2} MB");

/// <summary>Every send fails, so minutes stay buffered and nothing leaves the machine.</summary>
internal sealed class Unreachable : ITransport
{
    public TransportResult Post(string path, byte[] body, TimeSpan? timeout = null) => new(TransportOutcome.Retry);
}
