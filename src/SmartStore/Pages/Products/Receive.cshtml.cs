using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SmartStore.Data;
using SmartStore.Models;

namespace SmartStore.Pages.Products;

public sealed class ReceiveModel(IProductRepository repository, ILogger<ReceiveModel> logger) : PageModel
{
    [BindProperty] public ReceptionInput Input { get; set; } = new();
    [BindProperty(SupportsGet = true)] public string? Code { get; set; }

    public IReadOnlyList<Product> AllProducts { get; private set; } = [];
    public Product? Selected { get; private set; }
    public IReadOnlyList<Reception> History { get; private set; } = [];
    public string? DatabaseWarning { get; private set; }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public string? StatusKind { get; set; }

    public async Task<IActionResult> OnGetAsync(string? id, CancellationToken cancellationToken)
    {
        try
        {
            // A scanned UPC or EPC selects the product.
            if (!string.IsNullOrWhiteSpace(Code))
            {
                var scanned = (await repository.ResolveCodeAsync(Code, cancellationToken))?.Product;
                if (scanned is null)
                {
                    SetStatus($"No product has the code \"{Code.Trim()}\". Add it as a new product first.", "error");
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
            if (Selected is not null) Input.ProductId = Selected.Id!;
        }
        catch (Exception exception) when (exception is TimeoutException or MongoDB.Driver.MongoException)
        {
            logger.LogWarning(exception, "Cannot load receiving page.");
            DatabaseWarning = "Products are unavailable. Check the MongoDB connection.";
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var today = DateTime.Today;
        if (Input.ReceivedOn is { } received && received.Date > today)
            ModelState.AddModelError("Input.ReceivedOn", "The date received can't be in the future.");
        if (Input.ExpiresOn is { } expires && Input.ReceivedOn is { } receivedOn && expires.Date < receivedOn.Date)
            ModelState.AddModelError("Input.ExpiresOn", "The expiry date can't be before the date received.");

        try
        {
            if (ModelState.IsValid)
            {
                var product = await repository.ReceiveStockAsync(Input.ToReception(), cancellationToken);
                SetStatus($"Received {Input.Quantity} × {product.Name}. Stock is now {product.QuantityInStock}.", "success");
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
        catch (Exception exception) when (exception is TimeoutException or MongoDB.Driver.MongoException)
        {
            logger.LogWarning(exception, "Cannot receive stock.");
            SetStatus("The delivery could not be saved. Check the MongoDB connection.", "error");
            return RedirectToPage(new { id = Input.ProductId });
        }

        try { await LoadAsync(Input.ProductId, cancellationToken); }
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
                ? "Delivery removed and stock adjusted."
                : "That delivery was already removed.",
                "success");
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
        AllProducts = await repository.GetAllAsync(cancellationToken);
        Selected = string.IsNullOrWhiteSpace(id) ? null : AllProducts.FirstOrDefault(p => p.Id == id);
        History = Selected is null ? [] : await repository.GetReceptionsAsync(Selected.Id!, 50, cancellationToken);
    }

    private void SetStatus(string message, string kind) => (StatusMessage, StatusKind) = (message, kind);
}
