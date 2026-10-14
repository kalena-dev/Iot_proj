using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using SmartStore.Pages;
using SmartStore.Services;

namespace SmartStore.Tests;

public sealed class RefrigeratorsTests
{
    [Fact]
    public async Task Status_ProxiesJsonWithoutRequiringTheCustomerDatabase()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"brokerConnected\":true}");
        var page = CreatePage(handler);
        var result = Assert.IsType<ContentResult>(await page.OnGetStatusAsync(CancellationToken.None));
        Assert.Equal(200, result.StatusCode);
        Assert.Contains("true", result.Content);
        Assert.Equal("/api/status", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Theory]
    [InlineData("other", 8)]
    [InlineData("Frig1", -1)]
    [InlineData("Frig2", 51)]
    public async Task InvalidThreshold_SkipsTheService(string fridge, double threshold)
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");
        var result = Assert.IsType<JsonResult>(await CreatePage(handler).OnPostThresholdAsync(fridge, threshold, CancellationToken.None));
        Assert.Equal(400, result.StatusCode);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task Threshold_UsesTheCorrectFridgeAndHttpMethod()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"message\":\"Saved\"}");
        await CreatePage(handler).OnPostThresholdAsync("Frig2", 24, CancellationToken.None);
        Assert.Equal(HttpMethod.Put, handler.LastRequest!.Method);
        Assert.Equal("/api/refrigerators/Frig2/threshold", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Contains("24", handler.Body);
    }

    [Fact]
    public async Task MissingFanChoice_IsRejectedRatherThanTurningMotorOff()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");
        var result = Assert.IsType<JsonResult>(await CreatePage(handler).OnPostFanAsync(null, CancellationToken.None));
        Assert.Equal(400, result.StatusCode);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task FanCommand_ProxiesTheRequestedBoolean()
    {
        var handler = new StubHandler(HttpStatusCode.Conflict, "{\"error\":\"Controller offline\"}");
        var result = Assert.IsType<ContentResult>(await CreatePage(handler).OnPostFanAsync(true, CancellationToken.None));
        Assert.Equal(409, result.StatusCode);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("/api/fan", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Contains("true", handler.Body);
    }

    [Fact]
    public async Task NonJsonServiceError_ProducesSafeUnavailableResponse()
    {
        var handler = new StubHandler(HttpStatusCode.InternalServerError, "private stack trace");
        var result = Assert.IsType<JsonResult>(await CreatePage(handler).OnGetStatusAsync(CancellationToken.None));
        Assert.Equal(503, result.StatusCode);
        Assert.DoesNotContain("private", System.Text.Json.JsonSerializer.Serialize(result.Value));
    }

    private static RefrigeratorsModel CreatePage(StubHandler handler)
    {
        var client = new IotServiceClient(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:3100/") });
        var page = new RefrigeratorsModel(client, NullLogger<RefrigeratorsModel>.Instance);
        page.PageContext.HttpContext = new DefaultHttpContext();
        return page;
    }

    private sealed class StubHandler(HttpStatusCode status, string response) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(response) };
        }
    }
}
