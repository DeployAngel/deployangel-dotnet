using System.Reflection;

namespace DeployAngel.Core;

internal static class AgentInfo
{
    public const string Name = "deployangel-dotnet";

    public static readonly string Version = PlainVersion(typeof(AgentInfo).Assembly);

    /// <summary>An assembly's informational version without its "+commit" suffix.</summary>
    public static string PlainVersion(Assembly assembly)
    {
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? assembly.GetName().Version?.ToString() ?? "0.0.0";
        var plus = version.IndexOf('+');
        return plus >= 0 ? version[..plus] : version;
    }
}
