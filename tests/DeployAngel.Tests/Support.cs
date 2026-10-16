using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DeployAngel.Core;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace DeployAngel.Tests;

internal sealed class FakeClock
{
    // 2026-10-16 12:00:10 UTC: ten seconds into a minute.
    public double Now { get; set; } = 1_792_152_010;

    public void Advance(double seconds) => Now += seconds;

    public Func<double> Func => () => Now;
}

internal sealed class FakeTransport : ITransport
{
    public List<(string Path, JsonElement Body)> Posts { get; } = new();
    public Queue<TransportResult> Results { get; } = new();
    public Func<string, TransportResult>? Respond { get; set; }

    public TransportResult Post(string path, byte[] body, TimeSpan? timeout = null)
    {
        using var input = new GZipStream(new MemoryStream(body), CompressionMode.Decompress);
        using var document = JsonDocument.Parse(input);
        lock (Posts) Posts.Add((path, document.RootElement.Clone()));
        if (Results.Count > 0) return Results.Dequeue();
        return Respond?.Invoke(path) ?? new TransportResult(TransportOutcome.Ok, 202);
    }

    public List<JsonElement> Telemetry()
    {
        lock (Posts) return Posts.Where(p => p.Path == Agent.TelemetryPath).Select(p => p.Body).ToList();
    }

    public List<JsonElement> Metadata()
    {
        lock (Posts) return Posts.Where(p => p.Path == Agent.MetadataPath).Select(p => p.Body).ToList();
    }
}

internal static class Paths
{
    /// <summary>The repository root, as this build saw it.</summary>
    public static string RepoRoot([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
}

internal static class Maps
{
    public static readonly Assembly App = typeof(Maps).Assembly;
    public static readonly Assembly Framework = typeof(JsonSerializer).Assembly;

    /// <summary>This test assembly plays the app.</summary>
    public static SourceMap ForTests(string? root = null) =>
        new(new[] { App.GetName().Name! }, root ?? Paths.RepoRoot());
}

internal static class Env
{
    public static IReadOnlyDictionary<string, string?> Of(params (string Key, string? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    public static readonly IReadOnlyDictionary<string, string?> Empty = new Dictionary<string, string?>();
}

internal static class Json
{
    public static IEnumerable<JsonElement> Array(this JsonElement element, string name) =>
        element.GetProperty(name).EnumerateArray();

    public static string Str(this JsonElement element, string name) => element.GetProperty(name).GetString()!;
    public static long Num(this JsonElement element, string name) => element.GetProperty(name).GetInt64();

    public static Dictionary<string, long> Counts(this JsonElement element, string name) =>
        element.GetProperty(name).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt64());
}
