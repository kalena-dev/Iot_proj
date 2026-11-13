using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SmartStore.Models;
using SmartStore.Services;

namespace SmartStore.Pages;

public sealed class RecordsModel(StoreDemo store) : PageModel
{
    private const int PageSize = 25;
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    [BindProperty(SupportsGet = true)] public string? ViewLog { get; set; }
    public List<DemoSale> Sales { get; private set; } = [];
    public List<EmailLog> Emails { get; private set; } = [];
    public bool HasNext { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken token)
    {
        if (PageNumber < 1 || PageNumber > int.MaxValue / PageSize) return BadRequest();
        Response.Headers.CacheControl = "no-store";
        var offset = (PageNumber - 1) * PageSize;
        if (ViewLog == "email")
        {
            Emails = await store.ListEmailsAsync(offset, PageSize + 1, token);
            HasNext = Emails.Count > PageSize;
            if (HasNext) Emails.RemoveAt(PageSize);
        }
        else
        {
            Sales = await store.ListTransactionsAsync(offset, PageSize + 1, token);
            HasNext = Sales.Count > PageSize;
            if (HasNext) Sales.RemoveAt(PageSize);
        }
        return Page();
    }
}
