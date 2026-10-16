using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeployAngel.Core;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DeployAngel.AspNetCore;

/// <summary>
/// Starts the agent as the host starts, before other hosted services and the
/// web server, so the process reports from boot; and sends what's left
/// once everything else has stopped, so the last requests and jobs count.
/// </summary>
internal sealed class DeployAngelHostedService : IHostedLifecycleService
{
    private readonly IServiceProvider _services;
    private readonly Action<DeployAngelOptions>? _configure;

    public DeployAngelHostedService(IServiceProvider services, DeployAngelConfigure configure)
    {
        _services = services;
        _configure = configure.Action;
    }

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        try
        {
            var options = DeployAngelOptions.FromEnvironment();
            if (_services.GetService<IConfiguration>()?.GetSection("DeployAngel") is { } section)
                ConfigurationOptions.Apply(options, section);
            options.Logger ??= _services.GetService<ILoggerFactory>()?.CreateLogger("DeployAngel");
            _configure?.Invoke(options);
            var host = _services.GetService<IHostEnvironment>();
            var (framework, version) = Framework();
            AgentProcess.AddMetadataSource(new EndpointRoutes(_services));
            AgentProcess.Start(options, host?.EnvironmentName, host?.ContentRootPath, framework, version, eager: true);
        }
        catch (Exception)
        {
            // The app starts regardless.
        }
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        AgentProcess.Shutdown();
        return Task.CompletedTask;
    }

    /// <summary>
    /// ASP.NET Core in a process that serves HTTP; else the job framework a
    /// worker runs, so the cloud keeps the web framework a release's web
    /// processes report.
    /// </summary>
    private (string? Name, string? Version) Framework()
    {
        if (_services.GetService<IServer>() is not null)
            return ("aspnetcore", AgentInfo.PlainVersion(typeof(Microsoft.AspNetCore.Http.HttpContext).Assembly));
        var hangfire = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Hangfire.Core");
        return hangfire is null ? (null, null) : ("hangfire", AgentInfo.PlainVersion(hangfire));
    }
}

/// <summary>The code AddDeployAngel was given, applied last.</summary>
internal sealed record DeployAngelConfigure(Action<DeployAngelOptions>? Action);
