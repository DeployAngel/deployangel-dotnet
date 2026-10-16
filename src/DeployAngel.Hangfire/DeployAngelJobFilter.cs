using System;
using System.Diagnostics;
using DeployAngel.Integration;
using Hangfire.Client;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.States;

namespace DeployAngel.Hangfire;

/// <summary>
/// Records every Hangfire job attempt: its duration, whether it threw, how
/// long it waited in the queue, and whether it will run again. Jobs are
/// keyed by type and method ("Shop.Jobs.InvoiceJob.Send"), the way Hangfire
/// names them.
/// </summary>
internal sealed class DeployAngelJobFilter : IClientFilter, IServerFilter, IElectStateFilter
{
    // After AutomaticRetry (order 20), so the state elected is the one
    // retries settled on.
    public const int Order = 1000;
    // Set when a job is created straight into a queue and read back when it
    // runs, so queue latency can be measured. Scheduled jobs and retries
    // wait on purpose, so they have none.
    internal const string EnqueuedAtParameter = "DeployAngel.EnqueuedAt";
    private const string RunItem = "DeployAngel.Run";

    public void OnCreating(CreatingContext context)
    {
        try
        {
            if (Instrumentation.Recording && context.InitialState is EnqueuedState)
                context.SetJobParameter(EnqueuedAtParameter, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        catch (Exception)
        {
            // Never fail the enqueue.
        }
    }

    public void OnCreated(CreatedContext context)
    {
    }

    public void OnPerforming(PerformingContext context)
    {
        try
        {
            if (!Instrumentation.Recording) return;
            var jobClass = JobClass(context.BackgroundJob.Job);
            context.Items[RunItem] = new Run(jobClass, QueueLatency(context), Instrumentation.BeginJob(),
                Instrumentation.JobStarted(jobClass));
        }
        catch (Exception)
        {
            // Never fail the job.
        }
    }

    public void OnPerformed(PerformedContext context)
    {
        try
        {
            if (!context.Items.TryGetValue(RunItem, out var item) || item is not Run run) return;
            context.Items.Remove(RunItem);
            run.Unit.Dispose();
            Instrumentation.JobFinished(run.Running);
            // Stopped by a shutdown: Hangfire runs it again elsewhere, so this
            // attempt says nothing about the job.
            if (context.Canceled) return;
            var failed = context.Exception is not null && !context.ExceptionHandled;
            if (failed)
            {
                var cause = context.Exception is JobPerformanceException { InnerException: { } inner } ? inner : context.Exception!;
                Instrumentation.RecordException(cause, $"job_class:{run.JobClass}");
            }
            Instrumentation.RecordJob(run.JobClass, Stopwatch.GetElapsedTime(run.Started).TotalMilliseconds, failed,
                queueLatencyMs: run.QueueLatencyMs);
        }
        catch (Exception)
        {
            // Never fail the job.
        }
    }

    /// <summary>
    /// A failed state is final: retries ran out (or there were none), so the
    /// job won't run again. So is a deletion after the last attempt.
    /// </summary>
    public void OnStateElection(ElectStateContext context)
    {
        try
        {
            if (!Instrumentation.Recording || context.BackgroundJob?.Job is not { } job) return;
            var final = context.CandidateState switch
            {
                FailedState => true,
                DeletedState { ExceptionInfo: not null } => true,
                _ => false,
            };
            if (final) Instrumentation.RecordDiscard(JobClass(job));
        }
        catch (Exception)
        {
            // Never fail the state change.
        }
    }

    public static string JobClass(Job job) => $"{Instrumentation.TypeName(job.Type)}.{job.Method.Name}";

    /// <summary>For a job's first attempt, how long it waited after it was enqueued.</summary>
    private static double? QueueLatency(PerformingContext context)
    {
        if (context.GetJobParameter<int>("RetryCount") > 0) return null;
        var enqueuedAt = context.GetJobParameter<long?>(EnqueuedAtParameter);
        if (enqueuedAt is null) return null;
        var waited = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - enqueuedAt.Value;
        return waited >= 0 ? waited : null;
    }

    private sealed class Run
    {
        public Run(string jobClass, double? queueLatencyMs, IDisposable unit, object? running)
        {
            JobClass = jobClass;
            QueueLatencyMs = queueLatencyMs;
            Unit = unit;
            Running = running;
            Started = Stopwatch.GetTimestamp();
        }

        public string JobClass { get; }
        public double? QueueLatencyMs { get; }
        public IDisposable Unit { get; }
        public object? Running { get; }
        public long Started { get; }
    }
}
