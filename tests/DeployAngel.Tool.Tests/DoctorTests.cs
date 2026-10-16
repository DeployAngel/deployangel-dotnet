using System.Text.Json.Nodes;

namespace DeployAngel.Tool.Tests;

/// <summary>
/// deployangel doctor. The Python package's cases read pyproject.toml and its
/// locks; here the same cases read a .NET checkout's projects and NuGet locks.
/// </summary>
public sealed class DoctorTests : CliTestBase
{
    private const string Project = """
        <Project Sdk="Microsoft.NET.Sdk.Web">
          <PropertyGroup>
            <TargetFramework>net8.0</TargetFramework>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Hangfire.AspNetCore" Version="1.8.14" />
            <PackageReference Include="DeployAngel" Version="0.1.*" />
          </ItemGroup>
        </Project>
        """;

    private const string PackagesLock = """
        {
          "version": 1,
          "dependencies": {
            "net8.0": {
              "Hangfire.AspNetCore": {"type": "Direct", "requested": "[1.8.14, )", "resolved": "1.8.14"},
              "DeployAngel": {"type": "Direct", "requested": "[0.1.*, )", "resolved": "0.1.10"}
            }
          }
        }
        """;

    private const string Startup = "var builder = WebApplication.CreateBuilder(args);\nbuilder.Services.AddDeployAngel();\nvar app = builder.Build();\napp.Run();\n";

    public DoctorTests()
    {
        Root.Write("Shop.csproj", Project);
        Root.Write("packages.lock.json", PackagesLock);
        Root.Write("Program.cs", Startup);
    }

    private int Doctor(FakeClient? client = null, Dictionary<string, string>? env = null, params string[] argv) =>
        RunWith(client, null, env, new[] { "doctor", "--format=text" }.Concat(argv).ToArray());

    private string Output => Stdout.ToString();

    private static FakeClient ClientWith(object? document) => new() { SetupDocument = document };

    private static JsonObject SetupDocument(Action<JsonObject>? change = null)
    {
        var document = Documents.Json("""
            {"app": {"id": 1, "name": "shop", "environment": "production"},
             "token": {"scopes": ["verifications:read"], "purpose": "read"},
             "agent": {"last_received_at": "2026-10-09T11:59:00Z", "reporting": true,
                       "processes": [{"host": "web.1", "processes": 2, "release": "81ac27d"},
                                     {"host": "worker.1", "processes": 1, "release": "81ac27d"}]},
             "metadata": {"routes": 42, "job_classes": 12, "schedules": 2},
             "scheduled_jobs": [{"label": "NightlyInvoicesJob", "status": "ran"}, {"label": "SyncJob", "status": "ran"}],
             "warm_up_ends_at": null,
             "deployments": {"count": 3, "latest": {"label": "v12", "verdict": "verified"}},
             "gaps": []}
            """);
        change?.Invoke(document);
        return document;
    }

    private static JsonObject Quiet(JsonObject document)
    {
        document["agent"] = Documents.Json("""{"last_received_at": null, "reporting": false, "processes": []}""");
        document["metadata"] = null;
        return document;
    }

    [Fact]
    public void ChecksThisCheckoutAndWhatDeployangelHasSeenAndPassesWhenNothingsWrong()
    {
        Assert.Equal(0, Doctor(ClientWith(SetupDocument())));
        Assert.Equal("""
            DeployAngel doctor
              ✓ Agent: DeployAngel 0.1.10 in packages.lock.json
              · This checkout's scheduled jobs aren't checked here. Hangfire's recurring jobs and RecurringJobs are read when the app runs.
              ✓ App: shop (production)
              ✓ Agent reporting from 3 processes (web.1 ×2, worker.1), release 81ac27d
              ✓ Reported: 42 routes, 12 job classes, 2 scheduled jobs
              · 3 deploys so far; the latest, v12, is verified

            """.Replace("\r\n", "\n"), Output);
    }

