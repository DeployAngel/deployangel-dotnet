using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using DeployAngel.Integration;
using Microsoft.Extensions.Logging;

namespace DeployAngel.Core;

/// <summary>
/// This process's agent, started once, and what integrations register before
/// or after it starts: the capabilities they add and the metadata sources
/// they know.
/// </summary>
internal static class AgentProcess
{
    private static readonly object Lock = new();
    private static readonly List<IMetadataSource> Sources = new();
    private static bool _hooksInstalled;
    private static SourceMap? _sourceMap;

    // Tests swap in a fake transport, a clock, and the app's assemblies.
    internal static Func<DeployAngelOptions, ITransport>? TransportForTests;
    internal static Func<double>? ClockForTests;
    internal static SourceMap? SourceMapForTests;

    public static Agent? Agent { get; private set; }
    public static DeployAngelOptions? Options { get; private set; }
    public static CapabilitySet Capabilities { get; private set; } = new();

    /// <summary>What the source map would be: the agent's, else one built for this process.</summary>
    public static SourceMap SourceMap
    {
        get
        {
            if (Agent is not null) return Agent.SourceMap;
            if (SourceMapForTests is not null) return SourceMapForTests;
            lock (Lock) return _sourceMap ??= SourceMap.ForProcess(Options?.SourceRoot);
        }
    }

    public static void AddMetadataSource(IMetadataSource source)
    {
        lock (Sources)
        {
            if (!Sources.Contains(source)) Sources.Add(source);
        }
    }

    public static IReadOnlyList<IMetadataSource> MetadataSources()
    {
        lock (Sources) return Sources.ToList();
    }

    /// <summary>
    /// Starts this process's agent, once; later calls return the same agent.
    /// environment is the host's environment name, used unless one is
    /// configured.
    /// </summary>
    public static Agent? Start(DeployAngelOptions options, string? environment = null, string? root = null,
        string? framework = null, string? frameworkVersion = null, bool eager = true)
    {
        try
        {
            lock (Lock)
            {
                if (Agent is not null) return Agent;
                Options = options;
                var resolvedEnvironment = (options.Environment ?? environment ?? "production").Trim().ToLowerInvariant();
                var resolvedRoot = options.Root ?? root ?? Directory.GetCurrentDirectory();
                var sourceMap = SourceMapForTests ?? _sourceMap ?? SourceMap.ForProcess(options.SourceRoot);
                AddMetadataSource(new ConfiguredSchedules(options));
                var metadata = new Metadata(options, sourceMap, new LiveSources());
                var runtime = new RuntimeInfo("dotnet", Environment.Version.ToString(), framework, frameworkVersion);
                Agent = new Agent(options, resolvedEnvironment, resolvedRoot, runtime, DeployAngelOptions.EnvironmentVariables(),
                    sourceMap, TransportForTests?.Invoke(options), ClockForTests, eager, Capabilities, metadata,
                    informationalVersion: EntryInformationalVersion());
                InstallProcessHooks();
                return Agent;
            }
        }
        catch (Exception error)
        {
            try
            {
                options.Log(LogLevel.Warning, $"DeployAngel failed to start: {error.GetType().Name}: {error.Message}");
            }
            catch (Exception)
            {
                // Nothing more to do.
            }
            return null;
        }
    }

    public static void Shutdown() => Agent?.Shutdown();

    internal static void ResetForTests()
    {
        lock (Lock)
        {
            Agent?.Shutdown(TimeSpan.Zero);
            Agent = null;
            Options = null;
            TransportForTests = null;
            ClockForTests = null;
            SourceMapForTests = null;
            _sourceMap = null;
            Capabilities = new CapabilitySet();
            lock (Sources) Sources.Clear();
        }
    }

    private static string? EntryInformationalVersion()
    {
        try
        {
            return Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void InstallProcessHooks()
    {
        if (_hooksInstalled) return;
        _hooksInstalled = true;
        // The host's shutdown flushes first; this covers apps without one.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
    }

    /// <summary>The registered sources as they are when the metadata is sent, not at start.</summary>
    private sealed class LiveSources : IReadOnlyList<IMetadataSource>
    {
        public IMetadataSource this[int index] => MetadataSources()[index];
        public int Count => MetadataSources().Count;
        public IEnumerator<IMetadataSource> GetEnumerator() => MetadataSources().GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
