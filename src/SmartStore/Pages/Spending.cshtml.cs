using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SmartStore.Services;

namespace SmartStore.Pages;

public sealed record SpendingPeriod(DateTime Start, DateTime End, string Label, decimal Amount);

public sealed class SpendingModel(StoreDemo store) : PageModel
{
    public string BudgetKey { get; private set; } = "";
    public string View { get; private set; } = "monthly";
    public DateTime Start { get; private set; }
    public DateTime End { get; private set; }
    public DateTime? Previous { get; private set; }
    public DateTime? Next { get; private set; }
    public string Heading { get; private set; } = "";
    public IReadOnlyList<SpendingPeriod> Periods { get; private set; } = [];
    public IReadOnlyList<CustomerChartPoint> Chart { get; private set; } = [];
    public int CalendarOffset => ((int)Start.DayOfWeek + 6) % 7;

    public async Task<IActionResult> OnGetAsync(string? view, DateOnly? date, CancellationToken cancellationToken)
    {
        if (StoreAccounts.EmailOf(User) is not { } email) return Challenge();

        BudgetKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(email.ToLowerInvariant())));
        View = view is "daily" or "weekly" or "yearly" ? view : "monthly";
        var today = DateTime.Today;
        var selected = date?.ToDateTime(TimeOnly.MinValue) ?? today;
        var earliest = new DateTime(10, 1, 1);
        if (selected < earliest || selected > today) selected = today;
        Start = View switch
        {
            "daily" => selected,
            "weekly" => selected.AddDays(-((int)selected.DayOfWeek + 6) % 7),
            "yearly" => new DateTime(selected.Year, 1, 1),
            _ => new DateTime(selected.Year, selected.Month, 1)
        };
        End = View switch
        {
            "daily" => Start.AddDays(1),
            "weekly" => Start.AddDays(7),
            "yearly" => Start.AddYears(1),
            _ => Start.AddMonths(1)
        };
        Previous = Start > earliest ? Start.AddDays(-1) : null;
        Next = End <= today ? End : null;
        Heading = View switch
        {
            "daily" => Start.ToString("dddd, MMMM d, yyyy"),
            "weekly" => $"{Start:MMM d, yyyy} – {End.AddDays(-1):MMM d, yyyy}",
            "yearly" => Start.ToString("yyyy"),
            _ => Start.ToString("MMMM yyyy")
        };

        var purchases = await store.CustomerHistoryAsync(email, cancellationToken,
            Start.ToUniversalTime(), End.ToUniversalTime());
        var totalsByDay = purchases.GroupBy(sale => sale.CreatedAtUtc.ToLocalTime().Date)
            .ToDictionary(day => day.Key, day => day.Sum(sale => sale.Total));

        var periods = new List<SpendingPeriod>();
        for (var start = Start; start < End; )
        {
            var end = View == "yearly" ? start.AddMonths(1) : start.AddDays(1);
            var label = View == "yearly" ? start.ToString("MMMM") : start.ToString("ddd, MMM d");
            var amount = totalsByDay.Where(day => day.Key >= start && day.Key < end).Sum(day => day.Value);
            periods.Add(new(start, end, label, amount));
            start = end;
        }

        Periods = periods;
        Chart = View switch
        {
            "daily" => Enumerable.Range(0, 6).Select(bucket => new CustomerChartPoint(
                $"{Start.AddHours(bucket * 4):HH:mm}–{Start.AddHours((bucket + 1) * 4):HH:mm}",
                purchases.Where(sale => sale.CreatedAtUtc.ToLocalTime().Hour / 4 == bucket).Sum(sale => sale.Total)))
                .ToArray(),
            "monthly" => periods.GroupBy(period => period.Start.AddDays(-((int)period.Start.DayOfWeek + 6) % 7))
                .Select(week => new CustomerChartPoint($"{week.First().Start:MMM d}–{week.Last().Start:MMM d}",
                    week.Sum(period => period.Amount))).ToArray(),
            _ => periods.Select(period => new CustomerChartPoint(period.Label, period.Amount)).ToArray()
        };
        return Page();
    }
}
