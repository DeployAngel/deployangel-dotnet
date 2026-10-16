using System;
using System.Collections.Generic;

namespace DeployAngel.Integration;

/// <summary>A route the app serves, keyed the way requests are recorded ("GET /orders/{id}").</summary>
/// <param name="Files">The repository-relative files that implement it.</param>
public sealed record RouteInfo(string Key, string? Controller = null, string? Action = null,
    IReadOnlyList<string>? Files = null);

/// <summary>A recurring job a scheduler declares.</summary>
/// <param name="Source">Who declares it: "config", "hangfire".</param>
/// <param name="TimeZone">The IANA zone the schedule is read in, when known.</param>
public sealed record ScheduleInfo(string Key, string Class, string Schedule, string Source, string? TimeZone = null);

/// <summary>
/// An integration that knows part of the app. A process may know only part
/// of it (a web process that doesn't run the Hangfire server), so the cloud
/// merges what every process of a release reports. Called from the
/// reporter thread, once per process.
/// </summary>
public interface IMetadataSource
{
    IEnumerable<RouteInfo> Routes() => Array.Empty<RouteInfo>();
    IEnumerable<string> JobClasses() => Array.Empty<string>();
    IReadOnlyDictionary<string, IReadOnlyList<string>> JobClassFiles() => new Dictionary<string, IReadOnlyList<string>>();
    IEnumerable<ScheduleInfo> Schedules() => Array.Empty<ScheduleInfo>();
}
