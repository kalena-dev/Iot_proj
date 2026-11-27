using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using SmartStore.Services;

namespace SmartStore.Pages;

[EnableRateLimiting("Account")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class StaffAccountsModel(StoreAccounts accounts, IConfiguration configuration) : PageModel
{
    [BindProperty] public string AccountId { get; set; } = "";
    public StoreAccount? Selected { get; private set; }
    public string? Message { get; private set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostLookupAsync(CancellationToken cancellationToken)
    {
        if (!StoreAccounts.IsStaff(User, configuration)) return Forbid();
        Selected = Guid.TryParseExact(AccountId, "N", out _) ? await accounts.GetByIdAsync(AccountId, cancellationToken) : null;
        if (Selected is null) ModelState.AddModelError("", "Account ID was not found.");
        return Page();
    }

    public async Task<IActionResult> OnPostProvisionAsync(CancellationToken cancellationToken)
    {
        if (!StoreAccounts.IsStaff(User, configuration)) return Forbid();
        var result = await accounts.ProvisionStaffAsync(AccountId, configuration, cancellationToken);
        Message = result ? "Staff access provisioned. Existing sessions were revoked; password and email verification are required."
            : "Provisioning requires an enabled account ID explicitly configured in AUTH_ADMIN_ACCOUNT_IDS.";
        return Page();
    }

    public async Task<IActionResult> OnPostDisableAsync(CancellationToken cancellationToken) =>
        await ChangeDisabledAsync(true, cancellationToken);

    public async Task<IActionResult> OnPostEnableAsync(CancellationToken cancellationToken) =>
        await ChangeDisabledAsync(false, cancellationToken);

    public async Task<IActionResult> OnPostRevokeAsync(CancellationToken cancellationToken)
    {
        if (!StoreAccounts.IsStaff(User, configuration)) return Forbid();
        await accounts.RevokeSessionsAsync(AccountId, cancellationToken);
        Message = "Sessions revoked for the specified account, if present.";
        return Page();
    }

    private async Task<IActionResult> ChangeDisabledAsync(bool disabled, CancellationToken cancellationToken)
    {
        if (!StoreAccounts.IsStaff(User, configuration)) return Forbid();
        var found = await accounts.SetDisabledAsync(AccountId, disabled, cancellationToken);
        Message = found ? (disabled ? "Account disabled and sessions revoked." : "Account enabled; old sessions remain revoked.")
            : "Account ID was not found.";
        return Page();
    }
}
