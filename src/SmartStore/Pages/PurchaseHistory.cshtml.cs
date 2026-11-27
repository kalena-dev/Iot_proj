using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SmartStore.Services;

namespace SmartStore.Pages;

public sealed record CustomerChartPoint(string Label, decimal Amount);
public sealed record ProductPurchase(string ReceiptId, DateTime PurchasedAtUtc, DemoSaleLine Item)
{
    public decimal LineTotal => Item.Price * Item.Quantity;
}

public sealed class PurchaseHistoryModel(StoreDemo store) : PageModel
{
    private const int PageSize = 10;
    public IReadOnlyList<DemoSale> Sales { get; private set; } = [];
    public IReadOnlyList<ProductPurchase> ProductPurchases { get; private set; } = [];
    public decimal TotalSpent { get; private set; }
    public int HistoryPage { get; private set; }
    public int PageCount { get; private set; }
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }
    [BindProperty(SupportsGet = true)] public string? Product { get; set; }

    public async Task<IActionResult> OnGetAsync(int historyPage, CancellationToken cancellationToken)
    {
        if (StoreAccounts.EmailOf(User) is not { } email) return Challenge();
        if (From > To || To == DateOnly.MaxValue)
            ModelState.AddModelError(nameof(To), "Choose a valid end date on or after the start date.");
        if (!ModelState.IsValid) return Page();

        var start = From?.ToDateTime(TimeOnly.MinValue).ToUniversalTime();
        var end = To?.AddDays(1).ToDateTime(TimeOnly.MinValue).ToUniversalTime();
        var purchases = await store.CustomerHistoryAsync(email, cancellationToken, start, end);
        TotalSpent = purchases.Sum(sale => sale.Total);
        IEnumerable<DemoSale> matchingReceipts = purchases;
        if (Product?.Trim() is { Length: > 0 } search)
        {
            ProductPurchases = purchases.SelectMany(sale => sale.Items
                .Where(item => item.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    item.Code.Equals(search, StringComparison.OrdinalIgnoreCase))
                .Select(item => new ProductPurchase(sale.Id, sale.CreatedAtUtc, item))).ToArray();
            var receiptIds = ProductPurchases.Select(purchase => purchase.ReceiptId).ToHashSet();
            matchingReceipts = purchases.Where(sale => receiptIds.Contains(sale.Id));
        }

        var receipts = matchingReceipts.ToArray();
        PageCount = Math.Max(1, (receipts.Length + PageSize - 1) / PageSize);
        HistoryPage = Math.Clamp(historyPage, 1, PageCount);
        Sales = receipts.Skip((HistoryPage - 1) * PageSize).Take(PageSize).ToArray();
        return Page();
    }
}
