using System;
using System.Reflection;

namespace DeployAngel.Tool;

/// <summary>The tool's version and how it names itself to DeployAngel.</summary>
internal static class ToolInfo
{
    public const string DefaultEndpoint = "https://api.deployangel.com";

    /// <summary>The package version, from the assembly, without the source revision the SDK appends after a "+".</summary>
    public static string Version { get; } = ReadVersion();

    // Named like the Python CLI's deployangel-cli-python/V, so the cloud can
    // tell the command from the in-process agent (deployangel-dotnet/V).
    public static string UserAgent { get; } = $"deployangel-cli-dotnet/{Version} dotnet/{Environment.Version}";

    private static string ReadVersion()
    {
        var assembly = typeof(ToolInfo).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informational))
        {
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus >= 0 ? informational[..plus] : informational;
        }
        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
