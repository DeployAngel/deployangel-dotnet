using System;
using System.Collections.Generic;
using System.Linq;
using DeployAngel.Integration;

namespace DeployAngel.Core;

/// <summary>
/// Options.RecurringJobs, by the name the work runs under: a RunTask name or
/// a job class. A schedule without a zone is read in the server's.
/// </summary>
internal sealed class ConfiguredSchedules : IMetadataSource
{
    private readonly DeployAngelOptions _options;

    public ConfiguredSchedules(DeployAngelOptions options)
    {
        _options = options;
    }

    public IEnumerable<ScheduleInfo> Schedules()
    {
        var zone = TimeZones.Local();
        return _options.RecurringJobs
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => new ScheduleInfo(pair.Key, pair.Key, pair.Value, "config", zone))
            .ToList();
    }
}

internal static class TimeZones
{
    /// <summary>
    /// The zone cron reads schedules in on this server, as an IANA name.
    /// Null when it can't be told, and DeployAngel then doesn't assume one.
    /// </summary>
    public static string? Local()
    {
        try
        {
            return Iana(TimeZoneInfo.Local.Id);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>An IANA zone name for a zone id, which on Windows is a Windows id ("Eastern Standard Time").</summary>
    public static string? Iana(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (id == "UTC" || id.Contains('/')) return id;
        try
        {
            return TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana) ? iana : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
