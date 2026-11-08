using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SmartStore.Services;

namespace SmartStore.Pages;

public sealed class CheckoutModel(StoreDemo store, ReceiptEmailService emailService, IStatusIndicator indicator, ILogger<CheckoutModel> logger) : PageModel
{
    public IReadOnlyList<DemoProduct> Products => store.Products;
    [BindProperty] public string Cart { get; set; } = "{}";
    [BindProperty] public string PaymentResult { get; set; } = "approve";
    [BindProperty] public string CustomerEmail { get; set; } = "";
    [TempData] public string? ReceiptId { get; set; }
    [TempData] public string? Message { get; set; }
    [BindProperty] public string CustomerEmail { get; set; } = "";
    [TempData] public string? ReceiptId { get; set; }
    [TempData] public string? MessageKind { get; set; }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (Cart.Length > 4000) throw new InvalidOperationException("Cart is too large.");
            var items = JsonSerializer.Deserialize<Dictionary<string, int>>(Cart)
                ?? throw new InvalidOperationException("Add an item first.");
            var sale = await store.CheckoutAsync(items, PaymentResult, CustomerEmail, cancellationToken);
            if (sale.PaymentStatus == "declined")
            {
                await indicator.ShowFailureAsync();
                Message = $"Payment declined. Attempt {sale.Id[..8]} recorded; no stock or points changed.";
                MessageKind = "error";
            }
            else
            {
                await indicator.ShowSuccessAsync();
                ReceiptId = sale.Id;
                var emailSent = await emailService.SendAsync(sale, cancellationToken);
                if (sale.EmailStatus == "pending")
                    await store.UpdateEmailStatusAsync(sale.Id, emailSent ? "sent" : "failed", cancellationToken);
                Message = $"Payment approved! Receipt {sale.Id[..8]} — ${sale.Total:F2}. Points: {sale.PointsEarned}. " +
                    (sale.CustomerEmail is null ? "Guest checkout." : emailSent ? "Receipt emailed." : "Email not sent; receipt is available below.");
                MessageKind = "success";
            }
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
