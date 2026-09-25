using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SmartStore.Services;

namespace SmartStore.Pages;

public sealed class CheckoutModel(StoreDemo store, IStatusIndicator indicator, ILogger<CheckoutModel> logger) : PageModel
{
    public IReadOnlyList<DemoProduct> Products => store.Products;
    [BindProperty] public string Cart { get; set; } = "{}";
    [BindProperty] public string PaymentResult { get; set; } = "approve";
    [TempData] public string? Message { get; set; }
    [TempData] public string? MessageKind { get; set; }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (Cart.Length > 4000) throw new InvalidOperationException("Cart is too large.");
            var items = JsonSerializer.Deserialize<Dictionary<string, int>>(Cart)
                ?? throw new InvalidOperationException("Add an item first.");
            if (PaymentResult == "decline") throw new InvalidOperationException("Demo payment declined. No sale was saved.");
            if (PaymentResult != "approve") throw new InvalidOperationException("Choose a demo payment result.");

            var sale = await store.CheckoutAsync(items, cancellationToken);
            await indicator.ShowSuccessAsync();
            Message = $"Demo payment approved! Sale {sale.Id[..8]} saved — total ${sale.Total:F2}.";
            MessageKind = "success";
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            await indicator.ShowFailureAsync();
            Message = exception is JsonException ? "Invalid cart. Please add items again." : exception.Message;
            MessageKind = "error";
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Demo checkout could not be saved.");
            await indicator.ShowFailureAsync();
            Message = "Checkout could not be saved. Check the database connection.";
            MessageKind = "error";
        }
        return RedirectToPage();
    }
}
