using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace DeployAngel.Core;

/// <summary>
/// The per-process runtime: records requests and jobs into the aggregator
/// and sends completed minutes from a background thread. Every public method
/// fails open; nothing here may throw into, or block, the customer's app.
/// </summary>
internal sealed class Agent
{
    public const string TelemetryPath = "/api/v1/telemetry";
    public const string MetadataPath = "/api/v1/application_metadata";
    public static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);
    private const int DefaultPause = 60;
    // Seconds past each minute a process waits before sending, chosen once
    // per process so the cloud gets a steady stream instead of a burst at
    // the top of the minute. The cloud reads a minute 2 minutes after it
    // ends, so a send that times out and goes again a minute later still
    // lands in time.
    private const double MinJitter = 1.0;
    private const double MaxJitter = 50.0;

    // Exceptions already recorded, whichever instrumentation sees them first.
    // Weak, so a recorded exception is still collected; and kept off the
    // exception itself, whose Data loggers print.
    private static readonly ConditionalWeakTable<Exception, object> Seen = new();

    private readonly ITransport _transport;
    private readonly Func<double> _clock;
    private readonly HashSet<string> _warned = new();
    private readonly object _threadLock = new();
    private readonly object _releaseLock = new();
    private readonly ManualResetEventSlim _stopping = new(false);
    private readonly double _jitter;
    private readonly Aggregator _aggregator;
    private readonly RunningJobs _runningJobs;
    private readonly PayloadBuffer _buffer;
    private Thread? _thread;
    private double? _pausedUntil;
    private bool _metadataSent;

    public Agent(DeployAngelOptions options, string environment, string? root, RuntimeInfo runtime,
        IReadOnlyDictionary<string, string?> env, SourceMap sourceMap, ITransport? transport = null,
        Func<double>? clock = null, bool eager = false, CapabilitySet? capabilities = null, Metadata? metadata = null,
        string? informationalVersion = null, Func<string, string?>? http = null)
    {
        Options = options;
        Environment = environment;
        Root = root;
        Runtime = runtime;
        SourceMap = sourceMap;
        Metadata = metadata;
        Capabilities = capabilities ?? new CapabilitySet();
        _transport = transport ?? new Transport(options);
        _clock = clock ?? Now;
        Active = options.IsActive(environment);
        Release = Active
            ? ReleaseResolver.Resolve(options, env, root, informationalVersion, http)
            : new Release(null, null, "unknown");
        // A REVISION file or setting that names no commit leaves the release
        // as unknown as nothing at all, so the code fingerprint may name it too.
        if (Release.Unknown) Release.Pending = true;
        Instance = new Instance(env, _clock());
        _aggregator = new Aggregator(options.MaxRoutes, _clock);
        _runningJobs = new RunningJobs(_clock);
        _buffer = new PayloadBuffer(options.MaxQueuedPayloads);
        _jitter = MinJitter + Random.Shared.NextDouble() * (MaxJitter - MinJitter);
        if (Active && !Release.Pending) WarnAboutRelease();
        if (Active && eager) StartReporter();
    }

    public DeployAngelOptions Options { get; }
    public string Environment { get; }
    public string? Root { get; }
    public RuntimeInfo Runtime { get; }
    public SourceMap SourceMap { get; }
    public Metadata? Metadata { get; }
    public CapabilitySet Capabilities { get; }
    public bool Active { get; }
    public Release Release { get; private set; }
    public Instance Instance { get; }
    internal int Buffered => _buffer.Count;

    public static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    public void RecordRequest(string routeKey, int status, double durationMs, bool unhandled = false, bool inTotals = true)
    {
        if (!Active) return;
        try
        {
            StartReporter();
            _aggregator.Record(routeKey, status, durationMs, unhandled, inTotals);
        }
        catch (Exception error)
        {
            WarnOnce("record", $"DeployAngel failed to record a request: {Describe(error)}");
        }
    }

    public void RecordJob(string jobClass, double durationMs, bool failed = false, bool discarded = false,
        double? queueLatencyMs = null)
    {
        if (!Active) return;
        try
        {
            StartReporter();
            _aggregator.RecordJob(jobClass, durationMs, failed, discarded, queueLatencyMs);
        }
        catch (Exception error)
        {
            WarnOnce("record_job", $"DeployAngel failed to record a job: {Describe(error)}");
        }
    }

    /// <summary>
    /// Marks a job as running until JobFinished, so one that hangs is still
    /// reported. Returns the token to pass to it.
    /// </summary>
    public object? JobStarted(string jobClass)
    {
        if (!Active) return null;
        try
        {
            StartReporter();
            return _runningJobs.Start(jobClass);
        }
        catch (Exception error)
        {
            WarnOnce("job_started", $"DeployAngel failed to record a running job: {Describe(error)}");
            return null;
        }
    }

    public void JobFinished(object? token)
    {
        if (token is null) return;
        try
        {
            _runningJobs.Finish(token);
        }
        catch (Exception error)
        {
            WarnOnce("job_finished", $"DeployAngel failed to record a finished job: {Describe(error)}");
        }
    }

    public void RecordDiscard(string jobClass)
    {
        if (!Active) return;
        try
        {
            _aggregator.RecordDiscard(jobClass);
        }
        catch (Exception error)
        {
            WarnOnce("record_discard", $"DeployAngel failed to record a discarded job: {Describe(error)}");
        }
    }

    public void RecordCheckpoint(string name, long count = 1)
    {
        if (!Active) return;
        try
        {
            StartReporter();
            _aggregator.RecordCheckpoint(name, count, UnitOfWork.Current);
        }
        catch (Exception error)
        {
            WarnOnce("record_checkpoint", $"DeployAngel failed to record a checkpoint: {Describe(error)}");
        }
    }

    /// <summary>Records each exception object once, whichever instrumentation sees it first.</summary>
    public void RecordException(Exception? exception, string? source = null, bool handled = false)
    {
        if (!Active || exception is null) return;
        try
        {
            if (Seen.TryGetValue(exception, out _)) return;
            Seen.AddOrUpdate(exception, true);
            var messages = Options.ExceptionMessages;
            var details = Fingerprint.ForException(exception, SourceMap, messages,
                messages ? Redaction.Current() : null);
            RecordExceptionDetails(details, Fingerprint.Backtrace(exception, SourceMap), source, handled);
        }
        catch (Exception error)
        {
            WarnOnce("record_exception", $"DeployAngel failed to record an exception: {Describe(error)}");
        }
    }

    public void RecordExceptionDetails(ExceptionDetails details, IReadOnlyList<string>? backtrace, string? source = null,
        bool handled = false)
    {
        if (!Active) return;
        try
        {
            StartReporter();
            _aggregator.RecordException(details, source, handled, backtrace);
        }
        catch (Exception error)
        {
            WarnOnce("record_exception", $"DeployAngel failed to record an exception: {Describe(error)}");
        }
    }

    /// <summary>
    /// Drains completed minutes into the buffer, encoded so unsent minutes
    /// stay small while DeployAngel is unreachable, and sends what it can.
    /// </summary>
    public int Flush(bool includeCurrent = false, long? deadline = null)
    {
        if (!Active) return 0;
        try
        {
            var periods = _aggregator.Drain(includeCurrent, Options.MaxQueuedPayloads);
            if (periods.Count > 0) AddRunning(periods[^1]);
            var release = periods.Count > 0 ? ResolvedRelease() : null;
            foreach (var period in periods)
                _buffer.Push(Transport.Encode(Protocol.Telemetry(period, Instance, release!, Runtime, Capabilities.ToList())));
            return SendBuffered(deadline);
        }
        catch (Exception error)
        {
            WarnOnce("flush", $"DeployAngel failed to flush telemetry: {Describe(error)}");
            return 0;
        }
    }

    public void StartReporter()
    {
        if (!Active || _thread is { IsAlive: true }) return;
        lock (_threadLock)
        {
            if (_thread is { IsAlive: true }) return;
            _stopping.Reset();
            var thread = new Thread(RunReporter) { Name = "deployangel-reporter", IsBackground = true };
            thread.Start();
            _thread = thread;
        }
    }

    /// <summary>
    /// Sends the in-progress minute too, bounded by a short timeout, because
    /// deployments restart processes. Runs on the calling thread.
    /// </summary>
    public void Shutdown(TimeSpan? timeout = null)
    {
        if (!Active) return;
        try
        {
            _stopping.Set();
            Flush(includeCurrent: true, deadline: Stopwatch.GetTimestamp() + Ticks(timeout ?? ShutdownTimeout));
        }
        catch (Exception)
        {
            // Shutting down must never fail the app's own shutdown.
        }
    }

    /// <summary>
    /// Sent once per process; retried on the next minute if it fails. File
    /// digests are only uploaded when the cloud has not seen the manifest.
    /// </summary>
    public void SendMetadata()
    {
        if (_metadataSent || Metadata is null || Paused()) return;
        try
        {
            var result = _transport.Post(MetadataPath, Transport.Encode(MetadataPayload(withFiles: false)));
            if (!result.Ok) return;
            if (result.Body is { ValueKind: System.Text.Json.JsonValueKind.Object } body &&
                body.TryGetProperty("manifest_needed", out var needed) && needed.ValueKind == System.Text.Json.JsonValueKind.True)
            {
                if (!_transport.Post(MetadataPath, Transport.Encode(MetadataPayload(withFiles: true))).Ok) return;
            }
            _metadataSent = true;
            // Routes, jobs, and digests are all read; the symbols can go.
            SourceMap.Close();
        }
        catch (Exception error)
        {
            WarnOnce("metadata", $"DeployAngel failed to send application metadata: {Describe(error)}");
        }
    }

    private byte[] MetadataPayload(bool withFiles)
    {
        var json = new JsonOut();
        json.StartObject();
        json.Property("protocol_version", Protocol.Version);
        json.Name("instance");
        Instance.Write(json);
        json.Name("release");
        ResolvedRelease().Write(json);
        json.Name("runtime");
        Runtime.Write(json);
        Metadata!.Write(json);
        if (withFiles) Metadata.WriteFiles(json);
        json.EndObject();
        return json.ToBytes();
    }

    /// <summary>
    /// The release for a payload. When nothing named it at boot, the code
    /// fingerprint is computed here, once: from the reporter thread before
    /// its first send, or the final flush at shutdown, never in a request or
    /// job. It's the hash of the file digests the metadata sends, so the
    /// manifest is built once for both.
    /// </summary>
    internal Release ResolvedRelease()
    {
        if (!Release.Pending) return Release;
        lock (_releaseLock)
        {
            if (Release.Pending)
            {
                FileManifest? manifest = null;
                try
                {
                    manifest = Metadata?.FileManifest();
                }
                catch (Exception)
                {
                    manifest = null;
                }
                Release = ReleaseResolver.CodeFingerprint(manifest);
                WarnAboutRelease();
            }
        }
        return Release;
    }

    /// <summary>
    /// What's still running, on the latest period only: as of its end, or now
    /// for the in-progress one at shutdown.
    /// </summary>
    private void AddRunning(Period period)
    {
        try
        {
            var endedAt = Math.Min(period.StartedAt + Aggregator.PeriodSeconds, _clock());
            foreach (var (jobClass, (count, oldestMs)) in _runningJobs.Snapshot(endedAt))
                period.RecordRunning(jobClass, count, oldestMs, Options.MaxRoutes);
        }
        catch (Exception error)
        {
            WarnOnce("running_jobs", $"DeployAngel failed to read running jobs: {Describe(error)}");
        }
    }

    private void WarnAboutRelease()
    {
        if (Release.Unknown)
            WarnOnce("unknown_release", "DeployAngel could not determine the release; set DEPLOYANGEL_REVISION " +
                                        "or enable Heroku dyno metadata. Telemetry will not be attributed to deployments.");
        else if (Release.Source == "code_fingerprint")
            WarnOnce("code_fingerprint", "DeployAngel identifies releases by a fingerprint of the app's code. Set " +
                                         "DEPLOYANGEL_REVISION to the deployed commit to see each release's commits and pull requests.");
    }

    private void RunReporter()
    {
        try
        {
            // Off the request path, so a process that exits within a minute
            // or two doesn't build the fingerprint at shutdown instead.
            if (Metadata is not null) ResolvedRelease();
            while (!_stopping.IsSet)
            {
                if (_stopping.Wait(SecondsUntilNextFlush())) break;
                SendMetadata();
                Flush();
            }
        }
        catch (Exception error)
        {
            WarnOnce("reporter", $"DeployAngel reporter stopped: {Describe(error)}");
        }
    }

    private TimeSpan SecondsUntilNextFlush()
    {
        var interval = Math.Max(Options.FlushInterval.TotalSeconds, 1);
        return TimeSpan.FromSeconds(interval - _clock() % interval + _jitter);
    }

    private int SendBuffered(long? deadline)
    {
        var sent = 0;
        while (true)
        {
            var payload = _buffer.Shift();
            if (payload is null) break;
            TimeSpan? remaining = null;
            if (deadline is not null)
            {
                remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), deadline.Value);
                if (remaining <= TimeSpan.Zero)
                {
                    _buffer.Unshift(payload);
                    break;
                }
            }
            if (Paused())
            {
                _buffer.Unshift(payload);
                break;
            }
            var timeout = remaining is { } r && r < Options.Timeout ? r : (TimeSpan?)null;
            var result = _transport.Post(TelemetryPath, payload, timeout);
            if (result.Outcome == TransportOutcome.Ok)
            {
                sent++;
            }
            else if (result.Outcome == TransportOutcome.Retry)
            {
                _buffer.Unshift(payload);
                break;
            }
            else
            {
                HandleRejection(result);
            }
        }
        return sent;
    }

    private void HandleRejection(TransportResult result)
    {
        if (result.Status == 429)
            _pausedUntil = _clock() + (result.RetryAfter is > 0 ? result.RetryAfter.Value : DefaultPause);
        else if (result.Status is 401 or 403)
            WarnOnce("auth", $"DeployAngel rejected the token (HTTP {result.Status}); check DEPLOYANGEL_TOKEN " +
                             "and that it has the telemetry scope.");
        else
            WarnOnce($"rejected_{result.Status}", $"DeployAngel rejected a telemetry payload (HTTP {result.Status}).");
    }

    private bool Paused() => _pausedUntil is not null && _clock() < _pausedUntil;

    private void WarnOnce(string key, string message)
    {
        lock (_warned)
        {
            if (!_warned.Add(key)) return;
        }
        Options.Log(LogLevel.Warning, message);
    }

    private static long Ticks(TimeSpan span) => (long)(span.TotalSeconds * Stopwatch.Frequency);

    private static string Describe(Exception error) => $"{error.GetType().Name}: {error.Message}";
}

/// <summary>
/// What this process can observe, announced in every payload so the cloud
/// never claims to verify what the agent cannot see.
/// </summary>
internal sealed class CapabilitySet
{
    private readonly List<string> _names = new() { "http", "exceptions" };

    public CapabilitySet(params string[] names)
    {
        foreach (var name in names) Add(name);
    }

    public void Add(string name)
    {
        lock (_names)
        {
            if (!_names.Contains(name)) _names.Add(name);
        }
    }

    public List<string> ToList()
    {
        lock (_names) return _names.ToList();
    }
}
