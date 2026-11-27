using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SmartStore.Services;
using SmartStore.Data;

namespace SmartStore.Pages;

public sealed class CheckoutModel(StoreDemo store, ReceiptEmailService emailService, IStatusIndicator indicator, ILogger<CheckoutModel> logger, RfidCheckout rfid, IProductRepository products, IConfiguration configuration) : PageModel
{
    public string? AccountEmail => StoreAccounts.EmailOf(User);
    public bool CanAssignCodes => StoreAccounts.IsStaff(User, configuration);
    public bool SimulationMode => rfid.IsSimulation;
    public string RfidSession { get; private set; } = "";

    public async Task<IActionResult> OnGetCodeAsync(string? code, CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        if (string.IsNullOrWhiteSpace(code) || code.Length > 512) return BadRequest();
        var resolved = await products.ResolveCodeAsync(code, token);
        return resolved is null ? NotFound() : new JsonResult(new { code = resolved.Value.Product.Upc, isTag = resolved.Value.IsTag });
    }

    public async Task<IActionResult> OnPostAssignCodeAsync(string? code, string? upc, CancellationToken token)
    {
        if (!CanAssignCodes) return StatusCode(StatusCodes.Status403Forbidden);
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(upc)) return BadRequest();
        try { await products.AssignCodeAsync(code, upc, token); }
        catch (DuplicateProductCodeException) { return StatusCode(StatusCodes.Status409Conflict); }
        catch (ArgumentException) { return BadRequest(); }
        catch (ProductNotFoundException) { return NotFound(); }
        return await OnGetCodeAsync(code, token);
    }

    public IActionResult OnGetRfid()
    {
        Response.Headers.CacheControl = "no-store";
        var tags = rfid.Read(Request.Headers["X-Rfid-Session"].ToString());
        return tags is null ? NotFound() : new JsonResult(tags);
    }

    public IReadOnlyList<DemoProduct> Products { get; private set; } = [];
    public IReadOnlyList<LoyaltyCoupon> Coupons { get; private set; } = [];
    public string? DatabaseWarning { get; private set; }
    public string? CouponWarning { get; private set; }
    [BindProperty] public string Cart { get; set; } = "{}";
    [BindProperty] public string? CouponId { get; set; }
    [BindProperty] public string PaymentResult { get; set; } = "approve";
    [TempData] public string? ReceiptId { get; set; }
    [TempData] public string? Message { get; set; }
    [TempData] public string? MessageKind { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        try { Products = await store.GetProductsAsync(cancellationToken); }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Cannot load checkout inventory.");
            DatabaseWarning = "Inventory unavailable. Check the MongoDB connection before checking out.";
        }
        if (AccountEmail is { } email)
        {
            try { Coupons = await store.GetCouponsAsync(email, cancellationToken); }
            catch (Exception exception) when (exception is MongoDB.Driver.MongoException or TimeoutException)
            {
                logger.LogWarning(exception, "Cannot load checkout coupons.");
                CouponWarning = "Coupons are temporarily unavailable.";
            }
        }
        RfidSession = rfid.PairLocal(HttpContext);
    }

    public async Task<IActionResult> OnGetReceiptAsync(string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 40) return BadRequest();
        var sale = await store.FindSaleAsync(id, cancellationToken);
        if (sale is null || sale.PaymentStatus != "approved" || AccountEmail is not { } email
            || !string.Equals(sale.CustomerEmail, email, StringComparison.OrdinalIgnoreCase)) return NotFound();
        return new JsonResult(new { receipt = emailService.FormatReceipt(sale) });
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (AccountEmail is not { } email) return Challenge();
        DemoSale sale;
        try
        {
            if (string.IsNullOrWhiteSpace(Cart)) throw new InvalidOperationException("Add an item first.");
            // Up to 100 codes; an RFID tag can be 128 hex characters.
            if (Cart.Length > 16000) throw new InvalidOperationException("Cart is too large.");
            var items = JsonSerializer.Deserialize<Dictionary<string, int>>(Cart)
                ?? throw new InvalidOperationException("Add an item first.");
            sale = await store.CheckoutAsync(items, PaymentResult, email, cancellationToken, emailService.Sender, CouponId);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            await ShowResultAsync(success: false);
            Message = exception is JsonException ? "Invalid cart. Please add items again." : exception.Message;
            MessageKind = "error";
            return RedirectToPage();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Checkout could not be saved.");
            await ShowResultAsync(success: false);
            Message = "Checkout could not be saved. Check the database connection.";
            MessageKind = "error";
            return RedirectToPage();
        }

        if (sale.PaymentStatus == "declined")
        {
            await ShowResultAsync(success: false);
            Message = $"Payment declined. Attempt {sale.Id[..8]} recorded; no stock or points changed.";
            MessageKind = "error";
            return RedirectToPage();
        }

        // From here the purchase is committed. Notification failures must not invite a second purchase.
        ReceiptId = sale.Id;
        MessageKind = "success";
        await ShowResultAsync(success: true);
        var emailSent = false;
        if (sale.EmailStatus == "pending")
        {
            using var sendTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { emailSent = await emailService.SendAsync(sale, sendTimeout.Token); }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Receipt email failed for sale {SaleId}.", sale.Id);
            }
            try
            {
                await store.UpdateEmailStatusAsync(sale.Id, emailSent ? "sent" : "failed", CancellationToken.None);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Cannot save receipt email status for sale {SaleId}.", sale.Id);
            }
        }
        Message = $"Payment approved! Receipt {sale.Id[..8]} — ${sale.Total:F2}. " +
            $"Earned {sale.PointsEarned} points. Balance: {sale.PointsBalanceAfter}. " +
            (emailSent ? "Receipt emailed." : "Email not sent. View your receipt below.");
        return RedirectToPage();
    }

    private async Task ShowResultAsync(bool success)
    {
        try
        {
            if (success) await indicator.ShowSuccessAsync();
            else await indicator.ShowFailureAsync();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Checkout status notification failed.");
        }
    }
}
