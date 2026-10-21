using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DeployAngel.Tool;

/// <summary>
/// <c>deployangel install kamal|docker|agents</c>: adds DeployAngel to a
/// project's deploy hook, Dockerfile, or coding agents' settings. Never
/// overwrites what's there: it adds to a file, or says what to add.
/// </summary>
internal sealed class Installer
{
    // Registers each Kamal deploy. It runs where `kamal deploy` runs, and
    // never fails a deploy that has already shipped.
    public const string KamalHook = "#!/bin/sh\n" +
                                    "# Registers each deploy with DeployAngel, which then verifies it in\n" +
                                    "# production. Needs a \"CI deploys\" token in DEPLOYANGEL_API_TOKEN where\n" +
                                    "# you run `kamal deploy`. Added by `deployangel install kamal`.\n" +
                                    "deployangel release || true\n";

    public const string KamalHookLine = "deployangel release || true";

    // The block `deployangel install agents` keeps in AGENTS.md (and
    // CLAUDE.md when it doesn't import AGENTS.md), replaced whole on each run.
    public const string AgentsStart = "<!-- deployangel:start -->";
    public const string AgentsEnd = "<!-- deployangel:end -->";

    // After `deployangel setup`, the MCP server and CLI here use its saved token.
    public const string AgentsSavedTokenHelp =
        "Here, the MCP server and CLI use the token `deployangel setup` saved in .deployangel/.\n" +
        "Teammates redeem their own setup code, or set DEPLOYANGEL_API_TOKEN to a\n" +
        "\"CLI & coding agents\" token from the app's Settings, for example in an .envrc\n" +
        "with direnv. These files hold no token and are meant to be committed.\n" +
        "Codex reads .codex/config.toml only in projects you've marked as trusted.";

    public const string AgentsTokenHelp =
        "The MCP server and CLI need DEPLOYANGEL_API_TOKEN, a \"CLI & coding agents\" token\n" +
        "from the app's Settings, in the environment your agent runs in (for example in\n" +
        "an .envrc with direnv), or a token saved here by `deployangel setup`.\n" +
        "Never put it in these files: they're meant to be committed.\n" +
        "Codex reads .codex/config.toml only in projects you've marked as trusted.";

    // Added at the end of the Dockerfile's last stage: a value that changes
    // with every commit invalidates the cache of every layer after it, so it
    // goes after the dependency installs.
    public const string DockerfileLines =
        "# The commit this image runs, for DeployAngel. Build with --build-arg GIT_SHA=$(git rev-parse HEAD).\n" +
        "ARG GIT_SHA\n" +
        "ENV DEPLOYANGEL_REVISION=$GIT_SHA\n";

    public const string DockerBuildHelp =
        "Pass the commit when you build the image:\n" +
        "\n" +
        "  docker build --build-arg GIT_SHA=$(git rev-parse HEAD) .\n" +
        "  fly deploy --build-arg GIT_SHA=$(git rev-parse HEAD)\n" +
        "  GitHub Actions (docker/build-push-action):\n" +
        "    build-args: GIT_SHA=${{ github.sha }}\n" +
        "\n" +
        "Kamal apps don't need this: DeployAngel reads Kamal's KAMAL_VERSION.";