    [Fact]
    public void ListsSetupGapsWithTheirFixesAndScheduledJobsNeedingAttentionWithoutFailing()
    {
        var client = ClientWith(SetupDocument(document =>
        {
            document["scheduled_jobs"] = JsonNode.Parse("""[{"label": "SyncJob", "status": "overdue"}]""");
            document["gaps"] = JsonNode.Parse("""
                [{"code": "no_job_process", "title": "No job has run since the agent started reporting", "fix": "Give that process the settings."}]
                """);
        }));

        Assert.Equal(0, Doctor(client));
        Assert.Contains("! Reported: 42 routes, 12 job classes, 2 scheduled jobs, 1 needing attention (SyncJob overdue)", Output);
        Assert.Contains("! No job has run since the agent started reporting\n      Give that process the settings.", Output);
    }

    [Fact]
    public void SaysHowProductionWillKnowTheReleaseBeforeTheAgentReportsFromTheDeployFilesHere()
    {
        Root.Write("Dockerfile", "FROM mcr.microsoft.com/dotnet/aspnet:8.0\nCMD [\"dotnet\", \"Shop.dll\"]\n");
        var client = ClientWith(SetupDocument(document =>
        {
            Quiet(document);
            document["scheduled_jobs"] = null;
            document["deployments"] = Documents.Json("""{"count": 0, "latest": null}""");
        }));

        Doctor(client);
        Assert.Contains("! The agent hasn't reported yet", Output);
        Assert.Contains("set to its app token (\"Deployed application\" in the app's Settings, under API tokens)", Output);

        // Once setup saved the app token here, the fix says how to pass it,
        // through dotnet when the command is a local tool.
        Clear();
        Credentials.Write(Root.Path, new JsonObject { ["app_token"] = "da_live_saved" });
        Root.Write(".config/dotnet-tools.json", """{"version": 1, "isRoot": true, "tools": {"deployangel.tool": {"version": "0.1.0", "commands": ["deployangel"]}}}""");
        Doctor(client);
        Assert.Contains("The app token is saved here, so pass it without printing it: $(dotnet deployangel token app)", Output);
        Assert.DoesNotContain("da_live_saved", Output);
        Assert.Contains("· The Dockerfile doesn't set DEPLOYANGEL_REVISION; unless your platform passes the commit, releases won't be named by " +
                        "commit\n      Run `deployangel install docker`", Output);

        Clear();
        Root.Write(".do/app.yaml", "envs:\n  - key: DEPLOYANGEL_REVISION\n    value: ${_self.COMMIT_HASH}\n");
        Doctor(client);
        Assert.Contains("✓ The DigitalOcean app spec passes the commit in DEPLOYANGEL_REVISION", Output);
    }

    [Fact]
    public void TrustsTheServerThatTheAgentIsReportingEvenWithNoRecentProcessBatches()
    {
        Doctor(ClientWith(SetupDocument(document =>
            document["agent"] = Documents.Json("""{"last_received_at": "2026-10-09T11:59:00Z", "reporting": true, "processes": []}"""))));
        Assert.Contains("✓ Agent reporting\n", Output);
    }

