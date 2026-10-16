using DeployAngel.Core;

namespace DeployAngel.Tests;

public class HistogramTests
{
    [Fact]
    public void UsesTheCloudsBuckets()
    {
        // The same values as the Ruby and Python agents' buckets.
        Assert.Equal(new[] { 0, 0, 1, 1, 8, 49, 58, 218 },
            new[] { 0.5, 1, 1.05, 1.1, 2, 100, 250.5, 1e9 }.Select(Histogram.BucketFor));
        Assert.Equal(47, Histogram.BucketFor(84));
        Assert.Equal(Histogram.MaxBucket, Histogram.BucketFor(1e30));
        Assert.Equal(0, Histogram.BucketFor(double.NaN));
    }

    [Fact]
    public void SerializesSparseCountsWithTheScheme()
    {
        var histogram = new Histogram();
        for (var i = 0; i < 3; i++) histogram.Record(84);
        histogram.Record(1000);
        var json = new JsonOut();
        histogram.Write(json);
        Assert.Equal("{\"scheme\":\"log1.1_ms_v1\",\"counts\":{\"47\":3,\"73\":1}}", System.Text.Encoding.UTF8.GetString(json.ToBytes()));
    }
}

public class AggregatorTests
{
    private readonly FakeClock _clock = new();

    private static ExceptionDetails Details(string fingerprint) =>
        new(fingerprint, "Shop.PaymentFailed", null, "src/Shop/Payments.cs#Charge", true);

    [Fact]
    public void AccumulatesRequestsIntoTheCurrentMinute()
    {
        var aggregator = new Aggregator(100, _clock.Func);
        aggregator.Record("GET /orders/{id}", 200, 12);
        aggregator.Record("GET /orders/{id}", 500, 40, unhandled: true);
        aggregator.Record("POST /orders", 422, 8);
        _clock.Advance(60);
        var period = Assert.Single(aggregator.Drain());
        Assert.Equal(3, period.Requests);
        Assert.Equal(new Dictionary<string, long> { ["500"] = 1, ["422"] = 1 }, period.StatusCounts);
        Assert.Equal(1, period.UnhandledExceptions);
        Assert.Equal(2, period.Routes["GET /orders/{id}"].Requests);
        Assert.Equal(3, period.Histogram.Count());
    }

    [Fact]
    public void LeavesRequestsOutOfTotalsWhenAsked()
    {
        var aggregator = new Aggregator(100, _clock.Func);
        aggregator.Record("GET unmatched", 404, 3, inTotals: false);
        _clock.Advance(60);
        var period = Assert.Single(aggregator.Drain());
        Assert.Equal(0, period.Requests);
        Assert.Empty(period.StatusCounts);
        Assert.Equal(1, period.Routes["GET unmatched"].StatusCounts["404"]);
    }

    [Fact]
    public void KeepsTheMinuteInProgressUnlessAsked()
    {
        var aggregator = new Aggregator(100, _clock.Func);
        aggregator.Record("GET /", 200, 1);
        Assert.Empty(aggregator.Drain());
        Assert.Equal(1, Assert.Single(aggregator.Drain(includeCurrent: true)).Requests);
    }

    [Fact]
    public void ReportsIdleMinutesAndNeverDrainsOneTwice()
    {
        var aggregator = new Aggregator(100, _clock.Func);
        _clock.Advance(180);
        var periods = aggregator.Drain();
        Assert.Equal(3, periods.Count);
        Assert.All(periods, p => Assert.Equal(0, p.Requests));
        Assert.Equal(new[] { 60L, 60L }, periods.Zip(periods.Skip(1), (a, b) => b.StartedAt - a.StartedAt));
        Assert.Empty(aggregator.Drain());
    }

    [Fact]
    public void CapsPeriodsAfterALongPause()
    {
        var aggregator = new Aggregator(100, _clock.Func);
        _clock.Advance(3600);
        Assert.Equal(10, aggregator.Drain(maxPeriods: 10).Count);
    }

