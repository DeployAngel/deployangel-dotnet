using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.AspNetCore.Routing.Patterns;

namespace DeployAngel.AspNetCore;

/// <summary>
/// Route keys from the pattern the endpoint matched ("GET /orders/{id}"),
/// never the raw path, which keeps IDs out of route keys.
/// </summary>
internal static class RouteKeys
{
    // The route values a conventional route fills in from the action it
    // reaches ("{controller=Home}/{action=Index}/{id?}" is "/Orders/Show/{id?}"
    // for OrdersController.Show), so each action has its own key.
    // Each endpoint's pattern is rendered once; requests read it from here.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RoutePattern, string> Rendered = new();

    private static readonly string[] RequiredValueNames = { "area", "controller", "action", "page", "handler" };

    // Health checks at a conventional path. Load balancers and uptime
    // monitors call them all the time and they answer fast, so counting them
    // would make any app look busy and healthy. Others can be left out with
    // IgnoredRoutes.
    private static readonly string[] HealthCheckPaths =
        { "up", "health", "healthz", "healthcheck", "health_check", "livez", "readyz", "statusz", "ping", "ht", "alive" };

    /// <summary>
    /// The pattern with a leading slash, parameters as "{name}", "{name?}", or
    /// "{*name}" (constraints and defaults dropped, so adding one doesn't
    /// rename the route).
    /// </summary>
    public static string Pattern(RoutePattern pattern) => Rendered.GetValue(pattern, Render);

    private static string Render(RoutePattern pattern)
    {
        var segments = new List<string>();
        foreach (var segment in pattern.PathSegments)
        {
            var text = new StringBuilder();
            foreach (var part in segment.Parts)
            {
                switch (part)
                {
                    case RoutePatternLiteralPart literal:
                        text.Append(literal.Content);
                        break;
                    case RoutePatternSeparatorPart separator:
                        text.Append(separator.Content);
                        break;
                    case RoutePatternParameterPart parameter:
                        if (RequiredValue(pattern, parameter.Name) is { } value) text.Append(value);
                        else if (parameter.IsCatchAll) text.Append("{*").Append(parameter.Name).Append('}');
                        else text.Append('{').Append(parameter.Name).Append(parameter.IsOptional ? "?}" : "}");
                        break;
                }
            }
            // An optional area or similar that the action leaves empty.
            if (text.Length > 0) segments.Add(text.ToString());
        }
        return "/" + string.Join("/", segments);
    }

    public static string Key(string method, string pattern) => $"{method} {pattern}";

    /// <summary>
    /// A path segment that matches many values: ASP.NET Core's {id}, and the
    /// :id, *path, and &lt;id&gt; of other frameworks' patterns.
    /// </summary>
    public static bool IsPlaceholder(string segment) =>
        segment.StartsWith(':') || segment.StartsWith('*') || segment.IndexOfAny(new[] { '<', '{', '(' }) >= 0;

    /// <summary>
    /// A static route ending in a health-check name, such as "/healthz" or
    /// "/api/livez". A route with a placeholder, such as
    /// "/patients/{id}/health", is a real page about something, so it's
    /// recorded.
    /// </summary>
    public static bool IsHealthCheckPath(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        var segments = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 && HealthCheckPaths.Contains(segments[^1].ToLowerInvariant()) &&
               !segments.Any(IsPlaceholder);
    }

    private static string? RequiredValue(RoutePattern pattern, string name)
    {
        if (!RequiredValueNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return null;
        return pattern.RequiredValues.TryGetValue(name, out var value) && value is string text && text.Length > 0
            ? text
            : null;
    }
}
