using System;
using System.Collections.Generic;
using System.Linq;
using DeployAngel.Integration;

namespace DeployAngel.Core;

/// <summary>
/// What the application contains, sent once per process: the route table,
/// job classes, declared recurring schedules, critical flows, and file
/// digests. Paths and hashes only; source code never leaves the application.
///
/// Routes, jobs, and schedules come from the integrations in use (sources),
/// each reporting what it knows. Each source is read on its own, so one that
/// fails leaves the others' answers in place.
/// </summary>
internal sealed class Metadata
{
    private readonly DeployAngelOptions _options;
    private readonly IReadOnlyList<IMetadataSource> _sources;

    public Metadata(DeployAngelOptions options, SourceMap sourceMap, IReadOnlyList<IMetadataSource> sources)
    {
        _options = options;
        SourceMap = sourceMap;
        _sources = sources;
    }

    public SourceMap SourceMap { get; }

    public FileManifest FileManifest() => _options.FileDigests ? SourceMap.Manifest() : Core.FileManifest.Empty;

    public void Write(JsonOut json)
    {
        json.Name("routes");
        json.StartArray();
        foreach (var route in Routes())
        {
            json.StartObject();
            json.Property("key", route.Key);
            if (route.Controller is not null) json.Property("controller", route.Controller);
            if (route.Action is not null) json.Property("action", route.Action);
            if (route.Files is { Count: > 0 }) json.Property("files", route.Files);
            json.EndObject();
        }
        json.EndArray();
        json.Property("job_classes", Collect(s => s.JobClasses()).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList());
        json.Name("job_class_files");
        json.StartObject();
        foreach (var (name, files) in JobClassFiles()) json.Property(name, files);
        json.EndObject();
        json.Name("schedules");
        json.StartArray();
        foreach (var schedule in Collect(s => s.Schedules()))
        {
            json.StartObject();
            json.Property("key", schedule.Key);
            json.Property("class", schedule.Class);
            json.Property("schedule", schedule.Schedule);
            json.Property("source", schedule.Source);
            json.Property("time_zone", schedule.TimeZone);
            json.EndObject();
        }
        json.EndArray();
        json.Name("critical_flows");
        json.StartObject();
        foreach (var (name, items) in _options.CriticalFlows) json.Property(name, items ?? Array.Empty<string>());
        json.EndObject();
        var manifest = FileManifest();
        json.Name("file_manifest");
        json.StartObject();
        json.Property("hash", manifest.Hash);
        json.Property("count", manifest.Count);
        json.Property("truncated", manifest.Truncated);
        json.EndObject();
    }

    public void WriteFiles(JsonOut json)
    {
        json.Name("files");
        json.StartObject();
        foreach (var (path, digest) in FileManifest().Files) json.Property(path, digest);
        json.EndObject();
    }

    public List<RouteInfo> Routes()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return Collect(s => s.Routes())
            .Where(r => !string.IsNullOrEmpty(r.Key) && !_options.IsIgnoredRoute(r.Key) && seen.Add(r.Key))
            .ToList();
    }

    public Dictionary<string, List<string>> JobClassFiles()
    {
        var files = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var source in _sources)
        {
            try
            {
                foreach (var (name, paths) in source.JobClassFiles())
                {
                    if (!files.TryGetValue(name, out var list)) files[name] = list = new List<string>();
                    list.AddRange(paths.Where(p => !list.Contains(p)));
                }
            }
            catch (Exception)
            {
                // The other sources' answers still count.
            }
        }
        return files;
    }

    private List<T> Collect<T>(Func<IMetadataSource, IEnumerable<T>> read)
    {
        var items = new List<T>();
        foreach (var source in _sources)
        {
            try
            {
                items.AddRange(read(source));
            }
            catch (Exception)
            {
                // The other sources' answers still count.
            }
        }
        return items;
    }
}
