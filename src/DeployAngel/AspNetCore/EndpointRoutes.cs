using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DeployAngel.Core;
using DeployAngel.Integration;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DeployAngel.AspNetCore;

/// <summary>
/// The route table from the app's endpoints, keyed the way the middleware
/// records requests ("GET /orders/{id}"), with the controller and action and
/// the files that implement each route.
///
/// Only endpoints the app's own code handles are listed: controllers, Razor
/// Pages, minimal API handlers, and Razor components in the app's
/// assemblies. Framework endpoints (health checks, static assets, Identity's
/// pages) are still recorded when requested; they're just not listed as
/// routes a release should exercise.
/// </summary>
internal sealed class EndpointRoutes : IMetadataSource
{
    // An endpoint whose methods can't be told (app.Map(...)) is listed once,
    // under ANY, so DeployAngel still knows which file serves the path.
    private const string Any = "ANY";
    private static readonly string[] SkippedMethods = { "HEAD", "OPTIONS" };
    private readonly IServiceProvider _services;

    public EndpointRoutes(IServiceProvider services)
    {
        _services = services;
    }

    public IEnumerable<RouteInfo> Routes()
    {
        var source = _services.GetService<EndpointDataSource>();
        if (source is null) return Array.Empty<RouteInfo>();
        var map = AgentProcess.SourceMap;
        var routes = new List<RouteInfo>();
        foreach (var endpoint in source.Endpoints.OfType<RouteEndpoint>())
        {
            try
            {
                if (endpoint.Metadata.GetMetadata<ISuppressMatchingMetadata>()?.SuppressMatching == true) continue;
                var handler = Handler(endpoint, map);
                if (handler is null) continue;
                var pattern = RouteKeys.Pattern(endpoint.RoutePattern);
                if (RouteKeys.IsHealthCheckPath(pattern)) continue;
                foreach (var method in Methods(endpoint))
                    routes.Add(new RouteInfo(RouteKeys.Key(method, pattern), handler.Value.Controller, handler.Value.Action,
                        handler.Value.Files));
            }
            catch (Exception)
            {
                // One endpoint that can't be read leaves the rest.
            }
        }
        return routes;
    }

    private static IEnumerable<string> Methods(Endpoint endpoint)
    {
        var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods
            .Select(m => m.ToUpperInvariant()).Where(m => !SkippedMethods.Contains(m)).Distinct().OrderBy(m => m).ToList();
        return methods is { Count: > 0 } ? methods : new List<string> { Any };
    }

    private static (string? Controller, string? Action, IReadOnlyList<string>? Files)? Handler(Endpoint endpoint, SourceMap map)
    {
        var action = endpoint.Metadata.GetMetadata<ControllerActionDescriptor>();
        if (action is not null)
        {
            if (!map.IsAppAssembly(action.ControllerTypeInfo.Assembly)) return null;
            var files = new List<string>();
            AddFile(files, map.FileFor(action.MethodInfo));
            AddFile(files, View(map, action));
            return (Fingerprint.TypeName(action.ControllerTypeInfo), action.MethodInfo.Name, files);
        }
        var page = endpoint.Metadata.GetMetadata<PageActionDescriptor>();
        if (page is not null)
        {
            // The digests list only the app's own files, so a page there is the
            // app's, not a Razor class library's (Identity's UI). Without debug
            // symbols, pages outside areas are taken to be the app's.
            var file = ManifestFile(map, page.RelativePath);
            if (file is null && (map.Manifest().Count > 0 || page.AreaName is not null)) return null;
            return (null, page.ViewEnginePath, file is null ? null : new[] { file });
        }
        var handler = endpoint.Metadata.GetMetadata<MethodInfo>();
        if (handler is not null)
        {
            if (!map.IsAppAssembly(handler.Module.Assembly)) return null;
            var file = map.FileFor(handler);
            return (null, null, file is null ? null : new[] { file });
        }
        var component = endpoint.Metadata.GetMetadata<ComponentTypeMetadata>()?.Type;
        if (component is not null)
        {
            if (!map.IsAppAssembly(component.Assembly)) return null;
            var render = component.GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                null, new[] { typeof(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder) }, null);
            var file = map.FileFor(render);
            return (Fingerprint.TypeName(component), null, file is null ? null : new[] { file });
        }
        return null;
    }

    /// <summary>The view an action renders by convention, when the app has it.</summary>
    private static string? View(SourceMap map, ControllerActionDescriptor action)
    {
        var area = action.RouteValues.TryGetValue("area", out var a) && !string.IsNullOrEmpty(a) ? $"/Areas/{a}" : "";
        return ManifestFile(map, $"{area}/Views/{action.ControllerName}/{action.ActionName}.cshtml");
    }

    /// <summary>The repository path of a project-relative file ("/Pages/Index.cshtml").</summary>
    private static string? ManifestFile(SourceMap map, string projectPath)
    {
        var suffix = projectPath.StartsWith('/') ? projectPath : "/" + projectPath;
        foreach (var path in map.Manifest().Files.Keys)
        {
            if (("/" + path).EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return path;
        }
        return null;
    }

    private static void AddFile(List<string> files, string? file)
    {
        if (file is not null && !files.Contains(file)) files.Add(file);
    }
}
