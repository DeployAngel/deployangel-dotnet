using System;
using System.Collections.Generic;
using System.Linq;

namespace DeployAngel.Core;

/// <summary>
/// Sparse log-scale latency histogram, scheme log1.1_ms_v1 (Agent Protocol
/// v1). The agent only counts; percentiles are computed in the cloud after
/// merging every process, because percentiles cannot be averaged.
/// </summary>
internal sealed class Histogram
{
    public const string Scheme = "log1.1_ms_v1";
    public const int MaxBucket = 400;
    private static readonly double LogGamma = Math.Log(1.1);

    public Dictionary<int, long> Counts { get; } = new();

    public static int BucketFor(double milliseconds)
    {
        if (double.IsNaN(milliseconds) || milliseconds < 1) return 0;
        var bucket = Math.Ceiling(Math.Log(milliseconds) / LogGamma);
        return (int)Math.Min(Math.Max(bucket, 0), MaxBucket);
    }

    public void Record(double milliseconds)
    {
        var bucket = BucketFor(milliseconds);
        Counts[bucket] = Counts.TryGetValue(bucket, out var count) ? count + 1 : 1;
    }

    public long Count() => Counts.Values.Sum();

    public void Write(JsonOut json)
    {
        json.StartObject();
        json.Property("scheme", Scheme);
        json.Name("counts");
        json.StartObject();
        foreach (var bucket in Counts.Keys.OrderBy(b => b))
            json.Property(bucket.ToString(System.Globalization.CultureInfo.InvariantCulture), Counts[bucket]);
        json.EndObject();
        json.EndObject();
    }
}
