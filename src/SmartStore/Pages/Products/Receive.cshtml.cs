using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SmartStore.Data;
using SmartStore.Models;
using SmartStore.Services;

namespace SmartStore.Pages.Products;

public sealed class ReceiveModel(IProductRepository repository, ILogger<ReceiveModel> logger, RfidCheckout rfid) : PageModel
{
    [BindProperty] public ReceptionInput Input { get; set; } = new();
    [BindProperty(SupportsGet = true)] public string? Code { get; set; }
    [BindProperty] public string? ReaderSession { get; set; }

    public IReadOnlyList<Product> AllProducts { get; private set; } = [];
    public Product? Selected { get; private set; }
    public IReadOnlyList<Reception> History { get; private set; } = [];
    public IReadOnlyList<Item> TaggedItems { get; private set; } = [];
    public int TaggedInStock { get; private set; }
    public string? DatabaseWarning { get; private set; }
    public string RfidSession { get; private set; } = "";
    public bool SimulationMode => rfid.IsSimulation;

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public string? StatusKind { get; set; }

    public async Task<IActionResult> OnGetAsync(string? id, CancellationToken cancellationToken, string? rfidSession = null)
    {
        try
        {
            // A scanned UPC, barcode or registered tag selects the product.
            if (!string.IsNullOrWhiteSpace(Code))
            {
                var scanned = (await repository.ResolveCodeAsync(Code, cancellationToken))?.Product;
                if (scanned is null)
                {
                    SetStatus($"No product has the code \"{Code.Trim()}\". New RFID tags are registered by choosing the product, then scanning them into the delivery.", "error");
                    return RedirectToPage(new { id });
                }
                return RedirectToPage(new { id = scanned.Id, Code = (string?)null });
            }

            await LoadAsync(id, cancellationToken);
            if (id is not null && Selected is null)
            {
                SetStatus("That product no longer exists. It may have been deleted.", "error");
                return RedirectToPage(new { id = (string?)null });
            }
            if (Selected is { ArchivedAtUtc: null })
            {
                Input.ProductId = Selected.Id!;
                RfidSession = OpenReaderSession(rfidSession);
            }
        }
        catch (Exception exception) when (exception is TimeoutException or MongoDB.Driver.MongoException)
        {
            logger.LogWarning(exception, "Cannot load receiving page.");
            DatabaseWarning = "Products are unavailable. Check the MongoDB connection.";
        }
        return Page();
    }

    public IActionResult OnGetRfid()
    {
        Response.Headers.CacheControl = "no-store";
        var tags = rfid.Read(HttpContext, Request.Headers["X-Rfid-Session"].ToString(), receiving: true);
        return tags is null ? NotFound() : new JsonResult(tags);
    }

    public IActionResult OnPostPairRfid()
    {
        Response.Headers.CacheControl = "no-store";
        return rfid.PairReader(HttpContext, Request.Headers["X-Rfid-Session"].ToString(), receiving: true)
            ? new JsonResult(new { paired = true })
            : StatusCode(StatusCodes.Status409Conflict, new { error = "The reader is paired elsewhere. Release that session first." });
    }

