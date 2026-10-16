using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DeployAngel.Core;
using Microsoft.Extensions.Logging;

namespace DeployAngel;

/// <summary>
/// What application code records through: checkpoints, handled exceptions,
/// and scheduled work. Every method is safe to call anywhere: it never
/// throws, never touches the network, and does nothing while the agent isn't
/// recording.
/// </summary>
public static class DeployAngelAgent
{
    private static readonly Regex CheckpointName = new(@"\A[a-z0-9][a-z0-9_.:-]{0,99}\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex TaskName = new(@"\A\S[^\x00-\x1f\x7f]{0,199}\z", RegexOptions.CultureInvariant);
    private static bool _warnedCheckpoint;
    private static bool _warnedTask;

    /// <summary>Whether this process is reporting to DeployAngel.</summary>
    public static bool Recording => AgentProcess.Agent is { Active: true };

    /// <summary>
    /// Starts the agent in an app without the .NET generic host (a console
    /// app or a script). Apps with a host use services.AddDeployAngel().
    /// </summary>
    public static bool Start(Action<DeployAngelOptions>? configure = null, string? environment = null)
    {
        try
        {
            var options = DeployAngelOptions.FromEnvironment();
            configure?.Invoke(options);
            return AgentProcess.Start(options, environment ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"),
                eager: false) is { Active: true };
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Sends what's recorded, including the minute in progress. The host's
    /// shutdown does this; call it at the end of a short-lived process.
    /// </summary>
    public static void Shutdown() => AgentProcess.Shutdown();

    /// <summary>
    /// Counts a business event, such as Checkpoint("order.fulfilled").
    /// DeployAngel learns each checkpoint's normal rate and fails a release
    /// after which it drops sharply or stops.
    /// </summary>
    public static void Checkpoint(string name, long count = 1)
    {
        try
        {
            if (name is null || !CheckpointName.IsMatch(name) || count <= 0)
            {
                if (!_warnedCheckpoint)
                    AgentProcess.Options?.Log(LogLevel.Warning,
                        $"DeployAngel ignored checkpoint \"{name}\": use letters, numbers, and . _ : - (max 100)");
                _warnedCheckpoint = true;
                return;
            }
            AgentProcess.Capabilities.Add("checkpoints");
            AgentProcess.Agent?.RecordCheckpoint(name, count);
        }
        catch (Exception)
        {
            // Never fail the app.
        }
    }

    /// <summary>
    /// Reports an exception the app caught and handled itself. It's shown for
    /// context; only unhandled exceptions count toward verdicts.
    /// </summary>
    public static void Notify(Exception exception) => AgentProcess.Agent?.RecordException(exception, handled: true);

    /// <summary>
    /// Records work that runs on a schedule outside a job system (cron,
    /// Heroku Scheduler, a Kubernetes CronJob, a BackgroundService loop) as a
    /// job run under its name, with its duration and whether it threw.
    /// Options.RecurringJobs declares when it should run, so DeployAngel
    /// expects it like any recurring job. Exceptions are recorded and
    /// rethrown untouched.
    /// </summary>
    public static void RunTask(string name, Action work)
    {
        var run = TaskRun.Begin(name);
        try
        {
            work();
        }
        catch (Exception error)
        {
            run?.End(error);
            throw;
        }
        run?.End(null);
    }

    /// <inheritdoc cref="RunTask(string, Action)"/>
    public static T RunTask<T>(string name, Func<T> work)
    {
        var run = TaskRun.Begin(name);
        T result;
        try
        {
            result = work();
        }
        catch (Exception error)
        {
            run?.End(error);
            throw;
        }
        run?.End(null);
        return result;
    }

    /// <inheritdoc cref="RunTask(string, Action)"/>
    public static async Task RunTaskAsync(string name, Func<Task> work)
    {
        var run = TaskRun.Begin(name);
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            run?.End(error);
            throw;
        }
        run?.End(null);
    }

    /// <inheritdoc cref="RunTask(string, Action)"/>
    public static async Task<T> RunTaskAsync<T>(string name, Func<Task<T>> work)
    {
        var run = TaskRun.Begin(name);
        T result;
        try
        {
            result = await work().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            run?.End(error);
            throw;
        }
        run?.End(null);
        return result;
    }

    private sealed class TaskRun
    {
        private readonly string _name;
        private readonly long _started;
        private readonly string? _previousUnit;
        private readonly object? _running;

        private TaskRun(string name)
        {
            _name = name;
            AgentProcess.Capabilities.Add("jobs");
            _previousUnit = UnitOfWork.Begin(UnitOfWork.Job);
            _running = AgentProcess.Agent?.JobStarted(name);
            _started = Stopwatch.GetTimestamp();
        }

        public static TaskRun? Begin(string name)
        {
            try
            {
                if (!Recording) return null;
                if (name is not null && TaskName.IsMatch(name)) return new TaskRun(name);
                if (!_warnedTask)
                    AgentProcess.Options?.Log(LogLevel.Warning,
                        $"DeployAngel didn't record task \"{name}\": use a name of up to 200 printable characters");
                _warnedTask = true;
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void End(Exception? error)
        {
            try
            {
                var agent = AgentProcess.Agent;
                if (agent is null) return;
                if (error is not null) agent.RecordException(error, $"job_class:{_name}");
                agent.RecordJob(_name, Stopwatch.GetElapsedTime(_started).TotalMilliseconds, failed: error is not null);
                agent.JobFinished(_running);
            }
            catch (Exception)
            {
                // Never fail the app.
            }
            finally
            {
                UnitOfWork.End(_previousUnit);
            }
        }
    }
}
