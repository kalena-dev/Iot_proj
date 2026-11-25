using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SmartStore.Pages;

public sealed class LogoutModel : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Profile");

    public async Task<IActionResult> OnPostAsync()
    {
        await HttpContext.SignOutAsync();
        TempData.Clear();
        return RedirectToPage("/Login");
    }
}
