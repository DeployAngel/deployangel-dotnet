using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Extensions.Configuration;

namespace DeployAngel.AspNetCore;

/// <summary>
/// The "DeployAngel" configuration section (appsettings.json, or variables
/// like DeployAngel__Token), read key by key rather than with the reflection
/// binder, so it works in trimmed apps.
/// </summary>
internal static class ConfigurationOptions
{
    public static void Apply(DeployAngelOptions options, IConfigurationSection section)
    {
        if (!section.Exists()) return;
        options.Token = Text(section, "Token") ?? options.Token;
        options.Endpoint = Text(section, "Endpoint") ?? options.Endpoint;
        options.Enabled = DeployAngelOptions.ParseBoolean(section["Enabled"]) ?? options.Enabled;
        options.Environment = Text(section, "Environment") ?? options.Environment;
        options.ReleaseVersion = Text(section, "ReleaseVersion") ?? options.ReleaseVersion;
        options.Revision = Text(section, "Revision") ?? options.Revision;
        options.Root = Text(section, "Root") ?? options.Root;
        options.SourceRoot = Text(section, "SourceRoot") ?? options.SourceRoot;
        options.FileDigests = DeployAngelOptions.ParseBoolean(section["FileDigests"]) ?? options.FileDigests;
        options.ExceptionMessages = DeployAngelOptions.ParseBoolean(section["ExceptionMessages"]) ?? options.ExceptionMessages;
        if (Seconds(section, "FlushInterval") is { } flush) options.FlushInterval = flush;
        if (Seconds(section, "Timeout") is { } timeout) options.Timeout = timeout;
        if (List(section, "Environments") is { Count: > 0 } environments) options.Environments = environments;
        if (List(section, "IgnoredRoutes") is { Count: > 0 } ignored) options.IgnoredRoutes = ignored;
        var recurring = section.GetSection("RecurringJobs").GetChildren().Where(c => !string.IsNullOrWhiteSpace(c.Value)).ToList();
        if (recurring.Count > 0) options.RecurringJobs = recurring.ToDictionary(c => c.Key, c => c.Value!);
        var flows = section.GetSection("CriticalFlows").GetChildren().ToList();
        if (flows.Count > 0)
            options.CriticalFlows = flows.ToDictionary(f => f.Key,
                f => f.Value is { } single ? new[] { single } : f.GetChildren().Select(c => c.Value).OfType<string>().ToArray());
    }

    private static string? Text(IConfigurationSection section, string key) =>
        string.IsNullOrWhiteSpace(section[key]) ? null : section[key];

    private static TimeSpan? Seconds(IConfigurationSection section, string key)
    {
        var value = section[key];
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
            return TimeSpan.FromSeconds(seconds);
        return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var span) && span > TimeSpan.Zero ? span : null;
    }

    private static List<string>? List(IConfigurationSection section, string key)
    {
        var child = section.GetSection(key);
        if (!string.IsNullOrWhiteSpace(child.Value))
            return child.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var items = child.GetChildren().Select(c => c.Value).OfType<string>().ToList();
        return items.Count > 0 ? items : null;
    }
}
