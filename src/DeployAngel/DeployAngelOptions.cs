using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace DeployAngel;

/// <summary>
/// Agent settings: environment variables by default, then the "DeployAngel"
/// configuration section (appsettings.json), then code passed to
/// AddDeployAngel(options => ...).
/// </summary>
public sealed class DeployAngelOptions
{
    public const string DefaultEndpoint = "https://api.deployangel.com";
    private static readonly string[] TrueValues = { "1", "true", "yes", "on" };

    /// <summary>The app's ingest token (DEPLOYANGEL_TOKEN).</summary>
    public string? Token { get; set; }

    /// <summary>DEPLOYANGEL_URL.</summary>
    public string? Endpoint { get; set; } = DefaultEndpoint;

    /// <summary>
    /// Forces reporting on or off (DEPLOYANGEL_ENABLED). By default the agent
    /// reports only in the listed <see cref="Environments"/>.
    /// </summary>
    public bool? Enabled { get; set; }

    public IList<string> Environments { get; set; } = new List<string> { "production" };

    /// <summary>
    /// The deployment's name, such as "production" (DEPLOYANGEL_ENVIRONMENT).
    /// Unless set, the host's environment name, lowercased.
    /// </summary>
    public string? Environment { get; set; }

    /// <summary>DEPLOYANGEL_RELEASE_VERSION.</summary>
    public string? ReleaseVersion { get; set; }

    /// <summary>The deployed commit (DEPLOYANGEL_REVISION).</summary>
    public string? Revision { get; set; }

    /// <summary>
    /// Where the app runs, searched for a REVISION file and a git checkout
    /// (DEPLOYANGEL_ROOT). Unless set, the host's content root.
    /// </summary>
    public string? Root { get; set; }

    /// <summary>
    /// The repository root as the build saw it, which source paths are made
    /// relative to (DEPLOYANGEL_SOURCE_ROOT). Unless set, the root the
    /// package's build targets recorded in the app's assembly.
    /// </summary>
    public string? SourceRoot { get; set; }

    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);
    public int MaxQueuedPayloads { get; set; } = 10;
    public int MaxRoutes { get; set; } = 100;

    /// <summary>
    /// Whether to send the digests of the app's source files, from its debug
    /// symbols (DEPLOYANGEL_FILE_DIGESTS).
    /// </summary>
    public bool FileDigests { get; set; } = true;

    /// <summary>
    /// Off, exceptions are sent with their class and frames only, never a
    /// message, for apps whose messages may hold personal or health data
    /// (DEPLOYANGEL_EXCEPTION_MESSAGES).
    /// </summary>
    public bool ExceptionMessages { get; set; } = true;

    /// <summary>Named flows of routes and jobs that must keep working.</summary>
    public IDictionary<string, string[]> CriticalFlows { get; set; } = new Dictionary<string, string[]>();

    /// <summary>Route keys as the dashboard shows them, such as "GET /healthz".</summary>
    public IList<string> IgnoredRoutes { get; set; } = new List<string>();

    /// <summary>
    /// Work scheduled outside the app's own scheduler (cron, Heroku
    /// Scheduler, a Kubernetes CronJob), by the name it runs under: a
    /// DeployAngelAgent.RunTask name or a job class. Each schedule is a cron
    /// line or words like "every day at 4am", optionally ending in a time zone.
    /// </summary>
    public IDictionary<string, string> RecurringJobs { get; set; } = new Dictionary<string, string>();

    /// <summary>Where warnings go. AddDeployAngel sets it from the app's logging.</summary>
    public ILogger? Logger { get; set; }

    public static bool? ParseBoolean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return TrueValues.Contains(value.Trim().ToLowerInvariant());
    }

    /// <summary>The settings environment variables give.</summary>
    public static DeployAngelOptions FromEnvironment(IReadOnlyDictionary<string, string?>? env = null)
    {
        env ??= EnvironmentVariables();
        string? Get(string name) => env.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value) ? value : null;
        return new DeployAngelOptions
        {
            Token = Get("DEPLOYANGEL_TOKEN"),
            Endpoint = Get("DEPLOYANGEL_URL") ?? DefaultEndpoint,
            Enabled = ParseBoolean(Get("DEPLOYANGEL_ENABLED")),
            Environment = Get("DEPLOYANGEL_ENVIRONMENT"),
            ReleaseVersion = Get("DEPLOYANGEL_RELEASE_VERSION"),
            Revision = Get("DEPLOYANGEL_REVISION"),
            Root = Get("DEPLOYANGEL_ROOT"),
            SourceRoot = Get("DEPLOYANGEL_SOURCE_ROOT"),
            FileDigests = ParseBoolean(Get("DEPLOYANGEL_FILE_DIGESTS")) != false,
            ExceptionMessages = ParseBoolean(Get("DEPLOYANGEL_EXCEPTION_MESSAGES")) != false,
        };
    }

    internal static IReadOnlyDictionary<string, string?> EnvironmentVariables()
    {
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
            variables[(string)entry.Key] = entry.Value as string;
        return variables;
    }

    /// <summary>
    /// Reports only with a token. By default only in the listed environments;
    /// Enabled forces it on or off.
    /// </summary>
    public bool IsActive(string environment)
    {
        if (string.IsNullOrEmpty(Token) || string.IsNullOrEmpty(Endpoint)) return false;
        if (Enabled is not null) return Enabled.Value;
        return Environments.Contains(environment);
    }

    /// <summary>
    /// Whether requests to a route are left out. HEAD is answered by the GET
    /// endpoint, so ignoring "GET /healthz" ignores "HEAD /healthz" too.
    /// </summary>
    public bool IsIgnoredRoute(string key)
    {
        if (IgnoredRoutes.Count == 0) return false;
        if (IgnoredRoutes.Contains(key)) return true;
        return key.StartsWith("HEAD ", StringComparison.Ordinal) && IgnoredRoutes.Contains("GET " + key[5..]);
    }

    internal void Log(LogLevel level, string message)
    {
        try
        {
            if (Logger is not null) Logger.Log(level, "{Message}", message);
            else if (level >= LogLevel.Warning) Console.Error.WriteLine(message);
        }
        catch (Exception)
        {
            // Logging must never fail the app.
        }
    }
}
