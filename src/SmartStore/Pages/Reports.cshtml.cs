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
        if (!ModelState.IsValid) return;

        var start = From.Value.ToDateTime(TimeOnly.MinValue).ToUniversalTime();
        var end = To.Value.AddDays(1).ToDateTime(TimeOnly.MinValue).ToUniversalTime();
        var inventoryTask = store.GetProductsAsync(token);
        var salesTask = store.ApprovedSalesInPeriodAsync(start, end, token);
        var previousTask = store.PreviousPurchasersAsync(start, token);
        await Task.WhenAll(salesTask, previousTask, inventoryTask);
        var sales = await salesTask;
        Revenue = sales.Sum(sale => sale.Total);
        var sold = sales.SelectMany(sale => sale.Items).GroupBy(item => item.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key,
                group => new ProductSales(group.Key, group.Last().Name, group.Sum(item => (long)item.Quantity)),
                StringComparer.OrdinalIgnoreCase);
        foreach (var product in await inventoryTask)
            sold.TryAdd(product.Code, new ProductSales(product.Code, product.Name, 0));
        Sales = sold.Values.OrderByDescending(product => product.Units).ThenBy(product => product.Name).ToArray();

        var identified = sales.Where(sale => !string.IsNullOrWhiteSpace(sale.CustomerEmail)).ToArray();
        var comparer = StringComparer.OrdinalIgnoreCase;
        var current = identified.Select(sale => sale.CustomerEmail!.Trim()).ToHashSet(comparer);
        var previous = (await previousTask).Select(email => email?.Trim()).ToHashSet(comparer);
        Customers = current.Count;
        ReturningCustomers = current.Count(email => previous.Contains(email));

        var activeDays = identified.GroupBy(sale => DateOnly.FromDateTime(sale.CreatedAtUtc.ToLocalTime()))
            .Select(group => new CustomerDay(group.Key,
                group.Select(sale => sale.CustomerEmail!.Trim()).Distinct(comparer).Count()))
            .OrderBy(day => day.Date).ToArray();
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
