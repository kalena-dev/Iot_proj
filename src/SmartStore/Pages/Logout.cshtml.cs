using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SmartStore.Services;

namespace SmartStore.Pages;

public sealed class LogoutModel(StoreAccounts accounts) : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Profile");

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (StoreAccounts.AccountIdOf(User) is { } accountId)
            await accounts.RevokeSessionsAsync(accountId, cancellationToken);
        await HttpContext.SignOutAsync();
        TempData.Clear();
        return RedirectToPage("/Login");
    }
}
