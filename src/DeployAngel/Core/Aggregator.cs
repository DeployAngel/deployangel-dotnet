using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DeployAngel.Core;

internal sealed class RouteStats
{
    public long Requests;
    public readonly Dictionary<string, long> StatusCounts = new();
    public readonly Histogram Histogram = new();
}

/// <summary>
/// Running and OldestRunningMs: jobs still running as the period is sent,
/// and how long the oldest had been running at its end.
/// </summary>
internal sealed class JobStats
{
    public long Processed;
    public long Failed;
    public long Discarded;
    public readonly Histogram Duration = new();
    public readonly Histogram QueueLatency = new();
    public long Running;
    public long? OldestRunningMs;

    public void AddRunning(int count, long oldestMs)
    {
        Running += count;
        OldestRunningMs = OldestRunningMs is null ? oldestMs : Math.Max(OldestRunningMs.Value, oldestMs);
    }

    public void Record(bool failed, bool discarded, double durationMs, double? queueLatencyMs)
    {
        Processed++;
        if (failed) Failed++;
        if (discarded) Discarded++;
        Duration.Record(durationMs);
        if (queueLatencyMs is not null) QueueLatency.Record(queueLatencyMs.Value);
    }

    public void RecordDiscard() => Discarded++;
}

/// <summary>An exception fingerprint, as computed by <see cref="Fingerprint"/>.</summary>
internal sealed record ExceptionDetails(
    string Fingerprint, string ExceptionClass, string? Message, string TopFrame, bool AppFrame)
{
    public int FingerprintVersion => Core.Fingerprint.Version;
}

internal sealed class ExceptionEntry
{
    public ExceptionEntry(ExceptionDetails details)
    {
        Details = details;
    }

    public ExceptionDetails Details { get; }
    public long Count;
    public long HandledCount;
    public readonly Dictionary<string, long> Sources = new();
    public IReadOnlyList<string>? Backtrace;
}

internal sealed class CheckpointCounts
{
    public long Count;
    // How many of Count were recorded while handling an HTTP request or
    // running a job; the rest were neither.
    public long Http;
    public long Job;
}

internal sealed class Period
{
    public Period(long startedAt)
    {
        StartedAt = startedAt;
    }

    public long StartedAt { get; }
    public long Requests;
    public readonly Dictionary<string, long> StatusCounts = new();
    public long UnhandledExceptions;
    public readonly Histogram Histogram = new();
    public readonly Dictionary<string, RouteStats> Routes = new();
    public readonly JobStats Jobs = new();
    public readonly Dictionary<string, JobStats> JobClasses = new();
    public readonly Dictionary<string, ExceptionEntry> Exceptions = new();
    public long ExceptionsTruncated;
    public readonly Dictionary<string, CheckpointCounts> Checkpoints = new();

    public void Record(string routeKey, int status, double durationMs, bool unhandled, int maxRoutes, bool inTotals)
    {
        var statusKey = status.ToString(CultureInfo.InvariantCulture);
        if (inTotals)
        {
            Requests++;
            if (status >= 400) Increment(StatusCounts, statusKey);
            if (unhandled) UnhandledExceptions++;
            Histogram.Record(durationMs);
        }

        var key = Aggregator.BoundedKey(Routes, routeKey, maxRoutes);
        if (!Routes.TryGetValue(key, out var route)) Routes[key] = route = new RouteStats();
        route.Requests++;
        if (status >= 400) Increment(route.StatusCounts, statusKey);
        route.Histogram.Record(durationMs);
    }

    public JobStats JobStatsFor(string jobClass, int maxClasses)
    {
        var key = Aggregator.BoundedKey(JobClasses, jobClass, maxClasses);
        if (!JobClasses.TryGetValue(key, out var stats)) JobClasses[key] = stats = new JobStats();
        return stats;
    }

    /// <summary>
    /// A class with jobs running and none finished is listed too, so a job
    /// that hangs is reported.
    /// </summary>
    public void RecordRunning(string jobClass, int count, long oldestMs, int maxClasses)
    {
        Jobs.AddRunning(count, oldestMs);
        JobStatsFor(jobClass, maxClasses).AddRunning(count, oldestMs);
    }

    public void RecordCheckpoint(string name, long count, string? unitOfWork)
    {
        var key = Aggregator.BoundedKey(Checkpoints, name, Aggregator.MaxCheckpoints);
        if (!Checkpoints.TryGetValue(key, out var counts)) Checkpoints[key] = counts = new CheckpointCounts();
        counts.Count += count;
        if (unitOfWork == UnitOfWork.Http) counts.Http += count;
        else if (unitOfWork == UnitOfWork.Job) counts.Job += count;
    }

    /// <summary>Up to 20 fingerprints per period; the rest are only counted.</summary>
    public void RecordException(ExceptionDetails details, string? source, bool handled, IReadOnlyList<string>? backtrace)
    {
        if (!Exceptions.TryGetValue(details.Fingerprint, out var entry))
        {
            if (Exceptions.Count >= Aggregator.MaxExceptions)
            {
                ExceptionsTruncated++;
                return;
            }
            Exceptions[details.Fingerprint] = entry = new ExceptionEntry(details);
        }
        if (handled) entry.HandledCount++;
        else entry.Count++;
        if (!string.IsNullOrEmpty(source)) Increment(entry.Sources, source);
        if (backtrace is { Count: > 0 } && entry.Backtrace is null) entry.Backtrace = backtrace;
    }

