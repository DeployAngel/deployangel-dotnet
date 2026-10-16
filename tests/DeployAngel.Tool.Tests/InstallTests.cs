using System.Text.Json.Nodes;

namespace DeployAngel.Tool.Tests;

/// <summary>deployangel install kamal, docker, and agents.</summary>
public sealed class InstallTests : CliTestBase
{
    private const string RevisionLines = "# The commit this image runs, for DeployAngel. Build with --build-arg GIT_SHA=$(git rev-parse HEAD).\n" +
                                         "ARG GIT_SHA\nENV DEPLOYANGEL_REVISION=$GIT_SHA\n";

    private int Install(string target) => new Cli(new[] { "install", target }, env: new Dictionary<string, string>(), stdout: Stdout,
        stderr: Stderr, root: Root.Path).Run();

    private string InstallAgents()
    {
        Assert.Equal(0, Install("agents"));
        return Stdout.ToString();
    }

    [Fact]
    public void InstallKamalWritesAnExecutableHook()
    {
        Assert.Equal(0, Install("kamal"));
        var hook = Root[".kamal/hooks/post-deploy"];
        Assert.Contains("deployangel release || true", File.ReadAllText(hook));
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(hook).HasFlag(UnixFileMode.UserExecute));
        }
        Assert.Contains("Created .kamal/hooks/post-deploy", Stdout.ToString());
        Assert.Contains("DEPLOYANGEL_API_TOKEN", Stdout.ToString());
    }

    [Fact]
    public void InstallKamalLeavesAnExistingHookAlone()
    {
        Root.Write(".kamal/hooks/post-deploy", "#!/bin/sh\necho deployed\n");
        Assert.Equal(0, Install("kamal"));
        Assert.Equal("#!/bin/sh\necho deployed\n", Root.Read(".kamal/hooks/post-deploy"));
        Assert.Contains("already exists. Add this line to it:", Stdout.ToString());
    }

    [Fact]
    public void InstallDockerAddsTheRevisionBeforeTheLastStagesCmd()
    {
        Root.Write("Dockerfile", "FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build\nRUN dotnet publish -c Release -o /app\nCMD [\"build\"]\n\n" +
                                 "FROM mcr.microsoft.com/dotnet/aspnet:8.0\nCOPY --from=build /app /app\nEXPOSE 8080\n\n" +
                                 "# Start the server\nENTRYPOINT [\"tini\", \"--\"]\nCMD [\"dotnet\", \\\n  \"Shop.dll\"]\n");
        Assert.Equal(0, Install("docker"));
        Assert.Equal(
            "FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build\nRUN dotnet publish -c Release -o /app\nCMD [\"build\"]\n\n" +
            "FROM mcr.microsoft.com/dotnet/aspnet:8.0\nCOPY --from=build /app /app\nEXPOSE 8080\n\n" +
            RevisionLines + "\n# Start the server\nENTRYPOINT [\"tini\", \"--\"]\nCMD [\"dotnet\", \\\n  \"Shop.dll\"]\n",
            Root.Read("Dockerfile"));
        foreach (var text in new[]
                 {
                     "docker build --build-arg GIT_SHA=$(git rev-parse HEAD) .", "fly deploy --build-arg GIT_SHA=$(git rev-parse HEAD)",
                     "build-args: GIT_SHA=${{ github.sha }}", "Kamal apps don't need this",
                 })
        {
            Assert.Contains(text, Stdout.ToString());
        }
    }

    [Fact]
    public void InstallDockerAppendsWhenTheLastStageHasNoCmd()
    {
        Root.Write("Dockerfile", "FROM mcr.microsoft.com/dotnet/sdk:8.0\nCMD [\"early\"]\nFROM mcr.microsoft.com/dotnet/aspnet:8.0\nRUN apt-get update");
        Assert.Equal(0, Install("docker"));
        Assert.Equal("FROM mcr.microsoft.com/dotnet/sdk:8.0\nCMD [\"early\"]\nFROM mcr.microsoft.com/dotnet/aspnet:8.0\nRUN apt-get update\n\n" + RevisionLines,
            Root.Read("Dockerfile"));
    }

    [Fact]
    public void InstallDockerIsIdempotent()
    {
        Root.Write("Dockerfile", "FROM mcr.microsoft.com/dotnet/aspnet:8.0\nCMD [\"dotnet\", \"Shop.dll\"]\n");
        Assert.Equal(0, Install("docker"));
        var installed = Root.Read("Dockerfile");
        Assert.Equal(0, Install("docker"));
        Assert.Equal(installed, Root.Read("Dockerfile"));
        Assert.Equal(1, installed.Split("DEPLOYANGEL_REVISION").Length - 1);
        Assert.Contains("Dockerfile already sets DEPLOYANGEL_REVISION.", Stdout.ToString());
    }

    [Fact]
    public void InstallDockerNeedsADockerfile()
    {
        Assert.Equal(5, Install("docker"));
        Assert.Contains($"deployangel: no Dockerfile in {Root.Path}", Stderr.ToString());
        Assert.False(Root.Exists("Dockerfile"));
    }

    [Fact]
    public void InstallNeedsAKnownTarget()
    {
        Assert.Equal(5, Install("heroku"));
        Assert.Contains("deployangel install kamal, docker, or agents", Stderr.ToString());
    }

    [Fact]
    public void InstallAgentsSetsUpClaudeCodeCursorAndCodexInABareProject()
    {
        var output = InstallAgents();
        const string servers = """{"mcpServers":{"deployangel":{"command":"deployangel","args":["mcp"]}}}""";
        Assert.Equal(servers, JsonNode.Parse(Root.Read(".mcp.json"))!.ToJsonString());
        Assert.Equal(servers, JsonNode.Parse(Root.Read(".cursor/mcp.json"))!.ToJsonString());
        Assert.Equal("[mcp_servers.deployangel]\ncommand = \"deployangel\"\nargs = [\"mcp\"]\n" +
                     "env_vars = [\"DEPLOYANGEL_API_TOKEN\", \"DEPLOYANGEL_URL\"]\n", Root.Read(".codex/config.toml"));
        Assert.Equal(Installer.AgentInstructions("deployangel"), Root.Read("AGENTS.md"));
        Assert.Contains("`deployangel verify --commit=<sha> --wait --until=initial`", Root.Read("AGENTS.md"));
        Assert.Equal("@AGENTS.md\n", Root.Read("CLAUDE.md"));
        foreach (var text in new[]
                 {
                     "Created .mcp.json", "Created .cursor/mcp.json", "Created .codex/config.toml", "Created AGENTS.md",
                     "Created CLAUDE.md", "DEPLOYANGEL_API_TOKEN", "Never put it in these files",
                 })
        {
            Assert.Contains(text, output);
        }
    }

    [Fact]
    public void InstallAgentsWritesJsonIndentedTwoSpaces()
    {
        InstallAgents();
        Assert.Equal("{\n  \"mcpServers\": {\n    \"deployangel\": {\n      \"command\": \"deployangel\",\n      \"args\": [\n        \"mcp\"\n      ]\n    }\n  }\n}\n",
            Root.Read(".mcp.json").Replace("\r\n", "\n"));
    }

    [Fact]
    public void InstallAgentsSaysTheSavedTokenIsUsedOnceSetupHasRun()
    {
        Credentials.Write(Root.Path, new JsonObject { ["api_token"] = "da_live_saved" });
        var output = InstallAgents();
        Assert.Contains("use the token `deployangel setup` saved", output);
        Assert.Contains("Teammates redeem their own setup code", output);
        Assert.DoesNotContain("need DEPLOYANGEL_API_TOKEN", output);
        Assert.DoesNotContain("da_live_saved", output);
    }

    // The Python command runs through uv or Poetry; a .NET project that
    // installs the command as a local tool runs it through dotnet.
    [Fact]
    public void InstallAgentsRunsDeployangelThroughDotnetWhenItsALocalTool()
    {
        Root.Write(".config/dotnet-tools.json",
            """{"version": 1, "isRoot": true, "tools": {"deployangel.tool": {"version": "0.1.0", "commands": ["deployangel"]}}}""");
        InstallAgents();
        Assert.Equal("""{"command":"dotnet","args":["deployangel","mcp"]}""",
            JsonNode.Parse(Root.Read(".mcp.json"))!["mcpServers"]!["deployangel"]!.ToJsonString());
        Assert.Contains("args = [\"deployangel\", \"mcp\"]", Root.Read(".codex/config.toml"));
        Assert.Contains("`dotnet deployangel verify --commit=<sha>", Root.Read("AGENTS.md"));

        using var other = new TempDirectory();
        other.Write(".config/dotnet-tools.json", """{"version": 1, "isRoot": true, "tools": {"dotnet-ef": {"version": "8.0.0", "commands": ["dotnet-ef"]}}}""");
        new Cli(new[] { "install", "agents" }, env: new Dictionary<string, string>(), stdout: Stdout, stderr: Stderr, root: other.Path).Run();
        Assert.Equal("deployangel", (string?)JsonNode.Parse(other.Read(".mcp.json"))!["mcpServers"]!["deployangel"]!["command"]);
    }

    [Fact]
    public void InstallAgentsAddsToExistingFilesAndChangesNothingWhenRunAgain()
    {
        Root.Write(".mcp.json", """{"mcpServers": {"github": {"command": "gh-mcp"}}}""");
        Root.Write(".codex/config.toml", "model = \"gpt-5\"\n");
        Root.Write("AGENTS.md", "# Project\n\nRun the tests first.\n");
        Root.Write("CLAUDE.md", "# Claude\n");

        InstallAgents();
        var block = Installer.AgentInstructions("deployangel");
        Assert.Equal(new[] { "github", "deployangel" },
            ((JsonObject)JsonNode.Parse(Root.Read(".mcp.json"))!["mcpServers"]!).Select(pair => pair.Key));
        Assert.StartsWith("model = \"gpt-5\"\n\n[mcp_servers.deployangel]\n", Root.Read(".codex/config.toml"));
        Assert.Equal("# Project\n\nRun the tests first.\n\n" + block, Root.Read("AGENTS.md"));
        Assert.Equal("# Claude\n\n" + block, Root.Read("CLAUDE.md"));

        var paths = new[] { ".mcp.json", ".cursor/mcp.json", ".codex/config.toml", "AGENTS.md", "CLAUDE.md" };
        var files = paths.ToDictionary(path => path, Root.Read);
        Clear();
        var output = InstallAgents();
        Assert.Equal(files, paths.ToDictionary(path => path, Root.Read));
        Assert.Contains("already has a deployangel MCP server", output);
        Assert.Contains("already has DeployAngel's instructions", output);
    }

    [Fact]
    public void InstallAgentsReplacesItsOldInstructionsAndLeavesAnImportingClaudeMdAlone()
    {
        Root.Write("AGENTS.md", $"# Project\n\n{Installer.AgentsStart}\nold advice\n{Installer.AgentsEnd}\n\n## Style\n");
        Root.Write("CLAUDE.md", "@AGENTS.md\n");
        var output = InstallAgents();
        Assert.Equal("# Project\n\n" + Installer.AgentInstructions("deployangel") + "\n## Style\n", Root.Read("AGENTS.md"));
        Assert.Equal("@AGENTS.md\n", Root.Read("CLAUDE.md"));
        Assert.Contains("Updated DeployAngel's instructions in AGENTS.md.", output);
    }

    [Fact]
    public void InstallAgentsLeavesAConfigItCantRead()
    {
        Root.Write(".mcp.json", "{ not json");
        var output = InstallAgents();
        Assert.Equal("{ not json", Root.Read(".mcp.json"));
        Assert.Contains("Couldn't read .mcp.json, so it's unchanged.", output);
        Assert.Contains("command \"deployangel\", args [\"mcp\"]", output);
    }
}
