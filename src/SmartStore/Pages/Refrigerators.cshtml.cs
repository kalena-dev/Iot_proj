using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SmartStore.Services;

namespace SmartStore.Pages;

public sealed class RefrigeratorsModel(IotServiceClient iot, ILogger<RefrigeratorsModel> logger) : PageModel
{
    public void OnGet() { }

    public Task<IActionResult> OnGetStatusAsync(CancellationToken cancellationToken) =>
        ProxyAsync(() => iot.StatusAsync(cancellationToken), cancellationToken);

    public Task<IActionResult> OnPostThresholdAsync(string fridge, double? threshold, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid || fridge is not ("Frig1" or "Frig2") || threshold is null
            || !double.IsFinite(threshold.Value) || threshold is < 0 or > 50)
            return Task.FromResult<IActionResult>(new JsonResult(new { error = "Choose a refrigerator and a threshold from 0 to 50 °C." }) { StatusCode = 400 });
        return ProxyAsync(() => iot.ThresholdAsync(fridge, threshold.Value, cancellationToken), cancellationToken);
    }

    public Task<IActionResult> OnPostFanAsync(bool? on, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid || on is null)
            return Task.FromResult<IActionResult>(new JsonResult(new { error = "Choose ON or OFF." }) { StatusCode = 400 });
        return ProxyAsync(() => iot.FanAsync(on.Value, cancellationToken), cancellationToken);
    }

    private async Task<IActionResult> ProxyAsync(Func<Task<HttpResponseMessage>> request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            using var response = await request();
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            // Only forward JSON; never leak upstream HTML, stack traces or credentials.
            using var json = JsonDocument.Parse(body);
            return new ContentResult { Content = json.RootElement.GetRawText(), ContentType = "application/json", StatusCode = (int)response.StatusCode };
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning("Phase 2 IoT service is unavailable ({ErrorType}).", exception.GetType().Name);
            return new JsonResult(new { error = "IoT service unavailable. Start iot-server and check its configuration." }) { StatusCode = 503 };
        }
    }
}
