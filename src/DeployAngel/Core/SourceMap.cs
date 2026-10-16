using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeployAngel.Core;

/// <summary>Digests of the app's source files, keyed by repository-relative path.</summary>
internal sealed class FileManifest
{
    public static readonly FileManifest Empty = new(null, 0, false, new SortedDictionary<string, string>(StringComparer.Ordinal));

    public FileManifest(string? hash, int count, bool truncated, SortedDictionary<string, string> files)
    {
        Hash = hash;
        Count = count;
        Truncated = truncated;
        Files = files;
    }

    public string? Hash { get; }
    public int Count { get; }
    public bool Truncated { get; }
    public SortedDictionary<string, string> Files { get; }

    public static FileManifest From(SortedDictionary<string, string> files, bool truncated)
    {
        if (files.Count == 0) return new FileManifest(null, 0, truncated, files);
        var joined = string.Join("\n", files.Select(pair => $"{pair.Key}:{pair.Value}"));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined))).ToLowerInvariant();
        return new FileManifest(hash, files.Count, truncated, files);
    }
}

/// <summary>
/// What the app's own assemblies and their debug symbols say about its
/// source. A published .NET app has no source files, but its portable PDBs
/// (which `dotnet publish` deploys by default) name every file each assembly
/// was compiled from, with a SHA-256 of its contents, and which file each
/// method is in. That gives the agent what the Python agent reads from the
/// files themselves: repository-relative paths for exception frames, the
/// files behind each route and job, and the file digests. Paths and hashes
/// only; source code never leaves the application.
///
/// Paths in a PDB are where the build ran ("/src/Shop/Controllers/..." in a
/// Docker build, "/_/src/..." in a deterministic CI build), so they're made
/// relative to the repository root the build recorded (the
/// DeployAngel.SourceRoot assembly metadata the package's build targets add).
/// </summary>
internal sealed class SourceMap
{
    public const string SourceRootMetadata = "DeployAngel.SourceRoot";
    public const int MaxFiles = 20_000;
    public const int DigestLength = 16;
    // The prefix a deterministic build (ContinuousIntegrationBuild with
    // Source Link) maps the repository root to.
    private const string DeterministicRoot = "/_/";
    private static readonly string[] SkippedDirectories = { "obj", "bin" };

    private readonly HashSet<string> _appAssemblies;
    private readonly Func<string, string?> _assemblyPath;
    private readonly Dictionary<string, MetadataReaderProvider?> _pdbs = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly StringComparison _pathComparison;
    private FileManifest? _manifest;

    public SourceMap(IEnumerable<string> appAssemblies, string? sourceRoot, Func<string, string?>? assemblyPath = null,
        bool ignoreCase = false)
    {
        _appAssemblies = new HashSet<string>(appAssemblies, StringComparer.OrdinalIgnoreCase);
        _assemblyPath = assemblyPath ?? DefaultAssemblyPath;
        _pathComparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        SourceRoot = NormalizeRoot(sourceRoot);
    }

    /// <summary>The build's repository root, with forward slashes and a trailing slash.</summary>
    public string? SourceRoot { get; }

    public IReadOnlyCollection<string> AppAssemblies => _appAssemblies;

