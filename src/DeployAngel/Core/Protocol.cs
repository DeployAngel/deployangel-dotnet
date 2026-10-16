using System.Collections.Generic;
using System.Linq;

namespace DeployAngel.Core;

internal sealed record RuntimeInfo(string Language, string LanguageVersion, string? Framework, string? FrameworkVersion)
{
    public void Write(JsonOut json)
    {
        json.StartObject();
        json.Property("language", Language);
        json.Property("language_version", LanguageVersion);
        if (Framework is not null) json.Property("framework", Framework);
        if (FrameworkVersion is not null) json.Property("framework_version", FrameworkVersion);
        json.EndObject();
    }
}

/// <summary>
/// Builds Agent Protocol v1 telemetry payloads. Only mergeable values are
/// sent: counts and histograms, never percentiles or rates.
/// </summary>
internal static class Protocol
{
    public const int Version = 1;

    public static byte[] Telemetry(Period period, Instance instance, Release release, RuntimeInfo runtime,
        IReadOnlyCollection<string> capabilities)
    {
        var json = new JsonOut();
        json.StartObject();
        json.Property("protocol_version", Version);
        json.Name("agent");
        json.StartObject();
        json.Property("name", AgentInfo.Name);
        json.Property("version", AgentInfo.Version);
        json.EndObject();
        json.Name("runtime");
        runtime.Write(json);
        json.Name("instance");
        instance.Write(json);
        json.Name("release");
        release.Write(json);
        json.Property("capabilities", capabilities.ToList());
        json.Name("period");
        json.StartObject();
        json.Property("started_at", Instance.Iso8601(period.StartedAt));
        json.Property("duration_seconds", Aggregator.PeriodSeconds);
        json.EndObject();

        json.Name("http");
        json.StartObject();
        json.Property("requests", period.Requests);
        WriteCounts(json, "status_counts", period.StatusCounts);
        json.Property("unhandled_exceptions", period.UnhandledExceptions);
        json.Name("latency_histogram");
        period.Histogram.Write(json);
        json.EndObject();

        json.Name("routes");
        json.StartArray();
        foreach (var (key, route) in period.Routes)
        {
            json.StartObject();
            json.Property("key", key);
            json.Property("requests", route.Requests);
            WriteCounts(json, "status_counts", route.StatusCounts);
            json.Name("latency_histogram");
            route.Histogram.Write(json);
            json.EndObject();
        }
        json.EndArray();

        json.Name("exceptions");
        json.StartArray();
        foreach (var entry in period.Exceptions.Values)
        {
            var details = entry.Details;
            json.StartObject();
            json.Property("fingerprint", details.Fingerprint);
            json.Property("fingerprint_version", details.FingerprintVersion);
            json.Property("exception_class", details.ExceptionClass);
            if (details.Message is not null) json.Property("message", details.Message);
            json.Property("top_frame", details.TopFrame);
            json.Property("app_frame", details.AppFrame);
            json.Property("count", entry.Count);
            json.Property("handled_count", entry.HandledCount);
            WriteCounts(json, "sources", entry.Sources);
            if (entry.Backtrace is not null) json.Property("backtrace", entry.Backtrace);
            json.EndObject();
        }
        json.EndArray();
        json.Property("exceptions_truncated", period.ExceptionsTruncated);

        json.Name("jobs");
        WriteJobStats(json, period.Jobs, null);
        json.Name("job_classes");
        json.StartArray();
        foreach (var (key, stats) in period.JobClasses) WriteJobStats(json, stats, key);
        json.EndArray();

        // http and job: how many of count were recorded while handling an
        // HTTP request or running a job; the rest were neither.
        json.Name("checkpoints");
        json.StartArray();
        foreach (var (key, counts) in period.Checkpoints)
        {
            json.StartObject();
            json.Property("key", key);
            json.Property("count", counts.Count);
            json.Property("http", counts.Http);
            json.Property("job", counts.Job);
            json.EndObject();
        }
        json.EndArray();
        json.EndObject();
        return json.ToBytes();
    }

    /// <summary>running and oldest_running_ms only when jobs were running.</summary>
    private static void WriteJobStats(JsonOut json, JobStats stats, string? key)
    {
        json.StartObject();
        if (key is not null) json.Property("key", key);
        json.Property("processed", stats.Processed);
        json.Property("failed", stats.Failed);
        json.Property("discarded", stats.Discarded);
        json.Name("duration_histogram");
        stats.Duration.Write(json);
        json.Name("queue_latency_histogram");
        stats.QueueLatency.Write(json);
        if (stats.Running > 0)
        {
            json.Property("running", stats.Running);
            if (stats.OldestRunningMs is not null) json.Property("oldest_running_ms", stats.OldestRunningMs.Value);
        }
        json.EndObject();
    }

    private static void WriteCounts(JsonOut json, string name, Dictionary<string, long> counts)
    {
        json.Name(name);
        json.StartObject();
        foreach (var (key, count) in counts) json.Property(key, count);
        json.EndObject();
    }
}
