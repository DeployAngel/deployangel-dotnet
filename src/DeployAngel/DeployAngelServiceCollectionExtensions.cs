using System;
using DeployAngel.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

// In the DI namespace, like the framework's own Add* methods, so it's found
// without another using.
namespace Microsoft.Extensions.DependencyInjection;

public static class DeployAngelServiceCollectionExtensions
{
    /// <summary>
    /// Adds the DeployAngel agent: it records every request and unhandled
    /// exception in a web app, and starts and stops with the host in a
    /// worker. Settings come from DEPLOYANGEL_* environment variables, then
    /// the "DeployAngel" configuration section, then <paramref name="configure"/>.
    /// It only reports with a token, and by default only in production.
    /// </summary>
    public static IServiceCollection AddDeployAngel(this IServiceCollection services,
        Action<DeployAngel.DeployAngelOptions>? configure = null)
    {
        services.TryAddSingleton(new DeployAngelConfigure(configure));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IStartupFilter, DeployAngelStartupFilter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, DeployAngelHostedService>());
        return services;
    }
}