    private static void Increment(Dictionary<string, long> counts, string key) =>
        counts[key] = counts.TryGetValue(key, out var count) ? count + 1 : 1;
}

/// <summary>
/// Accumulates request, job, exception, and checkpoint behavior into
/// one-minute periods inside the process. Recording is a few dictionary
/// updates under a lock; nothing here touches the network.
/// </summary>
internal sealed class Aggregator
{
    public const string Other = "__other__";
    public const int PeriodSeconds = 60;
    public const int MaxExceptions = 20;
    public const int MaxBacktraces = 5;
    public const int MaxCheckpoints = 100;

    private readonly Func<double> _clock;
    private readonly Dictionary<long, Period> _periods = new();
    private readonly HashSet<string> _seenFingerprints = new();
    private readonly object _lock = new();
    private long _lastDrainedAt;

    public Aggregator(int maxRoutes, Func<double> clock)
    {
        MaxRoutes = maxRoutes;
        _clock = clock;
        _lastDrainedAt = PeriodStart(clock()) - PeriodSeconds;
    }

    public int MaxRoutes { get; }

    /// <summary>
    /// The key itself while the table has room, else Other: up to limit - 1
    /// distinct keys per period, the rest counted together.
    /// </summary>
    public static string BoundedKey<T>(Dictionary<string, T> table, string key, int limit) =>
        table.ContainsKey(key) || table.Count < limit - 1 ? key : Other;

    /// <summary>
    /// inTotals false records the request under its route only, leaving it
    /// out of the app-wide counts and latency.
    /// </summary>
    public void Record(string routeKey, int status, double durationMs, bool unhandled = false, bool inTotals = true)
    {
        lock (_lock) CurrentPeriod().Record(routeKey, status, durationMs, unhandled, MaxRoutes, inTotals);
    }

    /// <summary>
    /// failed: the attempt threw (or was retried); discarded: the job will not
    /// run again.
    /// </summary>
    public void RecordJob(string jobClass, double durationMs, bool failed = false, bool discarded = false,
        double? queueLatencyMs = null)
    {
        lock (_lock)
        {
            var period = CurrentPeriod();
            period.Jobs.Record(failed, discarded, durationMs, queueLatencyMs);
            period.JobStatsFor(jobClass, MaxRoutes).Record(failed, discarded, durationMs, queueLatencyMs);
        }
    }

    /// <summary>A job that will not run again, recorded without another attempt.</summary>
    public void RecordDiscard(string jobClass)
    {
        lock (_lock)
        {
            var period = CurrentPeriod();
            period.Jobs.RecordDiscard();
            period.JobStatsFor(jobClass, MaxRoutes).RecordDiscard();
        }
    }

    /// <summary>
    /// unitOfWork: UnitOfWork.Http or UnitOfWork.Job when the checkpoint was
    /// recorded while handling a request or running a job, else null.
    /// </summary>
    public void RecordCheckpoint(string name, long count = 1, string? unitOfWork = null)
    {
        lock (_lock) CurrentPeriod().RecordCheckpoint(name, count, unitOfWork);
    }

    /// <summary>
    /// A representative backtrace is kept only the first time this process
    /// sees a fingerprint, and at most 5 per period.
    /// </summary>
    public void RecordException(ExceptionDetails details, string? source = null, bool handled = false,
        IReadOnlyList<string>? backtrace = null)
    {
        lock (_lock)
        {
            var period = CurrentPeriod();
            var newHere = _seenFingerprints.Add(details.Fingerprint);
            var keepTrace = newHere && period.Exceptions.Values.Count(e => e.Backtrace is not null) < MaxBacktraces;
            period.RecordException(details, source, handled, keepTrace ? backtrace : null);
        }
    }

    /// <summary>
    /// Completed periods, oldest first. Minutes with nothing recorded are
    /// returned as empty periods, so idle processes still report their
    /// release. includeCurrent also closes the in-progress minute (used at
    /// shutdown).
    /// </summary>
    public List<Period> Drain(bool includeCurrent = false, int maxPeriods = 10)
    {
        var current = PeriodStart(_clock());
        var last = includeCurrent ? current : current - PeriodSeconds;
        lock (_lock)
        {
            var first = Math.Max(_lastDrainedAt + PeriodSeconds, last - (maxPeriods - 1) * (long)PeriodSeconds);
            var drained = new List<Period>();
            for (var startedAt = first; startedAt <= last; startedAt += PeriodSeconds)
            {
                drained.Add(_periods.Remove(startedAt, out var period) ? period : new Period(startedAt));
            }
            foreach (var startedAt in _periods.Keys.Where(s => s <= last).ToList()) _periods.Remove(startedAt);
            if (drained.Count > 0) _lastDrainedAt = last;
            return drained;
        }
    }

    public static long PeriodStart(double now)
    {
        var seconds = (long)Math.Floor(now);
        return seconds - seconds % PeriodSeconds;
    }

    private Period CurrentPeriod()
    {
        var startedAt = PeriodStart(_clock());
        if (!_periods.TryGetValue(startedAt, out var period)) _periods[startedAt] = period = new Period(startedAt);
        return period;
    }
}
