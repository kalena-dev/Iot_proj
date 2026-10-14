using System.Net.Http.Json;

namespace SmartStore.Services;

/// <summary>Server-side proxy to the diagram's TypeScript MQTT/data/email service.</summary>
public sealed class IotServiceClient(HttpClient client)
{
    public Task<HttpResponseMessage> StatusAsync(CancellationToken cancellationToken) =>
        client.GetAsync("api/status", cancellationToken);

    public Task<HttpResponseMessage> ThresholdAsync(string fridge, double threshold, CancellationToken cancellationToken) =>
        client.PutAsJsonAsync($"api/refrigerators/{fridge}/threshold", new { threshold }, cancellationToken);

    public Task<HttpResponseMessage> FanAsync(bool on, CancellationToken cancellationToken) =>
        client.PostAsJsonAsync("api/fan", new { on }, cancellationToken);
}
