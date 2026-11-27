using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SmartStore.Services;
using SmartStore.Data;

namespace SmartStore.Pages;

public sealed class CheckoutModel(StoreDemo store, ReceiptEmailService emailService, IStatusIndicator indicator, ILogger<CheckoutModel> logger, RfidCheckout rfid, IProductRepository products, IConfiguration configuration) : PageModel
{
    public string? AccountEmail => StoreAccounts.EmailOf(User);
    public string? AccountId => StoreAccounts.AccountIdOf(User);
    public bool CanAssignCodes => StoreAccounts.IsStaff(User, configuration);
    public bool SimulationMode => rfid.IsSimulation;
    public string RfidSession { get; private set; } = "";

    public async Task<IActionResult> OnGetCodeAsync(string? code, CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        if (string.IsNullOrWhiteSpace(code) || code.Length > 512) return BadRequest();
        var resolved = await products.ResolveCodeAsync(code, token);
        if (resolved is null) return NotFound();
        if (resolved.Value.IsTag && !await products.CanCheckoutItemAsync(await products.FindItemAsync(code, token), token))
            return StatusCode(StatusCodes.Status409Conflict, new { error = "This tag is not available for checkout." });
        return new JsonResult(new { code = resolved.Value.Product.Upc, isTag = resolved.Value.IsTag, source = "manual" });
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
        var tags = rfid.Read(HttpContext, Request.Headers["X-Rfid-Session"].ToString());
        return tags is null ? NotFound() : new JsonResult(tags);
    }

    public IActionResult OnPostPairRfid()
    {
        Response.Headers.CacheControl = "no-store";
        return rfid.PairReader(HttpContext, Request.Headers["X-Rfid-Session"].ToString())
            ? new JsonResult(new { paired = true })
            : StatusCode(StatusCodes.Status409Conflict, new { error = "The reader is already paired to another session. Release it there first." });
    }

    public IActionResult OnPostCloseRfid()
    {
        return rfid.Close(HttpContext, Request.Headers["X-Rfid-Session"].ToString())
            ? new JsonResult(new { closed = true }) : NotFound();
    }

    public IReadOnlyList<DemoProduct> Products { get; private set; } = [];
    public IReadOnlyList<LoyaltyCoupon> Coupons { get; private set; } = [];
    public string? DatabaseWarning { get; private set; }
    public string? CouponWarning { get; private set; }
    [BindProperty] public string Cart { get; set; } = "{}";
    [BindProperty] public string? CouponId { get; set; }
    [BindProperty] public string PaymentResult { get; set; } = "approve";
    [BindProperty] public string AttemptId { get; set; } = "";
    [BindProperty] public string? ReceiptEmail { get; set; }
    [BindProperty] public string? ReaderSession { get; set; }
    [TempData] public string? ReceiptId { get; set; }
    [TempData] public string? Message { get; set; }
    [TempData] public string? MessageKind { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken, string? rfidSession = null, string? attemptId = null)
    {
        Response.Headers.CacheControl = "no-store";
        CheckoutAccess.Owner(HttpContext, create: true);
        AttemptId = Guid.TryParseExact(attemptId, "N", out _) ? attemptId : Guid.NewGuid().ToString("N");
        ReceiptEmail = AccountEmail;
        try { Products = await store.GetProductsAsync(cancellationToken); }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Cannot load checkout inventory.");
            DatabaseWarning = "Inventory unavailable. Check the MongoDB connection before checking out.";
        }
        if (AccountId is { } accountId)
        {
            try { Coupons = await store.GetCouponsAsync(accountId, cancellationToken); }
            catch (Exception exception) when (exception is MongoDB.Driver.MongoException or TimeoutException)
            {
                logger.LogWarning(exception, "Cannot load checkout coupons.");
                CouponWarning = "Coupons are temporarily unavailable.";
            }
        }
        RfidSession = OpenReaderSession(rfidSession);
    }

    public async Task<IActionResult> OnGetReceiptAsync(string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 40) return BadRequest();
        var sale = await store.FindSaleAsync(id, cancellationToken);
        if (sale is null || sale.PaymentStatus != "approved" || !CheckoutAccess.Owns(HttpContext, sale)) return NotFound();
        return new JsonResult(new { receipt = emailService.FormatReceipt(sale) });
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var owner = CheckoutAccess.Owner(HttpContext, create: false);
        if (owner.Length == 0) return BadRequest("This checkout session expired. Reload checkout before trying again.");
        DemoSale sale;
        try
        {
            if (string.IsNullOrWhiteSpace(Cart)) throw new InvalidOperationException("Add an item first.");
            // Up to 100 UPC/alias codes, each bounded independently by the service.
            if (Cart.Length > 16000) throw new InvalidOperationException("Cart is too large.");
            var items = JsonSerializer.Deserialize<Dictionary<string, int>>(Cart)
                ?? throw new InvalidOperationException("Add an item first.");
            sale = await store.CheckoutAsync(items, PaymentResult, AccountEmail ?? ReceiptEmail,
                new CheckoutAttempt(AttemptId, owner, AccountId), cancellationToken, emailService.Sender, CouponId);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            await ShowResultAsync(success: false);
            Message = exception is JsonException ? "Invalid cart. Please add items again." : exception.Message;
            MessageKind = "error";
            return RedirectToPage(new { attemptId = AttemptId });
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Checkout could not be saved.");
            await ShowResultAsync(success: false);
            Message = "Checkout could not be saved. Check the database connection.";
            MessageKind = "error";
            return RedirectToPage(new { attemptId = AttemptId });
        }

        if (sale.PaymentStatus == "declined")
        {
            await ShowResultAsync(success: false);
            var released = TryCloseReader();
            Message = $"Payment declined. Attempt {sale.Id[..8]} recorded; no stock or points changed." +
                (released ? "" : " The reader pairing could not be released.");
            MessageKind = "error";
            return RedirectToPage();
        }

        // From here the purchase is committed. Notification failures must not invite a second purchase.
        var readerReleased = TryCloseReader();
        ReceiptId = sale.Id;
        MessageKind = "success";
        await ShowResultAsync(success: true);
        var emailSent = sale.EmailStatus == "sent";
        if (sale.EmailStatus == "pending")
        {
            using var sendTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                if (await store.ClaimReceiptEmailAsync(sale.Id, sendTimeout.Token))
                {
                    emailSent = await emailService.SendAsync(sale, sendTimeout.Token);
                    using var saveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    await store.UpdateEmailStatusAsync(sale.Id, emailSent ? "sent" : "failed", saveTimeout.Token);
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Receipt delivery status is uncertain for sale {SaleId}; it will not be sent twice.", sale.Id);
            }
        }
        Message = $"Payment approved! Receipt {sale.Id[..8]} — ${sale.Total:F2}. " +
            (sale.AccountId is null ? "" : $"Earned {sale.PointsEarned} points. Balance: {sale.PointsBalanceAfter}. ") +
            (emailSent ? "Receipt emailed." : "View your receipt below.") +
            (readerReleased ? "" : " The reader pairing could not be released; release it before the next checkout.");
        return RedirectToPage();
    }

    private string OpenReaderSession(string? existingToken)
    {
        try { return rfid.Open(HttpContext, existingToken: existingToken); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogWarning(exception, "Reader session is unavailable; manual checkout remains available.");
            return "";
        }
    }

    private bool TryCloseReader()
    {
        try
        {
            rfid.Close(HttpContext, ReaderSession ?? Request.Query["rfidSession"].ToString());
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Checkout is saved, but the reader pairing could not be released.");
            return false;
        }
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