    /// <summary>
    /// The app's own assemblies are the entry assembly and the projects its
    /// deps.json lists (as opposed to NuGet packages and the framework).
    /// </summary>
    public static SourceMap ForProcess(string? configuredSourceRoot)
    {
        var entry = Assembly.GetEntryAssembly();
        var names = new HashSet<string>(ProjectAssemblies(), StringComparer.OrdinalIgnoreCase);
        if (entry?.GetName().Name is { } entryName) names.Add(entryName);
        var root = configuredSourceRoot;
        if (string.IsNullOrWhiteSpace(root)) root = RecordedSourceRoot(entry);
        if (string.IsNullOrWhiteSpace(root))
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name is not { } name || !names.Contains(name)) continue;
                root = RecordedSourceRoot(assembly);
                if (!string.IsNullOrWhiteSpace(root)) break;
            }
        }
        return new SourceMap(names, root, ignoreCase: OperatingSystem.IsWindows());
    }

    public static string? RecordedSourceRoot(Assembly? assembly)
    {
        try
        {
            return assembly?.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == SourceRootMetadata)?.Value;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public bool IsAppAssembly(Assembly? assembly)
    {
        if (assembly is null) return false;
        try
        {
            return assembly.GetName().Name is { } name && _appAssemblies.Contains(name);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// A build path as a repository-relative path with forward slashes, or
    /// null for one outside the repository or in its build output (obj/,
    /// where generated files go).
    /// </summary>
    public string? RelativePath(string? buildPath)
    {
        if (string.IsNullOrEmpty(buildPath)) return null;
        var path = buildPath.Replace('\\', '/');
        string? relative = null;
        if (SourceRoot is not null && path.StartsWith(SourceRoot, _pathComparison))
            relative = path[SourceRoot.Length..];
        else if (path.StartsWith(DeterministicRoot, StringComparison.Ordinal))
            relative = path[DeterministicRoot.Length..];
        if (string.IsNullOrEmpty(relative)) return null;
        var segments = relative.Split('/');
        if (segments.Any(s => s.Length == 0 || s == "..")) return null;
        if (segments.Take(segments.Length - 1).Any(s => SkippedDirectories.Contains(s, StringComparer.OrdinalIgnoreCase)))
            return null;
        return relative;
    }

    /// <summary>
    /// The file a method is written in. An async or iterator method's code is
    /// in its state machine's MoveNext. Razor maps generated code back to the
    /// .cshtml or .razor file, so the first mapped line wins.
    /// </summary>
    public string? FileFor(MethodBase? method)
    {
        if (method is null) return null;
        try
        {
            var target = method;
            var stateMachine = method.GetCustomAttribute<StateMachineAttribute>();
            var moveNext = stateMachine?.StateMachineType.GetMethod("MoveNext",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (moveNext is not null) target = moveNext;
            lock (_lock)
            {
                var reader = Reader(target.Module.Assembly);
                if (reader is null) return null;
                var definition = MetadataTokens.MethodDefinitionHandle(target.MetadataToken & 0x00FFFFFF);
                var info = reader.GetMethodDebugInformation(definition.ToDebugInformationHandle());
                if (!info.Document.IsNil)
                {
                    var single = RelativePath(reader.GetString(reader.GetDocument(info.Document).Name));
                    if (single is not null && !IsGenerated(single)) return single;
                }
                foreach (var point in info.GetSequencePoints())
                {
                    if (point.IsHidden || point.Document.IsNil) continue;
                    var path = RelativePath(reader.GetString(reader.GetDocument(point.Document).Name));
                    if (path is not null && !IsGenerated(path)) return path;
                }
                return null;
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Every source file the app's assemblies were compiled from, with the
    /// first 16 hex characters of the SHA-256 the compiler recorded.
    /// </summary>
    public FileManifest Manifest()
    {
        lock (_lock)
        {
            if (_manifest is not null) return _manifest;
            try
            {
                var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
                var truncated = false;
                foreach (var name in _appAssemblies.OrderBy(n => n, StringComparer.Ordinal))
                {
                    var reader = Reader(name);
                    if (reader is null) continue;
                    foreach (var handle in reader.Documents)
                    {
                        var document = reader.GetDocument(handle);
                        var path = RelativePath(reader.GetString(document.Name));
                        if (path is null || document.Hash.IsNil || files.ContainsKey(path)) continue;
                        var hash = reader.GetBlobBytes(document.Hash);
                        if (hash.Length == 0) continue;
                        if (files.Count >= MaxFiles)
                        {
                            truncated = true;
                            break;
                        }
                        var digest = Convert.ToHexString(hash).ToLowerInvariant();
                        files[path] = digest.Length > DigestLength ? digest[..DigestLength] : digest;
                    }
                }
                _manifest = FileManifest.From(files, truncated);
            }
            catch (Exception)
            {
                _manifest = FileManifest.Empty;
            }
            return _manifest;
        }
    }

    /// <summary>Frees the debug symbols once the metadata no longer needs them.</summary>
    public void Close()
    {
        lock (_lock)
        {
            foreach (var provider in _pdbs.Values) provider?.Dispose();
            _pdbs.Clear();
        }
    }

    private static bool IsGenerated(string path) => path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase);

    private MetadataReader? Reader(Assembly assembly)
    {
        var name = assembly.GetName().Name;
        if (name is null || !_appAssemblies.Contains(name)) return null;
        return Reader(name, string.IsNullOrEmpty(assembly.Location) ? null : assembly.Location);
    }

    private MetadataReader? Reader(string name, string? location = null)
    {
        if (!_pdbs.TryGetValue(name, out var provider))
        {
            provider = OpenPdb(location ?? _assemblyPath(name));
            _pdbs[name] = provider;
        }
        return provider?.GetMetadataReader();
    }

    /// <summary>
    /// An embedded PDB, else the .pdb beside the assembly. Read into memory,
    /// so no file stays open.
    /// </summary>
    private static MetadataReaderProvider? OpenPdb(string? assemblyPath)
    {
        if (assemblyPath is null || !File.Exists(assemblyPath)) return null;
        try
        {
            using (var stream = File.OpenRead(assemblyPath))
            using (var pe = new PEReader(stream))
            {
                foreach (var entry in pe.ReadDebugDirectory())
                {
                    if (entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb)
                        return pe.ReadEmbeddedPortablePdbDebugDirectoryData(entry);
                }
            }
            var pdb = Path.ChangeExtension(assemblyPath, ".pdb");
            if (!File.Exists(pdb)) return null;
            return MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(pdb), MetadataStreamOptions.PrefetchMetadata);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? DefaultAssemblyPath(string name)
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name);
        if (loaded is not null && !string.IsNullOrEmpty(loaded.Location)) return loaded.Location;
        var path = Path.Combine(AppContext.BaseDirectory, name + ".dll");
        return File.Exists(path) ? path : null;
    }

    private static string? NormalizeRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        var normalized = root.Trim().Replace('\\', '/');
        return normalized.EndsWith('/') ? normalized : normalized + "/";
    }

    /// <summary>The "project" libraries in the app's deps.json.</summary>
    private static IEnumerable<string> ProjectAssemblies()
    {
        var names = new List<string>();
        try
        {
            var files = AppContext.GetData("APP_CONTEXT_DEPS_FILES") as string;
            foreach (var file in (files ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!File.Exists(file)) continue;
                using var stream = File.OpenRead(file);
                using var document = JsonDocument.Parse(stream);
                if (!document.RootElement.TryGetProperty("libraries", out var libraries) ||
                    libraries.ValueKind != JsonValueKind.Object) continue;
                foreach (var library in libraries.EnumerateObject())
                {
                    if (library.Value.ValueKind == JsonValueKind.Object &&
                        library.Value.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String &&
                        type.GetString() == "project")
                        names.Add(library.Name.Split('/')[0]);
                }
            }
        }
        catch (Exception)
        {
            // Without deps.json, only the entry assembly is the app's.
        }
        return names;
    }
}
