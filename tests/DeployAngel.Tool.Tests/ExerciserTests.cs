namespace DeployAngel.Tool.Tests;

public sealed class ExerciserTests
{
    private static (string Path, List<string> Missing) Fill(string path, Dictionary<string, string> parameters) =>
        new Exerciser("https://shop.example.com", parameters: parameters).Fill(path);

    [Fact]
    public void FillsEachFrameworksPathParametersByName()
    {
        Assert.Equal("/apps/3/deployments/212", Fill("/apps/:app_id/deployments/:id", new() { ["app_id"] = "3", ["id"] = "212" }).Path);
        Assert.Equal("/items/9", Fill("/items/{item_id}", new() { ["item_id"] = "9" }).Path);
        Assert.Equal("/orders/4/lines/2", Fill("/orders/{id:int}/lines/{line?}", new() { ["id"] = "4", ["line"] = "2" }).Path);
        Assert.Equal("/orders/5/", Fill("/orders/<int:pk>/", new() { ["pk"] = "5" }).Path);
    }

    [Fact]
    public void NamesTheParametersItCouldntFillOnceEach()
    {
        var (path, missing) = Fill("/apps/:app_id/deployments/:id", new() { ["id"] = "212" });
        Assert.Equal("/apps/:app_id/deployments/212", path);
        Assert.Equal(new List<string> { "app_id" }, missing);
        Assert.Equal(new List<string> { "id" }, Fill("/a/:id/b/:id", new()).Missing);
    }

    [Fact]
    public void EscapesValuesKeepingSlashesOnlyInParametersThatSpanSegments()
    {
        Assert.Equal("/users/a%20b%2Fc", Fill("/users/:id", new() { ["id"] = "a b/c" }).Path);
        Assert.Equal("/pages/about/team", Fill("/pages/*id", new() { ["id"] = "about/team" }).Path);
        Assert.Equal("/files/a/b", Fill("/files/{*path}", new() { ["path"] = "a/b" }).Path);
        Assert.Equal("/files/a/b", Fill("/files/{path:path}", new() { ["path"] = "a/b" }).Path);
        Assert.Equal("/docs/x/y", Fill("/docs/<path:rest>", new() { ["rest"] = "x/y" }).Path);
    }

    [Fact]
    public void ReadsParamsAndHeadersAndNeverPutsAHeadersValueInAnError()
    {
        Assert.Equal(new Dictionary<string, string> { ["id"] = "42", ["slug"] = "a=b" }, Exerciser.ParseParameters(new[] { "id=42", "slug=a=b" }));
        Assert.Throws<UsageException>(() => Exerciser.ParseParameters(new[] { "id=" }));
        Assert.Equal(new Dictionary<string, string> { ["Authorization"] = "Bearer x", ["Cookie"] = "a=b" },
            Exerciser.ParseHeaders(new[] { "Authorization:  Bearer x ", "", "Cookie: a=b" }));
        var error = Assert.Throws<UsageException>(() => Exerciser.ParseHeaders(new[] { "Cookie s3cret" }));
        Assert.DoesNotContain("s3cret", error.Message);
        Assert.Throws<UsageException>(() => Exerciser.ParseHeaders(new[] { "X: a\r\nHost: evil" }));
    }
}
