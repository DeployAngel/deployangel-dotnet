using System;
using System.Collections.Generic;

namespace DeployAngel.Core;

/// <summary>
/// The jobs this process is running now, so each payload can say how long
/// the oldest of each class has been running: a job that hangs never
/// finishes, so it never shows up in the counts. Starting and finishing is a
/// dictionary update under a lock.
/// </summary>
internal sealed class RunningJobs
{
    // More than any process runs at once; past it, new jobs go untracked
    // rather than growing memory.
    public const int Max = 1000;

    private readonly Func<double> _clock;
    private readonly Dictionary<object, (string JobClass, double StartedAt)> _jobs = new();
    private readonly object _lock = new();

    public RunningJobs(Func<double> clock)
    {
        _clock = clock;
    }

    /// <summary>Returns the token to pass to <see cref="Finish"/>.</summary>
    public object Start(string jobClass)
    {
        var token = new object();
        lock (_lock)
        {
            if (_jobs.Count < Max) _jobs[token] = (jobClass, _clock());
        }
        return token;
    }

    public void Finish(object? token)
    {
        if (token is null) return;
        lock (_lock) _jobs.Remove(token);
    }

    /// <summary>
    /// {job class: (running, oldest running ms)} for jobs that started by
    /// <paramref name="at"/> and are still running.
    /// </summary>
    public Dictionary<string, (int Count, long OldestMs)> Snapshot(double at)
    {
        var running = new Dictionary<string, (int Count, long OldestMs)>();
        lock (_lock)
        {
            foreach (var (jobClass, startedAt) in _jobs.Values)
            {
                if (startedAt > at) continue;
                var ageMs = (long)Math.Round((at - startedAt) * 1000, MidpointRounding.ToEven);
                running.TryGetValue(jobClass, out var entry);
                running[jobClass] = (entry.Count + 1, Math.Max(entry.OldestMs, ageMs));
            }
        }
        return running;
    }
}