    // The gem also compares the schedules in the checkout's files with what
    // the running agent reported. A .NET app's schedules are registered in
    // code when it runs, so there's nothing to compare: doctor names where
    // they come from.
    [Fact]
    public void LeavesSchedulesToTheRunningAgentNamingTheSchedulersInTheDependencies()
    {
        Root.Write("Shop.csproj", Project.Replace("Hangfire.AspNetCore", "DeployAngel.Hangfire"));
        File.Delete(Root["packages.lock.json"]);
        Doctor();
        Assert.Contains("· This checkout's scheduled jobs aren't checked here. Hangfire's recurring jobs and RecurringJobs are read when the app runs.", Output);

        Clear();
        Root.Write("Shop.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"><ItemGroup><PackageReference Include="DeployAngel" /></ItemGroup></Project>""");
        Doctor();
        Assert.Contains("· This checkout's scheduled jobs aren't checked here. RecurringJobs is read when the app runs.", Output);
    }

    [Fact]
    public void ChecksOnlyThisCheckoutWithoutATokenAndFailsOnARejectedTokenOrAMissingAgent()
    {
        Assert.Equal(0, Doctor());
        Assert.Contains("! No token, so only this checkout was checked", Output);

        Clear();
        Assert.Equal(5, Doctor(ClientWith(new UnauthorizedException("HTTP 401"))));
        Assert.Contains("✗ DeployAngel rejected the token (HTTP 401)", Output);

        Clear();
        Root.Write("Shop.csproj", Project.Replace("""<PackageReference Include="DeployAngel" Version="0.1.*" />""", ""));
        Root.Write("packages.lock.json", """{"version": 1, "dependencies": {"net8.0": {"Hangfire.AspNetCore": {"type": "Direct", "resolved": "1.8.14"}}}}""");
        Assert.Equal(5, Doctor());
        Assert.Contains("✗ The agent isn't in the project's dependencies\n" +
                        "      Run dotnet add Shop.csproj package DeployAngel, then add builder.Services.AddDeployAngel(); to Program.cs.", Output);
    }

    [Fact]
    public void FailsWhenThePackageIsReferencedButNothingStartsTheAgent()
    {
        Root.Write("Program.cs", "var builder = WebApplication.CreateBuilder(args);\n// builder.Services.AddDeployAngel is coming\nbuilder.Build().Run();\n");
        Assert.Equal(5, Doctor());
        Assert.Contains("✓ Agent: DeployAngel 0.1.10 in packages.lock.json\n" +
                        "  ✗ The agent is installed but never started: nothing here calls AddDeployAngel()\n" +
                        "      In the app's startup code, add builder.Services.AddDeployAngel(); to Program.cs, before builder.Build().", Output);

        Clear();
        Root.Write("src/Shop/Extensions/ServiceCollectionExtensions.cs", "services.AddDeployAngel ( );\n");
        Assert.Equal(0, Doctor());
        Assert.DoesNotContain("never started", Output);
    }

    [Fact]
    public void WarnsOnAServerWithoutDoctorAndFailsWhenItCantBeReached()
    {
        Assert.Equal(0, Doctor(ClientWith(null)));
        Assert.Contains("! This DeployAngel server doesn't support doctor yet; only this checkout was checked", Output);

        Clear();
        Assert.Equal(5, Doctor(ClientWith(new ApiException("could not reach https://api.deployangel.com: HttpRequestException: timed out"))));
        Assert.Contains("✗ Couldn't reach DeployAngel: could not reach https://api.deployangel.com: HttpRequestException: timed out", Output);
    }

    [Fact]
    public void ReadsEitherTokenTheClisOrTheAgents()
    {
        Assert.Equal("da_live_agent", Token(new() { ["DEPLOYANGEL_TOKEN"] = "da_live_agent" }));
        Assert.Equal("da_cli", Token(new() { ["DEPLOYANGEL_TOKEN"] = "da_live_agent", ["DEPLOYANGEL_API_TOKEN"] = "da_cli" }));
        Assert.Null(new Cli(new[] { "doctor" }, env: new Dictionary<string, string>(), root: Root.Path).DoctorClient());

        string? Token(Dictionary<string, string> env) =>
            ((ApiClient)new Cli(new[] { "doctor" }, env: env, stdout: Stdout, stderr: Stderr, root: Root.Path).DoctorClient()!).Token;
    }

    // The Python doctor computes the code fingerprint the agent would fall
    // back to. Only the running .NET agent can compute its own, so here
    // doctor says that it would, without a value.
    [Fact]
    public void SaysWhichReleaseThisProcessWouldReportWhereTheAppRunsButNotFromACheckoutsGitHead()
    {
        var client = ClientWith(SetupDocument());

        Doctor(client, new() { ["DEPLOYANGEL_TOKEN"] = "da_live_agent", ["HEROKU_BUILD_COMMIT"] = "81ac27d0", ["HEROKU_RELEASE_VERSION"] = "v12" });
        Assert.Contains("✓ Here, the agent would report release v12 (from heroku_dyno_metadata)", Output);

        // Nothing in the environment names the release, so the agent would
        // report the commit built into the app, else the code fingerprint.
        Clear();
        Doctor(client, new() { ["DEPLOYANGEL_TOKEN"] = "da_live_agent" });
        Assert.Contains("! Here, nothing in the environment names the release: the agent would report the commit the app was built from (from assembly_version), else a code fingerprint (from code_fingerprint), with no commit\n" +
                        "      Set DEPLOYANGEL_REVISION to the deployed commit to be sure each release shows its commits and pull requests.", Output);

        Clear();
        Doctor(client, new() { ["DEPLOYANGEL_TOKEN"] = "da_live_agent", ["DEPLOYANGEL_FILE_DIGESTS"] = "false" });
        Assert.Contains("! Here, the agent can't tell which release is running", Output);

        Clear();
        Doctor(client);
        Assert.DoesNotContain("Here, the agent", Output);

        Clear();
        Root.Write(".git/HEAD", "81ac27d0f00dbadc0ffee81ac27d0f00dbadc0ff\n");
        Doctor(client, new() { ["DEPLOYANGEL_TOKEN"] = "da_live_agent" });
        Assert.DoesNotContain("Here, the agent", Output);
    }

    [Fact]
    public void WritesTheChecksAndTheServersDocumentAsJson()
    {
        RunWith(ClientWith(SetupDocument()), null, null, "doctor", "--format=json");
        var document = JsonNode.Parse(Output)!;
        Assert.Equal("""{"status":"ok","message":"Agent: DeployAngel 0.1.10 in packages.lock.json"}""", document["checks"]![0]!.ToJsonString());
        Assert.Equal("shop", (string?)document["setup"]!["app"]!["name"]);
    }

    [Theory]
    [InlineData("src/Shop/Shop.csproj", """<Project><ItemGroup><PackageReference Include="DeployAngel" Version="0.1.9" /></ItemGroup></Project>""",
        "Agent: DeployAngel 0.1.9 in src/Shop/Shop.csproj")]
    [InlineData("Shop.fsproj", "<Project><ItemGroup>\n  <PackageReference Include=\"DeployAngel\">\n    <Version>0.1.8</Version>\n  </PackageReference>\n</ItemGroup></Project>",
        "Agent: DeployAngel 0.1.8 in Shop.fsproj")]
    [InlineData("Shop.csproj", """<Project><ItemGroup><PackageReference Include="deployangel" Version="$(AgentVersion)" /></ItemGroup></Project>""",
        "Agent: DeployAngel in Shop.csproj")]
    [InlineData("src/Shop/packages.lock.json", """{"version": 1, "dependencies": {"net8.0": {"DeployAngel": {"type": "Direct", "resolved": "0.1.7"}}}}""",
        "Agent: DeployAngel 0.1.7 in src/Shop/packages.lock.json")]
    [InlineData("paket.lock", "NUGET\n  remote: https://api.nuget.org/v3/index.json\n    DeployAngel (0.1.6)\n    Hangfire.Core (1.8.14)\n",
        "Agent: DeployAngel 0.1.6 in paket.lock")]
    [InlineData("Shop.csproj", """<Project><ItemGroup><PackageReference Include="DeployAngel.Hangfire" Version="0.1.0" /></ItemGroup></Project>""",
        "✗ The agent isn't in the project's dependencies")]
    [InlineData("Shop.csproj", """<Project><ItemGroup><!-- <PackageReference Include="DeployAngel" Version="0.1.0" /> --></ItemGroup></Project>""",
        "✗ The agent isn't in the project's dependencies")]
    [InlineData("Directory.Packages.props", """<Project><ItemGroup><PackageVersion Include="DeployAngel" Version="0.1.5" /></ItemGroup></Project>""",
        "✗ The agent isn't in the project's dependencies")]
    public void FindsTheAgentInEachKindOfDependencyFile(string path, string content, string found)
    {
        File.Delete(Root["Shop.csproj"]);
        File.Delete(Root["packages.lock.json"]);
        Root.Write(path, content);
        Doctor();
        Assert.Contains(found, Output);
    }

    // With central package management, the project names the package and
    // Directory.Packages.props its version.
    [Fact]
    public void ReadsTheVersionFromCentralPackageManagement()
    {
        File.Delete(Root["packages.lock.json"]);
        Root.Write("Shop.csproj", """<Project><ItemGroup><PackageReference Include="DeployAngel" /></ItemGroup></Project>""");
        Root.Write("Directory.Packages.props", """<Project><ItemGroup><PackageVersion Include="DeployAngel" Version="0.1.5" /></ItemGroup></Project>""");
        Doctor();
        Assert.Contains("✓ Agent: DeployAngel 0.1.5 in Directory.Packages.props", Output);
    }

    [Fact]
    public void SkipsTheAgentCheckWithoutDependencyFilesAndSaysHowToAddTheAgent()
    {
        File.Delete(Root["Shop.csproj"]);
        File.Delete(Root["packages.lock.json"]);
        Assert.Equal(0, Doctor());
        Assert.DoesNotContain("Agent", Output);

        foreach (var (path, content, fix) in new[]
                 {
                     ("paket.lock", "NUGET\n    Hangfire.Core (1.8.14)\n", "Run paket add DeployAngel, then add builder.Services.AddDeployAngel(); to Program.cs."),
                     ("src/Shop/Shop.csproj", "<Project />", "Run dotnet add src/Shop/Shop.csproj package DeployAngel, then add builder.Services.AddDeployAngel(); to Program.cs."),
                     ("tests/Shop.Tests/Shop.Tests.csproj", "<Project />", "Run dotnet add package DeployAngel in the app's project, then add builder.Services.AddDeployAngel(); to Program.cs."),
                 })
        {
            Clear();
            if (Root.Exists("paket.lock"))
            {
                File.Delete(Root["paket.lock"]);
            }
            Root.Write(path, content);
            Assert.Equal(5, Doctor());
            Assert.Contains($"✗ The agent isn't in the project's dependencies\n      {fix}", Output);
        }
    }

    [Fact]
    public void ReadsKamalADockerfileThatPassesTheCommitAndHerokuFromTheDeployFiles()
    {
        var quiet = Quiet(SetupDocument());
        Root.Write("Procfile", "web: dotnet Shop.dll --urls http://*:$PORT\n");
        Doctor(ClientWith(quiet));
        Assert.Contains("· On Heroku, turn on dyno metadata so the agent knows the commit\n" +
                        "      heroku labs:enable runtime-dyno-metadata && heroku labs:enable runtime-dyno-build-metadata", Output);

        Clear();
        Root.Write("Dockerfile", "FROM mcr.microsoft.com/dotnet/aspnet:8.0\nARG GIT_SHA\nENV DEPLOYANGEL_REVISION=$GIT_SHA\n");
        Doctor(ClientWith(quiet));
        Assert.Contains("✓ The Dockerfile passes the commit in DEPLOYANGEL_REVISION", Output);

        Clear();
        Root.Write("config/deploy.yml", "service: shop\n");
        Doctor(ClientWith(quiet));
        Assert.Contains("✓ Kamal deploys: the agent reads the release from KAMAL_VERSION", Output);

        // Once processes report, what they report says it instead.
        Clear();
        Doctor(ClientWith(SetupDocument()));
        Assert.DoesNotContain("Kamal", Output);
    }

    [Fact]
    public void SaysWhenTheAgentLastReportedAndWhileItsLearningWhatsNormal()
    {
        var hosts = new JsonArray(Enumerable.Range(1, 7)
            .Select(n => (JsonNode?)new JsonObject { ["host"] = $"web.{n}", ["processes"] = 1, ["release"] = $"r{n}" }).ToArray());
        Doctor(ClientWith(SetupDocument(document =>
        {
            document["agent"] = new JsonObject { ["last_received_at"] = "2026-10-09T11:59:00Z", ["reporting"] = true, ["processes"] = hosts };
            document["warm_up_ends_at"] = "2026-10-16T09:05:00.123Z";
        })));
        Assert.Contains("✓ Agent reporting from 7 processes (web.1, web.2, web.3, web.4, web.5, and others)\n", Output);
        Assert.Contains("· Learning what's normal for this app until Oct 16, 09:05 UTC; deploys before then are checked, not cleared", Output);

        Clear();
        Doctor(ClientWith(SetupDocument(document =>
        {
            document["agent"] = Documents.Json("""{"last_received_at": "2026-10-09T11:59:00Z", "reporting": false, "processes": []}""");
            document["deployments"] = Documents.Json("""{"count": 0, "latest": null}""");
        })));
        Assert.Contains("! The agent last reported Oct 9, 11:59 UTC, and nothing since\n" +
                        "      Check the app is running, and that DEPLOYANGEL_TOKEN is still set on its processes.", Output);
        Assert.Contains("· No deploys yet: your next deploy gets the first verdict", Output);
    }
}
