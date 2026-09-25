using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SmartStore.Services;

namespace SmartStore.Pages;

public sealed class ManagementModel(StoreDemo store, ILogger<ManagementModel> logger) : PageModel
{
    public IReadOnlyList<DemoProduct> Products => store.Products;
    public IReadOnlyList<DemoSale> Sales { get; private set; } = [];
    public string? DatabaseWarning { get; private set; }
    [BindProperty] public string Code { get; set; } = "";
    [BindProperty] public int Stock { get; set; }
    [TempData] public string? Message { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        try { Sales = await store.RecentSalesAsync(cancellationToken); }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Cannot load demo sales.");
            DatabaseWarning = "Sales unavailable. Check the MongoDB connection.";
        }
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        try
        {
            await store.SetStockAsync(Code, Stock, cancellationToken);
            Message = $"Stock for {Code} set to {Stock}.";
        }
        catch (InvalidOperationException exception) { Message = exception.Message; }
        return RedirectToPage();
    }
}
