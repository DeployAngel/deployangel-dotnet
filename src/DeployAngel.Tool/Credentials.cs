using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeployAngel.Tool;

/// <summary>
/// Tokens <c>deployangel setup</c> saved for a project, so they never have to
/// be printed, pasted into a coding agent's chat, or kept in the environment:
/// .deployangel/credentials.json, readable only by its owner. The directory
/// carries its own .gitignore, so it stays out of git without touching the
/// project's. Same file and layout as the Ruby gem's and the Python package's,
/// so a project can switch between them.
/// </summary>
internal static class Credentials
{
    public const string Directory = ".deployangel";
    public const string FileName = "credentials.json";

    public static string PathIn(string root) => System.IO.Path.Combine(root, Directory, FileName);

    /// <summary>From root or a parent directory, as git finds .git.</summary>
    public static string? Find(string root)
    {
        var directory = System.IO.Path.GetFullPath(root);
        while (true)
        {
            var candidate = PathIn(directory);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            var parent = System.IO.Path.GetDirectoryName(directory);
            if (parent is null || parent == directory)
            {
                return null;
            }
            directory = parent;
        }
    }

    public static JsonObject Read(string root)
    {
        var file = Find(root);
        if (file is null)
        {
            return new JsonObject();
        }
        try
        {
            return JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? new JsonObject();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new JsonObject();
        }
    }

    public static string Write(string root, JsonObject data)
    {
        var directory = System.IO.Path.Combine(root, Directory);
        System.IO.Directory.CreateDirectory(directory);
        File.WriteAllText(System.IO.Path.Combine(directory, ".gitignore"), "*\n");
        var file = PathIn(root);
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            // Created private, so the tokens are never readable by others,
            // even for a moment.
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        using (var stream = new FileStream(file, options))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(JsonValues.Pretty(data));
        }
        if (!OperatingSystem.IsWindows())
        {
            // UnixCreateMode applies only to a new file; an old one keeps its mode.
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        return file;
    }
}
