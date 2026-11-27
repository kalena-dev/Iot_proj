using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using SmartStore.Services;

namespace SmartStore.Pages;

[EnableRateLimiting("Account")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AccountSecurityModel(StoreAccounts accounts) : PageModel
{
    public StoreAccount? Account { get; private set; }
    [BindProperty] public string? FullName { get; set; }
    [BindProperty] public string? Password { get; set; }
    public string? Message { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!await LoadAsync(cancellationToken)) return Challenge();
        FullName = Account!.FullName;
        return Page();
    }

    public async Task<IActionResult> OnPostNameAsync(CancellationToken cancellationToken)
    {
        if (!await LoadAsync(cancellationToken)) return Challenge();
        if (string.IsNullOrWhiteSpace(FullName) || FullName.Trim().Length > 100)
        {
            ModelState.AddModelError(nameof(FullName), "Enter your name (up to 100 characters).");
            return Page();
        }
        if (!await accounts.UpdateNameAsync(Account!.AccountId, FullName, cancellationToken)) return Challenge();
        Message = "Name updated.";
        return Page();
    }

    public async Task<IActionResult> OnPostSignOutAllAsync(CancellationToken cancellationToken)
    {
        if (StoreAccounts.AccountIdOf(User) is not { } id) return Challenge();
        await accounts.RevokeSessionsAsync(id, cancellationToken);
        await HttpContext.SignOutAsync();
        TempData.Clear();
        return RedirectToPage("/Login");
    }

    public async Task<IActionResult> OnPostDisableAsync(CancellationToken cancellationToken)
    {
        if (!await LoadAsync(cancellationToken)) return Challenge();
        var confirmed = Password is { Length: > 0 and <= 128 }
            ? await accounts.AuthenticateAsync(Account!.Email, Password, cancellationToken) : null;
        if (confirmed is null || confirmed.AccountId != Account!.AccountId)
        {
            ModelState.AddModelError("", "Unable to confirm. Check your password or try again later.");
            return Page();
        }
        await accounts.SetDisabledAsync(Account.AccountId, true, cancellationToken);
        await HttpContext.SignOutAsync();
        TempData.Clear();
        return RedirectToPage("/Login");
    }

    private async Task<bool> LoadAsync(CancellationToken cancellationToken)
    {
        Account = StoreAccounts.AccountIdOf(User) is { } id ? await accounts.GetByIdAsync(id, cancellationToken) : null;
        return Account is { Disabled: false };
    }
}
