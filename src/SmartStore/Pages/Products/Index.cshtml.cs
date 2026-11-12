using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SmartStore.Data;
using SmartStore.Models;

namespace SmartStore.Pages.Products;

public sealed class IndexModel(IProductRepository repository, ILogger<IndexModel> logger) : PageModel
{
    public const int LowStockThreshold = 5;

    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public string? Category { get; set; }

    public IReadOnlyList<Product> Items { get; private set; } = [];
    public IReadOnlyList<string> Categories { get; private set; } = [];
    public int TotalCount { get; private set; }
    public string? DatabaseWarning { get; private set; }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public string? StatusKind { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        try
        {
            var all = await repository.GetAllAsync(cancellationToken);
            TotalCount = all.Count;
            Categories = all.Select(p => p.Category).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();

            IEnumerable<Product> filtered = all;
            if (!string.IsNullOrWhiteSpace(Category))
                filtered = filtered.Where(p => p.Category.Equals(Category, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(Q))
            {
                var term = Q.Trim();
                filtered = filtered.Where(p =>
                    p.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    p.Producer.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    p.Upc.Equals(term, StringComparison.Ordinal) ||
                    p.Epc.Equals(term, StringComparison.OrdinalIgnoreCase));
            }
            Items = filtered.ToList();
        }
        catch (Exception exception) when (exception is TimeoutException or MongoDB.Driver.MongoException)
        {
            logger.LogWarning(exception, "Cannot load products.");
            DatabaseWarning = "Products are unavailable. Check the MongoDB connection.";
        }
    }

    public async Task<IActionResult> OnPostDeleteAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            var product = await repository.GetByIdAsync(id, cancellationToken);
            if (product is null || !await repository.DeleteAsync(id, cancellationToken))
            {
                SetStatus("That product was already deleted.", "error");
            }
            else
            {
                SetStatus($"Product \"{product.Name}\" deleted successfully.", "success");
            }
        }
        catch (Exception exception) when (exception is TimeoutException or MongoDB.Driver.MongoException)
        {
            logger.LogWarning(exception, "Cannot delete product {ProductId}.", id);
            SetStatus("The product could not be deleted. Check the MongoDB connection.", "error");
        }

        return RedirectToPage(new { Q, Category });
    }

    private void SetStatus(string message, string kind) => (StatusMessage, StatusKind) = (message, kind);
}
