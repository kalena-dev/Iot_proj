using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SmartStore.Pages;

public sealed class SensorModel(IHttpClientFactory clients, IConfiguration configuration) : PageModel
{
    public async Task<IActionResult> OnGetReadingsAsync(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var deviceId = configuration["MSP01_DEVICE_ID"]?.Replace(":", "").Replace("-", "").Trim().ToLowerInvariant();
        var deviceType = configuration["MSP01_DEVICE_TYPE"] ?? "2";
        if (deviceId is null || deviceId.Length != 12 || !deviceId.All(Uri.IsHexDigit)
            || deviceType is not ("2" or "3"))
            return new JsonResult(new { status = "Configure the Minew MSP01 device ID to receive readings." });

        if (!Uri.TryCreate(configuration["MSP01_PARETO_URL"] ?? "http://127.0.0.1:3001/", UriKind.Absolute, out var baseUrl)
            || baseUrl.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(baseUrl.UserInfo)
            || baseUrl.Scheme == "http" && !baseUrl.IsLoopback)
            return new JsonResult(new { status = "Use HTTPS for a remote Pareto Anywhere service." });

        var key = $"{deviceId}/{deviceType}";
        try
        {
            using var client = clients.CreateClient("Msp01");
            using var response = await client.GetAsync(new Uri(baseUrl, $"/context/device/{key}"), cancellationToken);
            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                response.EnsureSuccessStatusCode();
                using var context = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);

                if (TryProperty(context.RootElement, "devices", out var devices)
                    && TryProperty(devices, key, out var device)
                    && TryProperty(device, "dynamb", out var readings))
                {
                    return new JsonResult(new
                    {
                        temperature = Number(readings, "temperature"),
                        humidity = Number(readings, "relativeHumidity"),
                        illuminance = Number(readings, "illuminance"),
                        luminousFlux = Number(readings, "luminousFlux"),
                        motion = Motion(readings),
                        timestamp = ObservationTime(readings)
                    });
                }
            }

            return new JsonResult(new { status = "Waiting for Minew MSP01 readings." });
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested
            && exception is HttpRequestException or OperationCanceledException or JsonException)
        {
            return new JsonResult(new { status = "Pareto Anywhere is unavailable." }) { StatusCode = 503 };
        }
    }

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value);
    }

    private static double? Number(JsonElement readings, string name) =>
        TryProperty(readings, name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;

    private static double? ObservationTime(JsonElement readings)
    {
        var timestamp = Number(readings, "timestamp");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // Pareto timestamps are Unix milliseconds. Seconds and clock errors are not live observations.
        return timestamp is >= 946684800000 && timestamp <= now + 5000
            && Math.Truncate(timestamp.Value) == timestamp ? timestamp : null;
    }

    private static bool? Motion(JsonElement readings)
    {
        if (!TryProperty(readings, "isMotionDetected", out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0)
            value = value[0];

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }
}
