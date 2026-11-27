using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using SmartStore.Pages;

namespace SmartStore.Tests;

public sealed class SensorTests
{
    [Theory]
    [InlineData("http://sensor.example.test/")]
    [InlineData("https://user:password@sensor.example.test/")]
    public async Task InsecureRemoteUpstream_IsRejectedBeforeAnyRequest(string address)
    {
        var handler = new SensorHandler("{}");
        var page = CreatePage(handler, address);
        var result = Assert.IsType<JsonResult>(await page.OnGetReadingsAsync(CancellationToken.None));
        Assert.Equal(0, handler.Requests);
        Assert.Contains("HTTPS", JsonSerializer.Serialize(result.Value));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("seconds")]
    [InlineData("fractional")]
    [InlineData("future")]
    public async Task InvalidObservationTime_IsNotReportedAsLive(string kind)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        double? timestamp = kind switch
        {
            "seconds" => now / 1000,
            "fractional" => now + 0.5,
            "future" => now + 60000,
            _ => null
        };
        var payload = JsonSerializer.Serialize(new
        {
            devices = new Dictionary<string, object>
            {
                ["ac233fab7f00/2"] = new { dynamb = new { temperature = 21, timestamp } }
            }
        });
        var page = CreatePage(new SensorHandler(payload));
        var result = Assert.IsType<JsonResult>(await page.OnGetReadingsAsync(CancellationToken.None));
        var readings = JsonSerializer.SerializeToElement(result.Value);
        Assert.Equal(JsonValueKind.Null, readings.GetProperty("timestamp").ValueKind);
        Assert.Equal(21, readings.GetProperty("temperature").GetDouble());
    }

    [Fact]
    public async Task ValidObservation_PreservesMeasurementsAndMotion()
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var payload = JsonSerializer.Serialize(new
        {
            devices = new Dictionary<string, object>
            {
                ["ac233fab7f00/2"] = new { dynamb = new
                {
                    temperature = 21.5, relativeHumidity = 42, illuminance = 123,
                    isMotionDetected = new[] { true }, timestamp
                } }
            }
        });
        var handler = new SensorHandler(payload);
        var result = Assert.IsType<JsonResult>(await CreatePage(handler).OnGetReadingsAsync(CancellationToken.None));
        var readings = JsonSerializer.SerializeToElement(result.Value);
        Assert.Equal(timestamp, readings.GetProperty("timestamp").GetDouble());
        Assert.Equal(21.5, readings.GetProperty("temperature").GetDouble());
        Assert.Equal(42, readings.GetProperty("humidity").GetDouble());
        Assert.Equal(123, readings.GetProperty("illuminance").GetDouble());
        Assert.True(readings.GetProperty("motion").GetBoolean());
        Assert.Equal(1, handler.Requests);
    }

    private static SensorModel CreatePage(SensorHandler handler, string address = "http://127.0.0.1:3001/")
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MSP01_DEVICE_ID"] = "ac233fab7f00", ["MSP01_DEVICE_TYPE"] = "2", ["MSP01_PARETO_URL"] = address
        }).Build();
        var page = new SensorModel(new SensorClients(handler), configuration);
        page.PageContext.HttpContext = new DefaultHttpContext();
        return page;
    }

    private sealed class SensorClients(SensorHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class SensorHandler(string payload) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) });
        }
    }
}
