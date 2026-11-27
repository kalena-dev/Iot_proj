using Microsoft.Extensions.DependencyInjection;
using SmartStore.Services;

namespace SmartStore.Tests;

public sealed class RfidTests
{
    private readonly FakeClock clock = new();
    private RfidCheckout Create(string mode = "Reader", string key = "abcdefghijklmnopqrstuvwxyz012345") =>
        new(Microsoft.Extensions.Options.Options.Create(new RfidOptions { Mode = mode, ApiKey = key }), clock);

    private static Microsoft.AspNetCore.Http.DefaultHttpContext Context(char owner = 'A')
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        context.Request.Headers.Cookie = "__Host-Northstar.RfidOwner=" + new string(owner, 64);
        return context;
    }

    private static string Paired(RfidCheckout service, Microsoft.AspNetCore.Http.HttpContext context,
        bool receiving = false)
    {
        var token = service.Open(context, receiving);
        Assert.True(service.PairReader(context, token, receiving));
        return token;
    }

    private static Task<RfidInventory> Resolve(string epc) => Task.FromResult(epc switch
    {
        "AABB" or "CCDD" => Inventory(epc, "1001"),
        "EEFF" => Inventory(epc, "1002"),
        _ => new RfidInventory(null, null, false)
    });

    private static RfidInventory Inventory(string epc, string code, string status = "in_stock",
        string? soldBy = null, bool eligible = true) =>
        new(new SmartStore.Models.Item { Epc = epc, ProductId = "product", Status = status, SoldBy = soldBy },
            code, eligible);

    [Fact]
    public void AuthenticationFailsClosed()
    {
        Assert.False(Create("Simulation").Authenticate("abcdefghijklmnopqrstuvwxyz012345"));
        Assert.False(Create(key: "short").Authenticate("short"));
        Assert.False(Create().Authenticate("wrong"));
        Assert.True(Create().Authenticate("abcdefghijklmnopqrstuvwxyz012345"));
    }

    [Fact]
    public async Task OpeningNeverPairsAndAnotherTabCannotRedirectActiveReader()
    {
        var service = Create();
        var browser = Context();
        var first = service.Open(browser);
        Assert.False(await service.AcceptAsync(new(first, ["AABB"]), Resolve));
        Assert.True(service.PairReader(browser, first));
        var otherTab = service.Open(Context());
        Assert.NotEqual(first, otherTab);
        Assert.False(service.PairReader(Context(), otherTab));
        Assert.False(service.PairReader(Context('B'), first));
        Assert.Null(service.Read(Context('B'), first));
        Assert.False(service.Close(Context('B'), first));
        Assert.True(await service.AcceptAsync(new(first, ["AABB"]), Resolve));
        Assert.Single(service.Read(browser, first)!);
        Assert.Empty(service.Read(Context(), otherTab)!);
        Assert.True(service.Close(browser, first));
        Assert.True(service.PairReader(Context(), otherTab));
    }

    [Fact]
    public void ReloadsReuseOrReclaimCapacityAndWorkflowMustMatch()
    {
        var service = Create();
        var browser = Context();
        var token = service.Open(browser);
        for (var i = 0; i < 150; i++)
            Assert.Equal(token, service.Open(Context(), existingToken: token));
        for (var i = 0; i < 150; i++)
            token = service.Open(Context());
        Assert.True(service.PairReader(browser, token));
        Assert.Null(service.Read(browser, token, receiving: true));
        Assert.False(service.Close(browser, token, receiving: true));
        var receiving = service.Open(browser, receiving: true);
        Assert.False(service.PairReader(browser, receiving, receiving: true));
        Assert.True(service.Close(browser, token));
        Assert.True(service.PairReader(browser, receiving, receiving: true));
    }

    [Fact]
    public void ExpiryReclaimsGlobalCapacityForOtherOwners()
    {
        var service = Create();
        Microsoft.AspNetCore.Http.DefaultHttpContext Browser(int id)
        {
            var context = Context();
            context.Request.Headers.Cookie = "__Host-Northstar.RfidOwner=" + id.ToString("X64");
            return context;
        }
        for (var i = 0; i < 100; i++) service.Open(Browser(i));
        Assert.Throws<InvalidOperationException>(() => service.Open(Browser(100)));
        clock.Now = clock.Now.AddMinutes(31);
        Assert.NotEmpty(service.Open(Browser(100)));
    }

    [Fact]
    public void OwnerCookieIsSecureAndAccountChangesDoNotInheritSession()
    {
        var service = Create();
        var browser = Context();
        browser.Request.Headers.Remove("Cookie");
        service.Open(browser);
        var cookie = browser.Response.Headers.SetCookie.ToString();
        Assert.Contains("__Host-Northstar.RfidOwner=", cookie);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        var token = service.Open(Context());
        var signedIn = Context();
        signedIn.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "1234567890abcdef1234567890abcdef")], "test"));
        Assert.Null(service.Read(signedIn, token));
        var insecure = Context();
        insecure.Request.Scheme = "http";
        Assert.False(service.PairReader(insecure, token));
        var remote = Context();
        remote.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.0.2.10");
        Assert.True(service.PairReader(remote, token));
        Assert.True(service.Close(remote, token));
    }

    [Fact]
    public async Task PhysicalTagsHaveReaderProvenanceAndAreDeduplicatedByEpc()
    {
        var service = Create();
        var browser = Context();
        var token = Paired(service, browser);
        Assert.True(await service.AcceptAsync(new(token, ["aabb", "AABB", "CCDD", "EEFF"]), Resolve));
        for (var i = 0; i < 150; i++)
            Assert.True(await service.AcceptAsync(new(token, ["AABB"], Guid.NewGuid()), Resolve));
        var tags = service.Read(browser, token)!;
        Assert.Equal(3, tags.Length);
        Assert.Equal(2, tags.Count(t => t.Code == "1001"));
        Assert.All(tags, tag => Assert.Equal("reader", tag.Source));
        Assert.True(await service.AcceptAsync(new(token, ["FFFF"], Source: "manual"),
            _ => Task.FromResult(Inventory("FFFF", "1002"))));
        Assert.Equal("manual", service.Read(browser, token)!.Single(t => t.Epc == "FFFF").Source);
    }

    [Fact]
    public async Task RegisteredEligibleItemsRequiredAndRejectedBatchesAreAtomic()
    {
        var service = Create();
        var browser = Context();
        var token = Paired(service, browser);
        foreach (var invalid in new[] { "FFFF", "not-an-epc", "ABC", new string('A', 26) })
            await Assert.ThrowsAsync<ArgumentException>(() => service.AcceptAsync(new(token, ["AABB", invalid]), Resolve));
        foreach (var mapping in new[]
        {
            new RfidInventory(null, "1001", true),
            Inventory("CCDD", "1001"),
            Inventory("AABB", "1001", "sold", "scan"),
            Inventory("AABB", "1001", "sold", null),
            Inventory("AABB", "1001", "sold", "guess", eligible: false)
        })
            await Assert.ThrowsAsync<ArgumentException>(() => service.AcceptAsync(new(token, ["AABB"]),
                _ => Task.FromResult(mapping)));
        Assert.Empty(service.Read(browser, token)!);
        Assert.True(await service.AcceptAsync(new(token, ["AABB"]),
            _ => Task.FromResult(Inventory("AABB", "1001", "sold", "guess"))));
    }

    [Fact]
    public async Task InvalidBatchSizeSourceAndScanIdsCannotMutateSession()
    {
        var service = Create();
        var browser = Context();
        var token = Paired(service, browser);
        foreach (var batch in new RfidBatch[]
        {
            new(token, []),
            new(token, Enumerable.Repeat("AABB", 101).ToArray()),
            new(token, ["AABB", "CCDD"], Guid.NewGuid()),
            new(token, ["AABB"], Source: "authenticated-tag")
        })
            await Assert.ThrowsAsync<ArgumentException>(() => service.AcceptAsync(batch, Resolve));
        Assert.Empty(service.Read(browser, token)!);
    }

    [Fact]
    public async Task ReceivingAllowsNewTagsButNeverConfirmedSoldItems()
    {
        var service = Create();
        var browser = Context();
        var token = Paired(service, browser, receiving: true);
        Assert.True(await service.AcceptAsync(new(token, [new string('A', 24)]), Resolve));
        await Assert.ThrowsAsync<ArgumentException>(() => service.AcceptAsync(new(token, ["AABB"]),
            _ => Task.FromResult(Inventory("AABB", "1001", "sold", "scan"))));
        Assert.Equal("", Assert.Single(service.Read(browser, token, receiving: true)!).Code);
    }

    [Fact]
    public async Task SessionsExpireAndClosingDuringResolutionCannotAcceptLateEvents()
    {
        var service = Create();
        var browser = Context();
        var token = Paired(service, browser);
        var pending = new TaskCompletionSource<RfidInventory>();
        var accepting = service.AcceptAsync(new(token, ["AABB"]), _ => pending.Task);
        Assert.True(service.Close(browser, token));
        pending.SetResult(Inventory("AABB", "1001"));
        Assert.False(await accepting);
        token = Paired(service, browser);
        clock.Now = clock.Now.AddMinutes(31);
        Assert.Null(service.Read(browser, token));
        Assert.False(await service.AcceptAsync(new(token, ["AABB"]), Resolve));
        Assert.True(service.PairReader(Context('B'), service.Open(Context('B'))));
    }

    [Fact]
    public async Task SimulationNeverAcceptsReaderEvents()
    {
        var service = Create("Simulation");
        Assert.Equal("", service.Open(Context()));
        await Assert.ThrowsAsync<ArgumentException>(() => service.AcceptAsync(new("manual", ["AABB"]), Resolve));
    }

    [Theory]
    [InlineData("wrong", "{}", 401)]
    [InlineData("abcdefghijklmnopqrstuvwxyz012345", "not json", 400)]
    [InlineData("abcdefghijklmnopqrstuvwxyz012345", "{\"session\":\"missing\",\"epcs\":[\"AABB\"]}", 404)]
    public async Task HttpEndpointRejectsUnauthorizedMalformedAndUnpairedEvents(string key, string body, int status)
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        builder.Services.AddSingleton(Create());
        await using var app = builder.Build();
        app.MapRfid();
        var routes = (Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)app;
        var endpoint = routes.DataSources.SelectMany(source => source.Endpoints).Single();
        var context = Context();
        context.RequestServices = app.Services;
        context.Request.Method = "POST";
        context.Request.Headers["X-Rfid-Key"] = key;
        context.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(context);
        Assert.Equal(status, context.Response.StatusCode);
    }

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
