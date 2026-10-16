using System;
using System.Diagnostics;
using System.Threading.Tasks;
using DeployAngel.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace DeployAngel.AspNetCore;

/// <summary>Puts the middleware first, around everything the app adds.</summary>
internal sealed class DeployAngelStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.UseMiddleware<DeployAngelMiddleware>();
        next(app);
    };
}

/// <summary>
/// Records each request against the route pattern its endpoint matched.
/// Outermost, so the time includes the app's own middleware, and a response
/// the app's exception handler rendered still counts the exception.
/// </summary>
internal sealed class DeployAngelMiddleware
{
    // MapHealthChecks names its endpoints this, wherever they're mounted.
    private const string HealthChecksDisplayName = "Health checks";
    private readonly RequestDelegate _next;

    public DeployAngelMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!DeployAngelAgent.Recording)
        {
            await _next(context);
            return;
        }
        var started = Stopwatch.GetTimestamp();
        EndpointRecorder? endpoints = null;
        string? previousUnit = null;
        try
        {
            Redaction.SetRequestHost(context.Request.Host.Host);
            endpoints = EndpointRecorder.Install(context);
            previousUnit = UnitOfWork.Begin(UnitOfWork.Http);
        }
        catch (Exception)
        {
            // Recording is best effort; the request goes on regardless.
        }
        try
        {
            await _next(context);
        }
        catch (Exception error)
        {
            UnitOfWork.End(previousUnit);
            Record(context, endpoints, started, error, thrownOut: true);
            throw;
        }
        UnitOfWork.End(previousUnit);
        Record(context, endpoints, started, null, thrownOut: false);
    }

    private static void Record(HttpContext context, EndpointRecorder? endpoints, long started, Exception? thrown,
        bool thrownOut)
    {
        try
        {
            var agent = AgentProcess.Agent;
            if (agent is null) return;
            var durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            // An exception the app's exception handler rendered as a response.
            var handlerFeature = context.Features.Get<IExceptionHandlerFeature>();
            var exception = thrown ?? handlerFeature?.Error;
            // Once the response has started, it goes out with the status it has.
            var status = thrownOut && !context.Response.HasStarted ? 500 : context.Response.StatusCode;
            var endpoint = endpoints?.Matched ?? handlerFeature?.Endpoint ?? context.GetEndpoint();
            var method = context.Request.Method;
            var pattern = endpoint is RouteEndpoint route ? RouteKeys.Pattern(route.RoutePattern) : null;
            var healthCheck = endpoint?.DisplayName == HealthChecksDisplayName || RouteKeys.IsHealthCheckPath(pattern);
            if (healthCheck) return;
            var key = pattern is null ? null : RouteKeys.Key(method, pattern);
            if (key is not null && agent.Options.IsIgnoredRoute(key)) return;
            // Unrouted successes are static files and similar; unrouted
            // errors (such as 404s) are still recorded.
            if (key is null && status < 400) return;
            key ??= $"{method} unmatched";
            var unhandled = exception is not null && status >= 500;
            if (unhandled) agent.RecordException(exception, $"route:{key}");
            // A 4xx no route matched is mostly bots probing paths like
            // /wp-admin. It stays visible under "unmatched", but out of the
            // app's totals, so it doesn't add to the evidence or dilute real
            // pages' latency. An unrouted 5xx still counts: something broke.
            agent.RecordRequest(key, status, durationMs, unhandled, inTotals: pattern is not null || status >= 500);
        }
        catch (Exception)
        {
            // Never fail the request.
        }
    }
}

/// <summary>
/// Remembers the first route endpoint the request matched. The exception
/// handler and status-code pages clear the endpoint and run the request
/// again against an error page, which shouldn't take its place.
/// </summary>
internal sealed class EndpointRecorder : IEndpointFeature
{
    private Endpoint? _endpoint;

    private EndpointRecorder(Endpoint? current)
    {
        _endpoint = current;
        if (current is RouteEndpoint) Matched = current;
    }

    public Endpoint? Matched { get; private set; }

    public Endpoint? Endpoint
    {
        get => _endpoint;
        set
        {
            _endpoint = value;
            if (Matched is null && value is RouteEndpoint) Matched = value;
        }
    }

    public static EndpointRecorder Install(HttpContext context)
    {
        var recorder = new EndpointRecorder(context.Features.Get<IEndpointFeature>()?.Endpoint);
        context.Features.Set<IEndpointFeature>(recorder);
        return recorder;
    }
}
