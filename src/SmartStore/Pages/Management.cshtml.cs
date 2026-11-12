using Microsoft.AspNetCore.Mvc.RazorPages;
using SmartStore.Services;

namespace SmartStore.Pages;

public sealed class ManagementModel(StoreDemo store, ILogger<ManagementModel> logger) : PageModel
{
    public IReadOnlyList<DemoProduct> Products { get; private set; } = [];
    public IReadOnlyList<DemoSale> Sales { get; private set; } = [];
    public string? DatabaseWarning { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        try { Products = await store.GetProductsAsync(cancellationToken); }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Cannot load management inventory.");
            DatabaseWarning = "Inventory unavailable. Check the MongoDB connection.";
        }
        try { Sales = await store.RecentSalesAsync(cancellationToken); }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Cannot load recent sales.");
            DatabaseWarning = DatabaseWarning is null
                ? "Sales unavailable. Check the MongoDB connection."
                : "Inventory and sales unavailable. Check the MongoDB connection.";
        }
    }

}
