using System.Runtime.CompilerServices;
using DeployAngel.Core;

namespace DeployAngel.Tests;

public class FingerprintTests
{
    private const string Root = "/build/shop";
    private static readonly SourceMap Map = Maps.ForTests(Root);

    private static readonly Frame[] Frames =
    {
        new("/_/src/Microsoft.EntityFrameworkCore/DbContext.cs", "Microsoft.EntityFrameworkCore.DbContext", "SaveChanges", Maps.Framework),
        new("/build/shop/src/Shop/Services/OrderService.cs", "Shop.Services.OrderService", "Create", Maps.App),
        new("/build/shop/src/Shop/Controllers/OrdersController.cs", "Shop.Controllers.OrdersController", "Post", Maps.App),
    };

    [Fact]
    public void UsesTheFirstApplicationFrameWithoutLineNumbers()
    {
        Assert.Equal(("src/Shop/Services/OrderService.cs#Create", true), Fingerprint.TopFrame(Frames, Map));
    }

    [Fact]
    public void IsStableWhenPackagesUpgradeOrMessagesChange()
    {
        var upgraded = Frames.Select((f, i) => i == 0 ? f with { File = "/_/src/EFCore/v9/DbContext.cs" } : f).ToArray();
        Assert.Equal(Fingerprint.Details("Shop.OutOfStock", Frames, Map, "Item 4 is out").Fingerprint,
            Fingerprint.Details("Shop.OutOfStock", upgraded, Map, "Item 9 is out").Fingerprint);
    }

    [Fact]
    public void DiffersByExceptionClassAndApplicationFrame()
    {
        var a = Fingerprint.Details("Shop.OutOfStock", Frames, Map).Fingerprint;
        Assert.NotEqual(a, Fingerprint.Details("Shop.PaymentFailed", Frames, Map).Fingerprint);
        Assert.NotEqual(a, Fingerprint.Details("Shop.OutOfStock", Frames.Skip(2).ToArray(), Map).Fingerprint);
        Assert.Matches("^[0-9a-f]{32}$", a);
    }

    [Fact]
    public void FallsBackToAVersionFreeFrameworkFrame()
    {
        Assert.Equal(("Microsoft.EntityFrameworkCore.DbContext#SaveChanges", false), Fingerprint.TopFrame(Frames.Take(1).ToArray(), Map));
        Assert.Equal(("unknown", false), Fingerprint.TopFrame(Array.Empty<Frame>(), Map));
    }

    [Fact]
    public void NamesAnAppFrameByItsTypeWhenItsFileIsUnknown()
    {
        var frames = new[]
        {
            new Frame(null, "Shop.Services.OrderService", "Create", Maps.App),
            new Frame("/build/shop-old/src/Shop/Orders.cs", "Shop.Orders", "Place", Maps.App),
        };
        Assert.Equal("Shop.Services.OrderService#Create", Fingerprint.Name(frames[0], Map));
        // A sibling directory isn't the repository.
        Assert.Equal("Shop.Orders#Place", Fingerprint.Name(frames[1], Map));
    }

