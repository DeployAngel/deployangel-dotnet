using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DeployAngel.Tool;

/// <summary>
/// What a .NET checkout declares, read from its files, never by building or
/// loading it: the NuGet packages its projects reference (lock files first,
/// since they pin the version), and whether its code calls a method.
/// </summary>
internal sealed class ProjectFiles
{
    // Build output, dependencies, and tool state: never the app's own files.
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", "packages", "TestResults", "artifacts",
    };

    private static readonly string[] ProjectExtensions = { ".csproj", ".fsproj", ".vbproj" };
    private static readonly string[] SourceExtensions = { ".cs", ".fs", ".vb" };
    private static readonly string[] SharedMsBuildFiles = { "Directory.Packages.props", "Directory.Build.props", "Directory.Build.targets" };

    // Bounds the walk, so doctor stays quick in a huge monorepo.
    private const int MaxEntries = 20_000;
    private const int MaxSourceBytes = 1_000_000;

    private static readonly Regex Comment = new("<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex PackageElement = new(
        @"<(?<tag>PackageReference|PackageVersion|GlobalPackageReference)\b(?<attributes>[^>]*?)(?:/>|>(?<body>.*?)</\k<tag>\s*>)",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex PlainVersion = new(@"\A\[?(?<version>\d+(?:\.\d+){0,3}(?:-[0-9A-Za-z.-]+)?)\]?\z", RegexOptions.CultureInvariant);
    private static readonly Regex PaketPackage = new(@"^ {4}(?<name>[^\s(]+) \((?<version>[^)]+)\)", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private readonly string _root;
    private readonly List<string> _sourceFiles = new();
    private bool? _called;

    private ProjectFiles(string root)
    {
        _root = root;
    }

    /// <summary>(relative path with forward slashes, contents) of each file that declares packages.</summary>
    public List<(string Path, string Text)> Dependencies { get; } = new();

    public IEnumerable<string> ProjectPaths => Dependencies.Select(file => file.Path)
        .Where(path => ProjectExtensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)));

    public bool UsesPaket => Dependencies.Any(file => Name(file.Path) == "paket.lock") || File.Exists(Path.Combine(_root, "paket.dependencies"));

    public static ProjectFiles Scan(string root)
    {
        var scan = new ProjectFiles(root);
        // Sorted afterwards, so the walk's order doesn't matter.
        var found = new List<(int Rank, string Path)>();
        var entries = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0 && entries < MaxEntries)
        {
            var directory = pending.Pop();
            List<string> children;
            try
            {
                children = Directory.EnumerateFileSystemEntries(directory).ToList();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var child in children)
            {
                entries++;
                var name = Path.GetFileName(child);
                if (Directory.Exists(child))
                {
                    if (!name.StartsWith('.') && !SkippedDirectories.Contains(name))
                    {
                        pending.Push(child);
                    }
                    continue;
                }
                var relative = Path.GetRelativePath(root, child).Replace(Path.DirectorySeparatorChar, '/');
                var rank = Rank(name);
                if (rank is not null)
                {
                    found.Add((rank.Value, relative));
                }
                else if (SourceExtensions.Any(extension => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
                {
                    scan._sourceFiles.Add(child);
                }
            }
        }
        foreach (var (_, relative) in found.OrderBy(file => file.Rank).ThenBy(file => file.Path, StringComparer.Ordinal))
        {
            var text = Read(Path.Combine(root, relative));
            if (text is not null)
            {
                scan.Dependencies.Add((relative, text));
            }
        }
        return scan;
    }

    /// <summary>
    /// (file, version or null) for the first file that references the
    /// package, preferring one that pins its version; null when none does.
    /// NuGet package IDs compare case-insensitively.
    /// </summary>
    public (string File, string? Version)? Find(string package)
    {
        (string, string?)? first = null;
        (string, string?)? central = null;
        foreach (var (path, text) in Dependencies)
        {
            var (found, version, centralVersion) = Look(Name(path), text, package);
            if (found && version is not null)
            {
                return (path, version);
            }
            if (found && first is null)
            {
                first = (path, null);
            }
            if (centralVersion is not null && central is null)
            {
                central = (path, centralVersion);
            }
        }
        // With central package management, the version is in
        // Directory.Packages.props and the reference in the project.
        return first is null ? null : central ?? first;
    }

    /// <summary>Whether any of the checkout's code calls the method, as <c>AddDeployAngel(</c>.</summary>
    public bool Calls(string method)
    {
        if (_called is null)
        {
            var call = new Regex($@"\b{Regex.Escape(method)}\s*\(", RegexOptions.CultureInvariant);
            _called = _sourceFiles.Any(file =>
            {
                try
                {
                    var info = new FileInfo(file);
                    return info.Length <= MaxSourceBytes && call.IsMatch(File.ReadAllText(file));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    return false;
                }
            });
        }
        return _called.Value;
    }

    private static (bool Found, string? Version, string? CentralVersion) Look(string name, string text, string package)
    {
        if (name == "packages.lock.json")
        {
            var resolved = LockedVersion(text, package);
            return (resolved is not null, resolved?.Length > 0 ? resolved : null, null);
        }
        if (name == "paket.lock")
        {
            var match = PaketPackage.Matches(text).FirstOrDefault(m => string.Equals(m.Groups["name"].Value, package, StringComparison.OrdinalIgnoreCase));
            return (match is not null, match?.Groups["version"].Value, null);
        }
        bool found = false;
        string? version = null;
        string? central = null;
        foreach (Match element in PackageElement.Matches(Comment.Replace(text, "")))
        {
            var attributes = element.Groups["attributes"].Value;
            if (!string.Equals(Attribute(attributes, "Include"), package, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var declared = Attribute(attributes, "Version") ?? Attribute(attributes, "VersionOverride") ?? ChildVersion(element.Groups["body"].Value);
            var plain = declared is null ? null : PlainVersion.Match(declared.Trim());
            var pinned = plain is { Success: true } ? plain.Groups["version"].Value : null;
            if (element.Groups["tag"].Value == "PackageVersion")
            {
                central ??= pinned;
                continue;
            }
            found = true;
            version ??= pinned;
        }
        return (found, version, central);
    }

    /// <summary>The version a packages.lock.json resolved the package to: "" when it lists it without one, null when it doesn't.</summary>
    private static string? LockedVersion(string text, string package)
    {
        try
        {
            foreach (var (_, framework) in JsonValues.ObjectOf(JsonValues.Get(JsonNode.Parse(text), "dependencies")))
            {
                foreach (var (id, entry) in JsonValues.ObjectOf(framework))
                {
                    if (string.Equals(id, package, StringComparison.OrdinalIgnoreCase))
                    {
                        return JsonValues.StringOf(JsonValues.Get(entry, "resolved")) ?? "";
                    }
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }
        return null;
    }

    private static string? Attribute(string attributes, string name)
    {
        var match = Regex.Match(attributes, $@"\b{name}\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)')", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static string? ChildVersion(string body)
    {
        var match = Regex.Match(body, @"<Version>\s*(?<value>[^<]*?)\s*</Version>", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["value"].Value : null;
    }

    /// <summary>Lock files first, since they pin the version, then shared MSBuild files, then projects.</summary>
    private static int? Rank(string name)
    {
        if (name is "packages.lock.json" or "paket.lock")
        {
            return 0;
        }
        if (SharedMsBuildFiles.Contains(name))
        {
            return 1;
        }
        return ProjectExtensions.Any(extension => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) ? 2 : null;
    }

    private static string Name(string relative) => relative[(relative.LastIndexOf('/') + 1)..];

    private static string? Read(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// How a coding agent or a person runs this project's deployangel: through
/// <c>dotnet</c> when the project installs it as a local tool, since it's
/// then on no one's PATH.
/// </summary>
internal static class ProjectCommand
{
    public static IReadOnlyList<string> For(string root) =>
        IsLocalTool(root) ? new[] { "dotnet", "deployangel" } : new[] { "deployangel" };

    public static string NameFor(string root) => string.Join(" ", For(root));

    private static bool IsLocalTool(string root)
    {
        foreach (var manifest in new[] { Path.Combine(root, ".config", "dotnet-tools.json"), Path.Combine(root, "dotnet-tools.json") })
        {
            try
            {
                if (!File.Exists(manifest))
                {
                    continue;
                }
                var tools = JsonValues.ObjectOf(JsonValues.Get(JsonNode.Parse(File.ReadAllText(manifest)), "tools"));
                if (tools.Any(tool => string.Equals(tool.Key, "DeployAngel.Tool", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                continue;
            }
        }
        return false;
    }
}
