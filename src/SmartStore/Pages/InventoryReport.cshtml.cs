using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SmartStore.Services;

namespace SmartStore.Pages;

public sealed class InventoryReportModel(StoreDemo store) : PageModel
{
    public const int LowStockThreshold = SmartStore.Pages.Products.IndexModel.LowStockThreshold;
    public IReadOnlyList<InventoryReportRow> Products { get; private set; } = [];

    public static string StockClass(int stock) => stock <= 0 ? "stock--out"
        : stock <= LowStockThreshold ? "stock--low" : "stock--ok";

    public async Task OnGetAsync(CancellationToken token) => Products = await ReadAsync(token);

    public async Task<IActionResult> OnGetStockAsync(CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        return new JsonResult(await ReadAsync(token));
    }

    private async Task<IReadOnlyList<InventoryReportRow>> ReadAsync(CancellationToken token) =>
        (await store.GetProductsAsync(token)).Select(product => new InventoryReportRow(
            product.Code, product.Name, product.Stock, StockClass(product.Stock),
            product.Stock <= 0 ? "Out of stock" : product.Stock <= LowStockThreshold ? "Low stock" : "In stock")).ToArray();
}

public sealed record InventoryReportRow(string Code, string Name, int Stock, string StockClass, string Status);