    [Fact]
    public void FoldsRoutesBeyondTheCap()
    {
        var aggregator = new Aggregator(3, _clock.Func);
        foreach (var route in new[] { "GET /a", "GET /b", "GET /c", "GET /d", "GET /a" }) aggregator.Record(route, 200, 1);
        _clock.Advance(60);
        var routes = Assert.Single(aggregator.Drain()).Routes;
        Assert.Equal(new[] { "GET /a", "GET /b", Aggregator.Other }, routes.Keys);
        Assert.Equal(2, routes["GET /a"].Requests);
        Assert.Equal(2, routes[Aggregator.Other].Requests);
    }

    [Fact]
    public void CountsJobAttemptsAndDiscards()
    {
        var aggregator = new Aggregator(100, _clock.Func);
        aggregator.RecordJob("Shop.Jobs.Invoice.Send", 30, queueLatencyMs: 5);
        aggregator.RecordJob("Shop.Jobs.Invoice.Send", 50, failed: true);
        aggregator.RecordDiscard("Shop.Jobs.Invoice.Send");
        _clock.Advance(60);
        var period = Assert.Single(aggregator.Drain());
        var stats = period.JobClasses["Shop.Jobs.Invoice.Send"];
        Assert.Equal((2, 1, 1), (stats.Processed, stats.Failed, stats.Discarded));
        Assert.Equal(2, stats.Duration.Count());
        Assert.Equal(1, stats.QueueLatency.Count());
        Assert.Equal(2, period.Jobs.Processed);
    }

    [Fact]
    public void KeepsABacktraceOnlyTheFirstTime()
    {
        var aggregator = new Aggregator(100, _clock.Func);
        var trace = new[] { "src/Shop/Payments.cs#Charge" };
        aggregator.RecordException(Details("a"), "route:POST /pay", backtrace: trace);
        aggregator.RecordException(Details("a"), "route:POST /pay", handled: true, backtrace: trace);
        _clock.Advance(60);
        var entry = Assert.Single(aggregator.Drain()).Exceptions["a"];
        Assert.Equal((1L, 1L), (entry.Count, entry.HandledCount));
        Assert.Equal(2, entry.Sources["route:POST /pay"]);
        Assert.Equal(trace, entry.Backtrace);
        aggregator.RecordException(Details("a"), backtrace: trace);
        _clock.Advance(60);
        Assert.Null(Assert.Single(aggregator.Drain()).Exceptions["a"].Backtrace);
    }

    [Fact]
    public void CountsOnlyTwentyFingerprintsAPeriod()
    {
        var aggregator = new Aggregator(100, _clock.Func);
        for (var i = 0; i < 25; i++) aggregator.RecordException(Details($"f{i}"));
        _clock.Advance(60);
        var period = Assert.Single(aggregator.Drain());
        Assert.Equal(20, period.Exceptions.Count);
        Assert.Equal(5, period.ExceptionsTruncated);
    }

    [Fact]
    public void CountsCheckpointsByWhereTheyWereRecorded()
    {
        var aggregator = new Aggregator(100, _clock.Func);
        aggregator.RecordCheckpoint("order.placed", 2, UnitOfWork.Http);
        aggregator.RecordCheckpoint("order.placed", 1, UnitOfWork.Job);
        aggregator.RecordCheckpoint("order.placed");
        _clock.Advance(60);
        var counts = Assert.Single(aggregator.Drain()).Checkpoints["order.placed"];
        Assert.Equal((4L, 2L, 1L), (counts.Count, counts.Http, counts.Job));
    }

    [Fact]
    public void FoldsCheckpointsBeyondTheCapWithWhereTheyWereRecorded()
    {
        var aggregator = new Aggregator(100, _clock.Func);
        for (var i = 0; i < Aggregator.MaxCheckpoints + 5; i++) aggregator.RecordCheckpoint($"event.{i}", 1, UnitOfWork.Http);
        _clock.Advance(60);
        var checkpoints = Assert.Single(aggregator.Drain()).Checkpoints;
        Assert.Equal(Aggregator.MaxCheckpoints, checkpoints.Count);
        Assert.Equal(6, checkpoints[Aggregator.Other].Count);
        Assert.Equal(6, checkpoints[Aggregator.Other].Http);
    }
}