    [Fact]
    public void MakesDeterministicBuildPathsRelativeAndSkipsGeneratedFiles()
    {
        Assert.Equal("src/Shop/Program.cs", Map.RelativePath("/_/src/Shop/Program.cs"));
        Assert.Equal("src/Shop/Program.cs", Maps.ForTests(@"C:\build\shop\").RelativePath(@"C:\build\shop\src\Shop\Program.cs"));
        Assert.Null(Map.RelativePath("/build/shop/src/Shop/obj/Release/net8.0/Shop.GlobalUsings.g.cs"));
        Assert.Null(Map.RelativePath("/build/shop/../etc/passwd"));
        Assert.Null(Map.RelativePath("/build/shop/"));
        Assert.Null(Map.RelativePath("Microsoft.Extensions.Logging.Generators/LoggerMessage.g.cs"));
    }

    [Fact]
    public void NamesExceptionsByNamespaceWithoutGenericArgumentsOrVersions()
    {
        Assert.Equal("System.InvalidOperationException", Fingerprint.ExceptionClass(new InvalidOperationException()));
        Assert.Equal("DeployAngel.Tests.FingerprintTests+Failure`1", Fingerprint.TypeName(typeof(Failure<string>)));
        Assert.Equal("System.Collections.Generic.List`1", Fingerprint.TypeName(typeof(List<int>)));
    }

    [Fact]
    public void UnwrapsReflectionAndSingleTaskFailures()
    {
        var cause = new TimeoutException();
        Assert.Same(cause, Fingerprint.Unwrap(new System.Reflection.TargetInvocationException(new AggregateException(cause))));
        var two = new AggregateException(cause, new TimeoutException());
        Assert.Same(two, Fingerprint.Unwrap(two));
    }

    [Fact]
    public async Task ReadsARealAsyncStackInnermostFirstAsTheMethodsAreWritten()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => PlaceOrderAsync());
        var map = Maps.ForTests();
        var details = Fingerprint.ForException(exception, map);
        Assert.Equal("tests/DeployAngel.Tests/FingerprintTests.cs#ChargeAsync", details.TopFrame);
        Assert.True(details.AppFrame);
        Assert.Equal("Card <n> declined for <string>", details.Message);
        var backtrace = Fingerprint.Backtrace(exception, map);
        Assert.Equal(new[] { "tests/DeployAngel.Tests/FingerprintTests.cs#ChargeAsync", "tests/DeployAngel.Tests/FingerprintTests.cs#PlaceOrderAsync" },
            backtrace.Take(2));
    }

    [Fact]
    public void NamesLambdasAndLocalFunctionsByTheMethodHoldingThem()
    {
        var exception = Assert.Throws<ArgumentException>(() => Checkout(new[] { 1, -1 }));
        var frames = Fingerprint.Frames(exception);
        Assert.Equal("Checkout", frames[0].Method);
        Assert.Equal("DeployAngel.Tests.FingerprintTests", frames[0].Type);
    }

    [Fact]
    public void LeavesTheMessageOutWhenAsked()
    {
        Assert.Null(Fingerprint.ForException(new InvalidOperationException("secret"), Map, message: false).Message);
    }

    [Fact]
    public void MessagesLoseIdsEmailsAndQuotedValues()
    {
        Assert.Equal("User <n> (<email>) not found: <string> <hex>",
            Fingerprint.NormalizeMessage("User 42 (pat@example.com) not found: 'abc' 0x7f3a"));
        Assert.Equal("Order <uuid> failed", Fingerprint.NormalizeMessage("Order 0f8fad5b-d9cb-469f-a165-70867728950e failed\nat line 2"));
    }

    [Fact]
    public void MessagesKeepUnquotedWords()
    {
        Assert.Equal("Payment of <n> failed for Jane Doe", Fingerprint.NormalizeMessage("Payment of 1250.00 failed for Jane Doe"));
    }

    [Fact]
    public void MessagesReplaceRedactionsAsWholeWordsFirst()
    {
        var redactions = new[] { ("acme.lendwell.com", "<host>"), ("acme", "<tenant>") };
        Assert.Equal("Blocked host: <host> for <tenant>_x, not acmeco",
            Fingerprint.NormalizeMessage("Blocked host: ACME.lendwell.com for acme_x, not acmeco", redactions));
    }

    [Fact]
    public void RedactionsTakeTheRequestHostWithoutItsPort()
    {
        Redaction.SetRequestHost("acme.example.com:8443");
        Assert.Equal(new[] { ("acme.example.com", "<host>") }, Redaction.Current());
        Redaction.SetRequestHost("ab");
        Assert.Empty(Redaction.Current());
        Redaction.SetRequestHost(null);
    }

    [Fact]
    public void BacktracesKeepOnlyApplicationFrames()
    {
        Assert.Equal(new[] { "src/Shop/Services/OrderService.cs#Create", "src/Shop/Controllers/OrdersController.cs#Post" },
            Fingerprint.AppBacktrace(Frames, Map));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task PlaceOrderAsync()
    {
        await Task.Yield();
        await ChargeAsync("tok_visa");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task ChargeAsync(string token)
    {
        await Task.Yield();
        throw new InvalidOperationException($"Card 4242 declined for '{token}'");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Checkout(int[] quantities)
    {
        Array.ForEach(quantities, quantity =>
        {
            if (quantity < 0) throw new ArgumentException("negative");
        });
    }

    private sealed class Failure<T> : Exception;
}
