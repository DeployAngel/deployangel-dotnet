using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;

namespace DeployAngel.Core;

/// <summary>One OS process, with a random suffix so a restarted process never reuses an identity.</summary>
internal sealed class Instance
{
    public Instance(IReadOnlyDictionary<string, string?> env, double now)
    {
        env.TryGetValue("DYNO", out var dyno);
        Host = string.IsNullOrEmpty(dyno) ? SafeHostName() : dyno;
        Pid = Environment.ProcessId;
        ProcessType = string.IsNullOrEmpty(dyno) ? null : dyno.Split('.')[0];
        Id = $"{Host}:{Pid}:{Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant()}";
        StartedAt = now;
    }

    public string Id { get; }
    public string Host { get; }
    public int Pid { get; }
    public string? ProcessType { get; }
    public double StartedAt { get; }

    public void Write(JsonOut json)
    {
        json.StartObject();
        json.Property("id", Id);
        json.Property("host", Host);
        json.Property("pid", Pid);
        if (ProcessType is not null) json.Property("process_type", ProcessType);
        json.Property("started_at", Iso8601(StartedAt));
        json.EndObject();
    }

    public static string Iso8601(double epoch) =>
        DateTimeOffset.FromUnixTimeSeconds((long)Math.Floor(epoch)).UtcDateTime
            .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string SafeHostName()
    {
        try
        {
            return Environment.MachineName;
        }
        catch (Exception)
        {
            return "unknown";
        }
    }
}