    private static readonly Regex ImportsAgents = new(@"^@AGENTS\.md\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex InstructionsBlock = new(Regex.Escape(AgentsStart) + ".*?" + Regex.Escape(AgentsEnd) + "\n?",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private readonly string _root;
    private readonly Action<string> _print;
    private readonly Action<string> _error;

    public Installer(string root, Action<string> print, Action<string> error)
    {
        _root = root;
        _print = print;
        _error = error;
    }

    public static string AgentInstructions(string command) =>
        $@"{AgentsStart}
## Production verification (DeployAngel)

After deploying, or pushing a commit that deploys, wait for DeployAngel's
verdict: call the `wait_for_verification` MCP tool with the commit and
`until: ""initial""`, calling again while it's in progress, or run
`{command} verify --commit=<sha> --wait --until=initial`.

- Exit 0 / verified: the release is cleared. Report the clearance line, what
  it says it doesn't cover (""Not assessed"", ""Not run since the deploy""), and
  anything DeployAngel is still watching, then move on.
- Exit 6: no problems so far, but NOT cleared. Report ""no problems so far, not
  yet cleared"" and the expected clearance time. DeployAngel keeps verifying and
  alerts on failure.
- Exit 7: warnings at the initial check. Report them and review the findings.
  The release is NOT cleared.
- Exit 2 / inconclusive: the release is NOT verified. Do not claim success.
- Exit 1 / failed: read the findings and exceptions (`get_exception`),
  investigate the likely cause, and propose a fix. Do not roll back or change
  production without explicit approval.
- Exit 3: still verifying; run the command again.
- Not cleared yet: call `get_exercise_plan` (or `{command} plan`).
  If its status is ""exercisable"" or ""waiting_for_activity"", run
  `{command} exercise --url=<production URL>`: it sends the
  plan's read-only requests and records them on the release. Offer to
  exercise what it skips: routes that change data only with a test account
  or after asking. If the status is ""warm_up"", nothing run can clear it.
- Confidence `medium` clears a release just as `high` does (high also needs 80%
  coverage and a full baseline), so don't call a medium clearance weak.
  Warnings (`suspicious: true`) aren't failures: report them as ""watching
  closely:"" and the `watching_closely` items, never as ""suspicious"".
- After deploying a fix for a failed release, read `fix_check` in the verdict
  (`--format=json` with the CLI): `fixed` means every earlier problem is
  healthy again. Otherwise report what's still happening or not confirmed yet.
- At the start of a session, before changing code: call `list_deployments`
  and `list_late_regressions` (or run `{command} status`). If a recent release
  failed, isn't cleared yet, or has a late regression, mention it before other
  work.
{AgentsEnd}
".Replace("\r\n", "\n", StringComparison.Ordinal);

    public int Run(string? target)
    {
        return target switch
        {
            "docker" => Docker(),
            "agents" => Agents(),
            "kamal" => Kamal(),
            _ => throw new UsageException("install needs a target: deployangel install kamal, docker, or agents"),
        };
    }

    /// <summary>
    /// Sets up Claude Code, Cursor, and Codex in this project: the MCP server
    /// in each one's project config, and instructions to wait for a verdict
    /// after deploying. Never overwrites what's there: an existing entry is
    /// kept, and a file it can't read is left for you to edit.
    /// </summary>
    private int Agents()
    {
        var command = ProjectCommand.For(_root).Append("mcp").ToList();
        var lines = new List<string>
        {
            JsonMcp(".mcp.json", command, "Claude Code"),
            JsonMcp(Path.Combine(".cursor", "mcp.json"), command, "Cursor"),
            CodexMcp(command),
        };
        lines.AddRange(Instructions(string.Join(" ", command.Take(command.Count - 1))));
        var help = Credentials.Find(_root) is not null ? AgentsSavedTokenHelp : AgentsTokenHelp;
        _print(string.Join("\n", lines) + "\n\n" + help);
        return 0;
    }

    private string JsonMcp(string relative, IReadOnlyList<string> command, string agent)
    {
        var path = Path.Combine(_root, relative);
        var exists = File.Exists(path);
        JsonObject? config;
        try
        {
            config = exists ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : new JsonObject();
        }
        catch (JsonException)
        {
            config = null;
        }
        var servers = config is null ? null : config.ContainsKey("mcpServers") ? config["mcpServers"] as JsonObject : new JsonObject();
        if (config is null || servers is null)
        {
            return $"Couldn't read {relative}, so it's unchanged. Add a \"deployangel\" server to its mcpServers: " +
                   $"command \"{command[0]}\", args {JsonList(command.Skip(1), ",")}.";
        }
        if (servers.ContainsKey("deployangel"))
        {
            return $"{relative} already has a deployangel MCP server ({agent}).";
        }
        config["mcpServers"] = servers;
        servers["deployangel"] = new JsonObject
        {
            ["command"] = command[0],
            ["args"] = new JsonArray(command.Skip(1).Select(arg => (JsonNode?)arg).ToArray()),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonValues.Pretty(config) + "\n");
        return $"{(exists ? "Updated" : "Created")} {relative}: the DeployAngel MCP server for {agent}.";
    }

    private string CodexMcp(IReadOnlyList<string> command)
    {
        var relative = Path.Combine(".codex", "config.toml");
        var path = Path.Combine(_root, relative);
        var existing = File.Exists(path) ? File.ReadAllText(path) : null;
        if (existing is not null && existing.Contains("[mcp_servers.deployangel]", StringComparison.Ordinal))
        {
            return $"{relative} already has a deployangel MCP server (Codex).";
        }
        var table = "[mcp_servers.deployangel]\n" +
                    $"command = {JsonString(command[0])}\n" +
                    $"args = {JsonList(command.Skip(1), ", ")}\n" +
                    "env_vars = [\"DEPLOYANGEL_API_TOKEN\", \"DEPLOYANGEL_URL\"]\n";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, existing is not null ? existing.TrimEnd('\n') + "\n\n" + table : table);
        return $"{(existing is not null ? "Updated" : "Created")} {relative}: the DeployAngel MCP server for Codex.";
    }

    /// <summary>
    /// AGENTS.md serves Codex and Cursor. Claude Code reads CLAUDE.md, so it
    /// gets the block too, unless it already imports AGENTS.md; a project
    /// without one gets a CLAUDE.md that does.
    /// </summary>
    private List<string> Instructions(string command)
    {
        var block = AgentInstructions(command);
        var lines = new List<string> { Upsert("AGENTS.md", block) };
        var claude = Path.Combine(_root, "CLAUDE.md");
        if (!File.Exists(claude))
        {
            File.WriteAllText(claude, "@AGENTS.md\n");
            lines.Add("Created CLAUDE.md, which imports AGENTS.md for Claude Code.");
        }
        else if (!ImportsAgents.IsMatch(File.ReadAllText(claude)))
        {
            lines.Add(Upsert("CLAUDE.md", block));
        }
        return lines;
    }

