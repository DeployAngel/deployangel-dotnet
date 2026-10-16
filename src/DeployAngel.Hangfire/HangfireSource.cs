using System;
using System.Collections.Generic;
using System.Linq;
using DeployAngel.Integration;
using Hangfire;
using Hangfire.Storage;

namespace DeployAngel.Hangfire;

/// <summary>
/// The recurring jobs Hangfire's storage holds, with their cron schedules
/// and time zones, so DeployAngel expects each one and notices when one
/// stops running after a deploy.
/// </summary>
internal sealed class HangfireSource : IMetadataSource
{
    private readonly Func<JobStorage> _storage;

    public HangfireSource(Func<JobStorage>? storage = null)
    {
        _storage = storage ?? (() => JobStorage.Current);
    }

    public IEnumerable<ScheduleInfo> Schedules() =>
        RecurringJobs()
            .Where(job => job.Job is not null && !string.IsNullOrWhiteSpace(job.Cron))
            // Hangfire reads a schedule without a zone in UTC.
            .Select(job => new ScheduleInfo(job.Id, DeployAngelJobFilter.JobClass(job.Job), job.Cron, "hangfire",
                Instrumentation.TimeZoneName(job.TimeZoneId) ?? "UTC"))
            .ToList();

    public IEnumerable<string> JobClasses() =>
        RecurringJobs().Where(job => job.Job is not null).Select(job => DeployAngelJobFilter.JobClass(job.Job)).Distinct().ToList();

    public IReadOnlyDictionary<string, IReadOnlyList<string>> JobClassFiles()
    {
        var files = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var job in RecurringJobs())
        {
            if (job.Job is null || !Instrumentation.IsAppType(job.Job.Type)) continue;
            var file = Instrumentation.SourceFile(job.Job.Method);
            if (file is not null) files[DeployAngelJobFilter.JobClass(job.Job)] = new[] { file };
        }
        return files;
    }

    private List<RecurringJobDto> RecurringJobs()
    {
        try
        {
            using var connection = _storage().GetConnection();
            return connection.GetRecurringJobs();
        }
        catch (Exception)
        {
            return new List<RecurringJobDto>();
        }
    }
}
