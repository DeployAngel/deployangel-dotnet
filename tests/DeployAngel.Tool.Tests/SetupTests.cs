using System.Text.Json.Nodes;

namespace DeployAngel.Tool.Tests;

/// <summary>Answers a setup code like the server: the app and its two tokens, once.</summary>
internal sealed class Redeemer : FakeClient
{
    public List<string> Codes { get; } = new();

    public override JsonObject RedeemSetupCode(string code)
    {
        Codes.Add(code);
        if (code != "da_setup_good")
        {
            throw new NotFoundException("That setup code is invalid, already used, or expired.");
        }
        return Documents.Json("""
            {"app": {"id": 7, "name": "shop", "environment": "production"},
             "app_token": "da_live_apptoken", "api_token": "da_live_apitoken"}
            """);
    }
}

/// <summary>
/// deployangel setup and token, ported from the Python package's tests:
/// redeem a one-time code, save the tokens privately, and never print them.
/// </summary>
public sealed class SetupTests : IDisposable
{
    private readonly TempDirectory _root = new();
    private readonly StringWriter _stdout = new();
    private readonly StringWriter _stderr = new();

    private Cli CommandIn(string root, FakeClient? client = null, Dictionary<string, string>? env = null, params string[] argv) =>
        new(argv, env: env ?? new Dictionary<string, string>(), stdout: _stdout, stderr: _stderr, client: client, root: root);

    private Cli Command(params string[] argv) => CommandIn(_root.Path, null, null, argv);

    public void Dispose() => _root.Dispose();

    [Fact]
    public void RedeemsACodeAndSavesBothTokensPrivatelyWithoutPrintingEither()
    {
        Assert.Equal(0, CommandIn(_root.Path, new Redeemer(), null, "setup", "--code=da_setup_good").Run());

        var file = _root[".deployangel/credentials.json"];
        var data = JsonNode.Parse(File.ReadAllText(file))!;
        Assert.Equal("da_live_apptoken", (string?)data["app_token"]);
        Assert.Equal("da_live_apitoken", (string?)data["api_token"]);
        Assert.Equal("shop", (string?)data["app"]!["name"]);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        }
        Assert.Equal("*\n", _root.Read(".deployangel/.gitignore"));
        var output = _stdout.ToString();
        Assert.Contains("Set up DeployAngel for shop (production)", output);
        Assert.Contains("not shown here", output);
        Assert.Contains("reads its token only from DEPLOYANGEL_TOKEN", output);
        Assert.Contains("setup-for-agents.md", output);
        Assert.DoesNotContain("fly secrets", output);
        Assert.DoesNotContain("heroku config:set", output);
        Assert.DoesNotContain("da_live_", output + _stderr);
    }

    [Fact]
    public void UsesTheSavedTokenUnlessTheEnvironmentNamesOne()
    {
        CommandIn(_root.Path, new Redeemer(), null, "setup", "--code=da_setup_good").Run();

        Assert.Equal("da_live_apitoken", ((ApiClient)Command().Client()).Token);
        Assert.Equal("da_live_env", ((ApiClient)CommandIn(_root.Path, null, new() { ["DEPLOYANGEL_API_TOKEN"] = "da_live_env" }).Client()).Token);
        var nested = _root["Controllers/Admin"];
        Directory.CreateDirectory(nested);
        Assert.Equal("da_live_apitoken", (string?)Credentials.Read(nested)["api_token"]);
    }

    [Fact]
    public void PrintsASavedTokenWithNothingAroundItForPiping()
    {
        CommandIn(_root.Path, new Redeemer(), null, "setup", "--code=da_setup_good").Run();
        _stdout.GetStringBuilder().Clear();
        Assert.Equal(0, Command("token", "app").Run());
        Assert.Equal("da_live_apptoken", _stdout.ToString());
    }

    [Fact]
    public void SaysWhatsWrongWithABadCodeAMissingCodeOrNothingSaved()
    {
        Assert.Equal(4, CommandIn(_root.Path, new Redeemer(), null, "setup", "--code=da_setup_used").Run());
        Assert.Contains("invalid, already used, or expired", _stderr.ToString());
        Assert.False(_root.Exists(".deployangel"));

        Assert.Equal(5, CommandIn(_root.Path, new Redeemer(), null, "setup").Run());
        _stderr.GetStringBuilder().Clear();
        Assert.Equal(5, Command("token", "app").Run());
        Assert.Contains("run deployangel setup --code=<code> first", _stderr.ToString());
        Assert.Equal(5, Command("token", "nope").Run());
    }
}
