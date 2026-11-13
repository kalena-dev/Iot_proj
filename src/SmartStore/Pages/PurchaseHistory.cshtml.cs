using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SmartStore.Services;

namespace SmartStore.Pages;
public sealed class PurchaseHistoryModel(StoreDemo store) : PageModel
{
    [BindProperty(SupportsGet = true)] public string Email { get; set; } = "";
    public IReadOnlyList<DemoSale> Sales { get; private set; } = [];
    public async Task OnGetAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(Email) && Email.Length <= 254)
            Sales = await store.CustomerHistoryAsync(Email, ct);
    }
}