public class UnitOfWorkTests
{
    [Fact]
    public void NestsAndTheInnermostWins()
    {
        Assert.Null(UnitOfWork.Current);
        var outer = UnitOfWork.Begin(UnitOfWork.Http);
        var inner = UnitOfWork.Begin(UnitOfWork.Job);
        Assert.Equal(UnitOfWork.Job, UnitOfWork.Current);
        UnitOfWork.End(inner);
        Assert.Equal(UnitOfWork.Http, UnitOfWork.Current);
        UnitOfWork.End(outer);
        Assert.Null(UnitOfWork.Current);
    }

    [Fact]
    public async Task AsyncFlowsAreSeparate()
    {
        var seen = new string?[2];
        var ready = new SemaphoreSlim(0);
        var a = Task.Run(async () =>
        {
            UnitOfWork.Begin(UnitOfWork.Http);
            ready.Release();
            await Task.Delay(20);
            seen[0] = UnitOfWork.Current;
        });
        await ready.WaitAsync();
        var b = Task.Run(() => seen[1] = UnitOfWork.Current);
        await Task.WhenAll(a, b);
        Assert.Equal(UnitOfWork.Http, seen[0]);
        Assert.Null(seen[1]);
    }
}

public class BufferTests
{
    [Fact]
    public void DropsTheOldestWhenFull()
    {
        var buffer = new PayloadBuffer(2);
        buffer.Push(new byte[] { 1 });
        buffer.Push(new byte[] { 2 });
        buffer.Push(new byte[] { 3 });
        Assert.Equal(1, buffer.Dropped);
        Assert.Equal(new byte[] { 2 }, buffer.Shift());
        buffer.Unshift(new byte[] { 2 });
        buffer.Unshift(new byte[] { 9 });
        Assert.Equal(2, buffer.Count);
        Assert.Equal(new byte[] { 2 }, buffer.Shift());
    }
}

public class RunningJobsTests
{
    [Fact]
    public void ReportEachClassAndItsOldestJob()
    {
        var clock = new FakeClock();
        var jobs = new RunningJobs(clock.Func);
        var first = jobs.Start("Import");
        clock.Advance(2);
        jobs.Start("Import");
        var other = jobs.Start("Mail");
        clock.Advance(1);
        Assert.Equal((2, 3000L), jobs.Snapshot(clock.Now)["Import"]);
        jobs.Finish(first);
        jobs.Finish(other);
        Assert.Equal((1, 1000L), Assert.Single(jobs.Snapshot(clock.Now)).Value);
        Assert.Empty(jobs.Snapshot(clock.Now - 5));
    }

    [Fact]
    public void StopTrackingPastTheirLimit()
    {
        var clock = new FakeClock();
        var jobs = new RunningJobs(clock.Func);
        for (var i = 0; i < RunningJobs.Max + 10; i++) jobs.Start("Import");
        Assert.Equal(RunningJobs.Max, jobs.Snapshot(clock.Now)["Import"].Count);
    }
}

public class OptionsTests
{
    [Fact]
    public void ReportsOnlyWithATokenAndInListedEnvironments()
    {
        Assert.False(new DeployAngelOptions().IsActive("production"));
        var options = new DeployAngelOptions { Token = "t" };
        Assert.True(options.IsActive("production"));
        Assert.False(options.IsActive("staging"));
        options.Enabled = true;
        Assert.True(options.IsActive("development"));
        options.Enabled = false;
        Assert.False(options.IsActive("production"));
    }

    [Fact]
    public void ReadsEnvironmentVariables()
    {
        var options = DeployAngelOptions.FromEnvironment(Env.Of(("DEPLOYANGEL_TOKEN", "tok"), ("DEPLOYANGEL_ENABLED", "yes"),
            ("DEPLOYANGEL_EXCEPTION_MESSAGES", "0"), ("DEPLOYANGEL_URL", "")));
        Assert.Equal("tok", options.Token);
        Assert.True(options.Enabled);
        Assert.False(options.ExceptionMessages);
        Assert.True(options.FileDigests);
        Assert.Equal(DeployAngelOptions.DefaultEndpoint, options.Endpoint);
    }

    [Fact]
    public void IgnoresHeadWithTheGetRoute()
    {
        var options = new DeployAngelOptions { IgnoredRoutes = { "GET /status" } };
        Assert.True(options.IsIgnoredRoute("GET /status"));
        Assert.True(options.IsIgnoredRoute("HEAD /status"));
        Assert.False(options.IsIgnoredRoute("POST /status"));
    }
}
