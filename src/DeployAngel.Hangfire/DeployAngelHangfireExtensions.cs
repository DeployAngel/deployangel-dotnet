using DeployAngel.Hangfire;
using DeployAngel.Integration;

// In Hangfire's namespace, beside its own Use* methods.
namespace Hangfire;

public static class DeployAngelHangfireExtensions
{
    /// <summary>
    /// Reports every job attempt and the recurring jobs to DeployAngel:
    /// services.AddHangfire(config => config.UseDeployAngel()...). The agent
    /// itself starts with services.AddDeployAngel().
    /// </summary>
    public static IGlobalConfiguration UseDeployAngel(this IGlobalConfiguration configuration)
    {
        lock (Lock)
        {
            if (!_installed)
            {
                _installed = true;
                GlobalJobFilters.Filters.Add(new DeployAngelJobFilter(), DeployAngelJobFilter.Order);
            }
        }
        Instrumentation.AddCapability("jobs");
        Instrumentation.AddMetadataSource(Source);
        return configuration;
    }

    private static readonly object Lock = new();
    private static readonly HangfireSource Source = new();
    private static bool _installed;
}
