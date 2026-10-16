using System.Text.Json;
using DeployAngel.Core;
using DeployAngel.Hangfire;
using Hangfire;
using Hangfire.States;

namespace DeployAngel.Tests;

public static class HangfireJobs
{
    public static void Succeed() => DeployAngelAgent.Checkpoint("invoice.sent");

    [AutomaticRetry(Attempts = 0)]
    public static void Fail() => throw new TimeoutException("Gateway timed out after 30s");

    [AutomaticRetry(Attempts = 1, DelaysInSeconds = new[] { 1 })]
    public static void FailOnce() => throw new TimeoutException("flaky");
}

public sealed class HangfireTests : IDisposable
{
    private static readonly object StorageLock = new();
    private static bool _storageReady;
    private readonly FakeTransport _transport = new();

    public HangfireTests()
    {
        AgentProcess.ResetForTests();
        AgentProcess.TransportForTests = _ => _transport;
        AgentProcess.SourceMapForTests = Maps.ForTests();
        // One storage for the whole run: Hangfire's static BackgroundJob
        // client keeps the storage it first saw.
        lock (StorageLock)
        {
            if (!_storageReady) GlobalConfiguration.Configuration.UseInMemoryStorage();
            _storageReady = true;
        }
        GlobalConfiguration.Configuration.UseDeployAngel();
        AgentProcess.Start(new DeployAngelOptions { Token = "t", Revision = "abcdef1" }, "production", eager: false);
    }

    public void Dispose() => AgentProcess.ResetForTests();

    [Fact]
    public async Task RecordsEachAttemptItsFailuresAndWhenAJobWontRunAgain()
    {
        var succeeded = BackgroundJob.Enqueue(() => HangfireJobs.Succeed());
        var failed = BackgroundJob.Enqueue(() => HangfireJobs.Fail());
        using (new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = 1 }))
        {
            await WaitFor(succeeded, SucceededState.StateName);
            await WaitFor(failed, FailedState.StateName);
        }
        AgentProcess.Agent!.Flush(includeCurrent: true);
        var payload = Assert.Single(_transport.Telemetry());
        Assert.Contains("jobs", payload.Array("capabilities").Select(c => c.GetString()));
        var classes = payload.Array("job_classes").ToDictionary(j => j.Str("key"));
        var success = classes["DeployAngel.Tests.HangfireJobs.Succeed"];
        Assert.Equal((1L, 0L, 0L), (success.Num("processed"), success.Num("failed"), success.Num("discarded")));
        Assert.Equal(1, success.GetProperty("queue_latency_histogram").Counts("counts").Values.Sum());
        var failure = classes["DeployAngel.Tests.HangfireJobs.Fail"];
        Assert.Equal((1L, 1L, 1L), (failure.Num("processed"), failure.Num("failed"), failure.Num("discarded")));
        var exception = Assert.Single(payload.Array("exceptions"));
        Assert.Equal(("System.TimeoutException", "Gateway timed out after 30s"), (exception.Str("exception_class"), exception.Str("message")));
        Assert.Equal("tests/DeployAngel.Tests/HangfireTests.cs#Fail", exception.Str("top_frame"));
        Assert.Equal(new Dictionary<string, long> { ["job_class:DeployAngel.Tests.HangfireJobs.Fail"] = 1 }, exception.Counts("sources"));
        var checkpoint = Assert.Single(payload.Array("checkpoints"));
        Assert.Equal((1L, 0L, 1L), (checkpoint.Num("count"), checkpoint.Num("http"), checkpoint.Num("job")));
    }

    [Fact]
    public async Task ARetriedAttemptIsAFailureNotADiscard()
    {
        var job = BackgroundJob.Enqueue(() => HangfireJobs.FailOnce());
        using (new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = 1, SchedulePollingInterval = TimeSpan.FromMilliseconds(200) }))
        {
            await WaitFor(job, ScheduledState.StateName);
        }
        AgentProcess.Agent!.Flush(includeCurrent: true);
        var stats = Assert.Single(_transport.Telemetry()).Array("job_classes").Single(j => j.Str("key") == "DeployAngel.Tests.HangfireJobs.FailOnce");
        Assert.Equal((1L, 1L, 0L), (stats.Num("processed"), stats.Num("failed"), stats.Num("discarded")));
    }

    [Fact]
    public void ListsRecurringJobsWithTheirSchedulesAndFiles()
    {
        RecurringJob.AddOrUpdate("nightly-invoices", () => HangfireJobs.Succeed(), Cron.Daily(4),
            new RecurringJobOptions { TimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York") });
        AgentProcess.Agent!.SendMetadata();
        var metadata = Assert.Single(_transport.Metadata());
        var schedule = metadata.Array("schedules").Single(s => s.Str("key") == "nightly-invoices");
        Assert.Equal(("DeployAngel.Tests.HangfireJobs.Succeed", "0 4 * * *", "hangfire", "America/New_York"),
            (schedule.Str("class"), schedule.Str("schedule"), schedule.Str("source"), schedule.Str("time_zone")));
        Assert.Contains("DeployAngel.Tests.HangfireJobs.Succeed", metadata.Array("job_classes").Select(j => j.GetString()));
        Assert.Equal(new[] { "tests/DeployAngel.Tests/HangfireTests.cs" },
            metadata.GetProperty("job_class_files").Array("DeployAngel.Tests.HangfireJobs.Succeed").Select(f => f.GetString()));
        RecurringJob.RemoveIfExists("nightly-invoices");
    }

    private static async Task WaitFor(string jobId, string state)
    {
        var monitoring = JobStorage.Current.GetMonitoringApi();
        for (var i = 0; i < 200; i++)
        {
            var details = monitoring.JobDetails(jobId);
            if (details?.History.Any(h => h.StateName == state) == true) return;
            await Task.Delay(50);
        }
        var history = monitoring.JobDetails(jobId)?.History.Select(h => $"{h.StateName}: {h.Reason}") ?? Array.Empty<string>();
        throw new TimeoutException($"job {jobId} never reached {state}: {string.Join(", ", history)}");
    }
}
