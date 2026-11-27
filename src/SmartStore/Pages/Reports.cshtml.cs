using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SmartStore.Services;

namespace SmartStore.Pages;

public sealed record ProductSales(string Code, string Name, long Units);
public sealed record CustomerDay(DateOnly Date, int Customers);

public sealed class ReportsModel(StoreDemo store) : PageModel
{
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }
    public IReadOnlyList<ProductSales> Sales { get; private set; } = [];
    public decimal Revenue { get; private set; }
    public int Customers { get; private set; }
    public int ReturningCustomers { get; private set; }
    public CustomerDay? BusiestDay { get; private set; }
    public CustomerDay? QuietestDay { get; private set; }

    public async Task OnGetAsync(CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        var today = DateOnly.FromDateTime(DateTime.Today);
        To ??= From ?? today;
        From ??= new DateOnly(To.Value.Year, To.Value.Month, 1);
        if (From > To || To == DateOnly.MaxValue)
            ModelState.AddModelError(nameof(To), "Choose an end date on or after the start date.");
        if (From is { } from && To is { } to && to.DayNumber - from.DayNumber > 365)
            ModelState.AddModelError(nameof(To), "Choose a report period of at most one year.");
        if (!ModelState.IsValid) return;

        var start = From.Value.ToDateTime(TimeOnly.MinValue).ToUniversalTime();
        var end = To.Value.AddDays(1).ToDateTime(TimeOnly.MinValue).ToUniversalTime();
        var inventoryTask = store.GetProductsAsync(token);
        var reportTask = store.ReportAsync(start, end, token);
        await Task.WhenAll(reportTask, inventoryTask);
        var report = await reportTask;
        Revenue = report.Revenue;
        var sold = report.Products.ToDictionary(product => product.ProductKey,
            product => new ProductSales(product.Code, product.Name, product.Units), StringComparer.Ordinal);
        foreach (var product in await inventoryTask)
            sold.TryAdd(product.ProductId, new ProductSales(product.Code, product.Name, 0));
        Sales = sold.Values.OrderByDescending(product => product.Units).ThenBy(product => product.Name).ToArray();
        Customers = report.Customers;
        ReturningCustomers = report.ReturningCustomers;
        var activeDays = report.Days.Select(day => new CustomerDay(day.Date, day.Customers)).OrderBy(day => day.Date).ToArray();
        var firstEmptyDay = From.Value;
        foreach (var day in activeDays)
        {
            if (day.Date != firstEmptyDay) break;
            firstEmptyDay = firstEmptyDay.AddDays(1);
        }
        BusiestDay = activeDays.MaxBy(day => day.Customers) ?? new CustomerDay(From.Value, 0);
        QuietestDay = firstEmptyDay <= To.Value
            ? new CustomerDay(firstEmptyDay, 0)
            : activeDays.MinBy(day => day.Customers);
    }
}