    public IActionResult OnPostCloseRfid()
    {
        return rfid.Close(HttpContext, Request.Headers["X-Rfid-Session"].ToString(), receiving: true)
            ? new JsonResult(new { closed = true }) : NotFound();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var today = DateTime.Today;
        if (Input.ReceivedOn is { } received && received.Date > today)
            ModelState.AddModelError("Input.ReceivedOn", "The date received can't be in the future.");
        if (Input.ExpiresOn is { } expires && Input.ReceivedOn is { } receivedOn && expires.Date < receivedOn.Date)
            ModelState.AddModelError("Input.ExpiresOn", "The expiry date can't be before the date received.");
        var (epcs, invalid) = Input.ParseTags();
        if (invalid.Count > 0)
            ModelState.AddModelError("Input.Tags", $"Not a valid RFID tag (EPC): {string.Join(", ", invalid.Take(3))}. " +
                "An EPC is 4–24 hexadecimal characters in whole bytes.");
        else if (epcs.Count > ReceptionInput.MaxTags)
            ModelState.AddModelError("Input.Tags", $"Receive at most {ReceptionInput.MaxTags} tags at a time.");
        else if (epcs.Count == 0 && (Input.Quantity ?? 0) < 1)
            ModelState.AddModelError("Input.Tags", "Scan the delivery's RFID tags, or enter a number of untagged units.");

        try
        {
            if (ModelState.IsValid)
            {
                var reception = Input.ToReception();
                var product = await repository.ReceiveStockAsync(reception, cancellationToken);
                var skipped = epcs.Count - reception.Epcs.Count;
                var parts = new List<string>();
                if (reception.Epcs.Count > 0) parts.Add($"{reception.Epcs.Count} tagged");
                if (Input.Quantity is > 0) parts.Add($"{Input.Quantity} untagged");
                SetStatus(reception.Quantity == 0
                    ? $"Corrected the previously guessed tag(s) for {product.Name}. No delivery or additional stock was recorded."
                    : $"Received {reception.Quantity} × {product.Name} ({string.Join(", ", parts)}). Stock is now {product.QuantityInStock}." +
                        (skipped > 0 ? $" {skipped} existing tag(s) were checked; only new units were added." : ""), "success");
                CloseReaderAfterSave();
                return RedirectToPage(new { id = product.Id });
            }
        }
        catch (ProductNotFoundException exception)
        {
            ModelState.AddModelError("Input.ProductId", exception.Message);
        }
        catch (TransactionOutcomeUnknownException exception)
        {
            SetStatus(exception.Message, "error");
            return RedirectToPage(new { id = Input.ProductId });
        }
        catch (InvalidOperationException exception)
        {
            // A tag belongs to another product, was sold, or every tag was already in stock.
            ModelState.AddModelError("Input.Tags", exception.Message);
        }
        catch (Exception exception) when (exception is TimeoutException or MongoDB.Driver.MongoException)
        {
            logger.LogWarning(exception, "Cannot receive stock.");
            SetStatus("The delivery could not be saved. Check the MongoDB connection.", "error");
            return RedirectToPage(new { id = Input.ProductId });
        }

        try
        {
            await LoadAsync(Input.ProductId, cancellationToken);
            if (Selected is { ArchivedAtUtc: null }) RfidSession = OpenReaderSession(
                existingToken: Request.Query["rfidSession"].ToString());
        }
        catch (Exception exception) when (exception is TimeoutException or MongoDB.Driver.MongoException)
        {
            logger.LogWarning(exception, "Cannot reload receiving page.");
            DatabaseWarning = "Products are unavailable. Check the MongoDB connection.";
        }
        return Page();
    }

    public async Task<IActionResult> OnPostUndoAsync(string receptionId, string? productId, CancellationToken cancellationToken)
    {
        try
        {
            SetStatus(await repository.DeleteReceptionAsync(receptionId, cancellationToken)
                ? "Delivery removed, its tags unregistered and stock adjusted."
                : "That delivery was already removed.",
                "success");
            CloseReaderAfterSave();
        }
        catch (InvalidOperationException exception)
        {
            SetStatus(exception.Message, "error");
        }
        catch (Exception exception) when (exception is TimeoutException or MongoDB.Driver.MongoException)
        {
            logger.LogWarning(exception, "Cannot undo reception {ReceptionId}.", receptionId);
            SetStatus("The delivery could not be removed. Check the MongoDB connection.", "error");
        }
        return RedirectToPage(new { id = productId });
    }

    private async Task LoadAsync(string? id, CancellationToken cancellationToken)
    {
        AllProducts = (await repository.GetAllAsync(cancellationToken)).Where(p => p.ArchivedAtUtc == null).ToArray();
        Selected = string.IsNullOrWhiteSpace(id) ? null : await repository.GetByIdAsync(id, cancellationToken);
        if (Selected is null) return;
        History = await repository.GetReceptionsAsync(Selected.Id!, 50, cancellationToken);
        TaggedInStock = await repository.CountTaggedInStockAsync(Selected.Id!, cancellationToken);
        TaggedItems = await repository.ListItemsAsync(Selected.Id!, Item.InStock, 25, cancellationToken);
    }

    private string OpenReaderSession(string? existingToken)
    {
        try { return rfid.Open(HttpContext, receiving: true, existingToken: existingToken); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogWarning(exception, "Reader session is unavailable; manual receiving remains available.");
            return "";
        }
    }

    private void CloseReaderAfterSave()
    {
        try { rfid.Close(HttpContext, ReaderSession ?? Request.Query["rfidSession"].ToString(), receiving: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Inventory is saved, but the reader pairing could not be released.");
            StatusMessage += " The reader pairing could not be released; release it before receiving another delivery.";
        }
    }

    private void SetStatus(string message, string kind) => (StatusMessage, StatusKind) = (message, kind);
}