    private string Upsert(string relative, string block)
    {
        var path = Path.Combine(_root, relative);
        if (!File.Exists(path))
        {
            File.WriteAllText(path, block);
            return $"Created {relative} with instructions to wait for DeployAngel's verdict after deploying.";
        }
        var content = File.ReadAllText(path);
        if (InstructionsBlock.IsMatch(content))
        {
            var updated = InstructionsBlock.Replace(content, _ => block, 1);
            if (updated == content)
            {
                return $"{relative} already has DeployAngel's instructions.";
            }
            File.WriteAllText(path, updated);
            return $"Updated DeployAngel's instructions in {relative}.";
        }
        File.WriteAllText(path, content.TrimEnd('\n') + "\n\n" + block);
        return $"Added instructions to wait for DeployAngel's verdict after deploying to {relative}.";
    }

    /// <summary>
    /// Writes .kamal/hooks/post-deploy, or says what to add to a hook that
    /// already exists, rather than overwriting it.
    /// </summary>
    private int Kamal()
    {
        var relative = Path.Combine(".kamal", "hooks", "post-deploy");
        var path = Path.Combine(_root, relative);
        if (File.Exists(path))
        {
            _print(File.ReadAllText(path).Contains("deployangel release", StringComparison.Ordinal)
                ? $"{relative} already registers deploys with DeployAngel."
                : $"{relative} already exists. Add this line to it:\n\n  {KamalHookLine}");
            return 0;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, KamalHook);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        _print($"Created {relative}. Each `kamal deploy` now registers its release with DeployAngel.\n" +
               "Set DEPLOYANGEL_API_TOKEN (a \"CI deploys\" token) wherever you run kamal deploy.");
        return 0;
    }

    /// <summary>
    /// Bakes the commit into the image as DEPLOYANGEL_REVISION, from a build
    /// argument. It never edits CI workflows; it says what to pass instead.
    /// </summary>
    private int Docker()
    {
        var path = Path.Combine(_root, "Dockerfile");
        if (!File.Exists(path))
        {
            _error($"no Dockerfile in {_root}; run this where your Dockerfile is");
            return ExitCodes.Usage;
        }
        var dockerfile = File.ReadAllText(path);
        if (dockerfile.Contains("DEPLOYANGEL_REVISION", StringComparison.Ordinal))
        {
            _print("Dockerfile already sets DEPLOYANGEL_REVISION.");
            return 0;
        }
        File.WriteAllText(path, AddRevision(dockerfile));
        _print("Added DEPLOYANGEL_REVISION to the Dockerfile's last stage. " + DockerBuildHelp);
        return 0;
    }

    /// <summary>
    /// Inserts <see cref="DockerfileLines"/> in the last stage, just before
    /// the CMD and ENTRYPOINT instructions that end it, or at the end of the file.
    /// </summary>
    internal static string AddRevision(string dockerfile)
    {
        var lines = Regex.Split(dockerfile, @"(?<=\r\n|\n|\r(?!\n))").Where(line => line.Length > 0).ToList();
        // (first line, instruction) for each instruction, joining continued lines.
        var instructions = new List<(int Index, string Keyword)>();
        var continued = false;
        for (var index = 0; index < lines.Count; index++)
        {
            var stripped = lines[index].Trim();
            if (continued)
            {
                continued = stripped.EndsWith('\\');
                continue;
            }
            if (stripped.Length == 0 || stripped.StartsWith('#'))
            {
                continue;
            }
            instructions.Add((index, stripped.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0].ToUpperInvariant()));
            continued = stripped.EndsWith('\\');
        }
        int? insertAt = null;
        for (var i = instructions.Count - 1; i >= 0; i--)
        {
            if (instructions[i].Keyword is not ("CMD" or "ENTRYPOINT"))
            {
                break;
            }
            insertAt = instructions[i].Index;
        }
        // A comment just above CMD describes it, so it stays with it.
        while (insertAt is > 0 && lines[insertAt.Value - 1].Trim().StartsWith('#'))
        {
            insertAt--;
        }
        if (insertAt is null)
        {
            if (lines.Count > 0 && !lines[^1].EndsWith('\n'))
            {
                lines[^1] += "\n";
            }
            return string.Concat(lines) + (lines.Count > 0 && lines[^1].Trim().Length > 0 ? "\n" : "") + DockerfileLines;
        }
        return string.Concat(lines.Take(insertAt.Value)) + DockerfileLines + "\n" + string.Concat(lines.Skip(insertAt.Value));
    }

    private static string JsonString(string value) => JsonValues.OneLine(JsonValue.Create(value));

    private static string JsonList(IEnumerable<string> values, string separator) =>
        "[" + string.Join(separator, values.Select(JsonString)) + "]";
}
