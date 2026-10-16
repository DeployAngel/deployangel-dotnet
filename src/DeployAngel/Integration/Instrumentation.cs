using System;
using System.ComponentModel;
using System.Reflection;
using DeployAngel.Core;

namespace DeployAngel.Integration;

/// <summary>
/// What framework integrations (DeployAngel.Hangfire, or your own for
/// another job system) record through. The Agent Protocol speaks in HTTP,
/// routes, exceptions, jobs, and scheduled tasks; an integration translates
/// its framework into those. Every method fails open.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Advanced)]
public static class Instrumentation
{
    public static bool Recording => DeployAngelAgent.Recording;

    /// <summary>Signals this process can observe, such as "jobs".</summary>
    public static void AddCapability(string name)
    {
        try
        {
            AgentProcess.Capabilities.Add(name);
        }
        catch (Exception)
        {
            // Never fail the app.
        }
    }

    public static void AddMetadataSource(IMetadataSource source)
    {
        try
        {
            AgentProcess.AddMetadataSource(source);
        }
        catch (Exception)
        {
            // Never fail the app.
        }
    }

    /// <summary>
    /// failed: the attempt threw (or will be retried); discarded: the job will
    /// not run again. queueLatencyMs: how long it waited to start, when known.
    /// </summary>
    public static void RecordJob(string jobClass, double durationMs, bool failed = false, bool discarded = false,
        double? queueLatencyMs = null) =>
        AgentProcess.Agent?.RecordJob(jobClass, durationMs, failed, discarded, queueLatencyMs);

    /// <summary>A job that will not run again, recorded without another attempt.</summary>
    public static void RecordDiscard(string jobClass) => AgentProcess.Agent?.RecordDiscard(jobClass);

    /// <summary>Marks a job as running; pass the token returned to <see cref="JobFinished"/>.</summary>
    public static object? JobStarted(string jobClass) => AgentProcess.Agent?.JobStarted(jobClass);

    public static void JobFinished(object? token) => AgentProcess.Agent?.JobFinished(token);

    /// <summary>
    /// source says where it was raised: "route:GET /orders/{id}" or
    /// "job_class:Shop.Jobs.InvoiceJob.Send".
    /// </summary>
    public static void RecordException(Exception exception, string? source = null, bool handled = false) =>
        AgentProcess.Agent?.RecordException(exception, source, handled);

    /// <summary>
    /// Marks what follows, in this async flow, as running a job until the
    /// returned scope is disposed, so checkpoints say where they were recorded.
    /// </summary>
    public static IDisposable BeginJob() => new UnitScope(UnitOfWork.Begin(UnitOfWork.Job));

    /// <summary>The repository-relative file a method is written in, from the app's debug symbols.</summary>
    public static string? SourceFile(MethodBase method)
    {
        try
        {
            return AgentProcess.SourceMap.FileFor(method);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Whether a type is the app's own code rather than a package's or the framework's.</summary>
    public static bool IsAppType(Type type)
    {
        try
        {
            return AgentProcess.SourceMap.IsAppAssembly(type.Assembly);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>A type's name as code writes it, without generic arguments or versions.</summary>
    public static string TypeName(Type type) => Fingerprint.TypeName(type);

    /// <summary>An IANA zone name for a time zone id, which may be a Windows id.</summary>
    public static string? TimeZoneName(string? id) => TimeZones.Iana(id);

    private sealed class UnitScope : IDisposable
    {
        private readonly string? _previous;
        private bool _disposed;

        public UnitScope(string? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            UnitOfWork.End(_previous);
        }
    }
}
