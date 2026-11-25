using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SmartStore.Pages;

public sealed class PurchaseHistoryModel : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Profile");
}
