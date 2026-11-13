using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SmartStore.Services;

namespace SmartStore.Tests;

public sealed class RfidTests
{
    private readonly FakeClock clock = new();
    private RfidCheckout Create(string mode = "Reader", string key = "abcdefghijklmnopqrstuvwxyz012345") =>
        new(Microsoft.Extensions.Options.Options.Create(new RfidOptions
        {
            Mode = mode, ApiKey = key,
            EpcProducts = new() { ["AABB"] = "1001", ["CCDD"] = "1001", ["EEFF"] = "1002" }
        }), clock);

    [Fact]
    public void AuthenticationFailsClosed()
    {
        Assert.False(Create("Simulation").Authenticate("abcdefghijklmnopqrstuvwxyz012345"));
        Assert.False(Create(key: "short").Authenticate("short"));
        Assert.False(Create().Authenticate("wrong"));
        Assert.True(Create().Authenticate("abcdefghijklmnopqrstuvwxyz012345"));
    }

    [Fact]
    public void MultiplePhysicalTagsMapToQuantitiesAndRetriesAreDeduplicated()
    {
        var service = Create();
        var session = service.Open();
        Assert.True(service.Accept(new(session, ["aabb", "AABB", "CCDD", "EEFF"])));
        Assert.True(service.Accept(new(session, ["AABB"])));
        var tags = service.Read(session)!;
        Assert.Equal(3, tags.Length);
        Assert.Equal(2, tags.Count(t => t.Code == "1001"));
    }

    [Fact]
    public void InvalidAndUnmappedBatchesAreAtomic()
    {
        var service = Create();
        var session = service.Open();
        Assert.Throws<ArgumentException>(() => service.Accept(new(session, ["AABB", "FFFF"])));
        Assert.Throws<ArgumentException>(() => service.Accept(new(session, ["AABB", "not-an-epc"])));
        Assert.Throws<ArgumentException>(() => service.Accept(new(session, new string[101])));
        Assert.Empty(service.Read(session)!);
    }

    [Fact]
    public void SessionsAreIsolatedAndExpire()
    {
        var service = Create();
        var first = service.Open();
        var second = service.Open();
        service.Accept(new(first, ["AABB"]));
        Assert.Empty(service.Read(second)!);
        Assert.False(service.Accept(new("unknown", ["AABB"])));
        clock.Now = clock.Now.AddMinutes(31);
        Assert.Null(service.Read(first));
        Assert.False(service.Accept(new(first, ["CCDD"])));
    }

    [Fact]
    public void SimulationNeverAcceptsReaderEvents()
    {
        var service = Create("Simulation");
        Assert.True(service.IsSimulation);
        Assert.Throws<ArgumentException>(() => service.Accept(new(service.Open(), ["AABB"])));
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
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
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
