using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using SmartStore.Services;

namespace SmartStore.Pages;

[EnableRateLimiting("Account")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class LoginModel(StoreAccounts accounts, AccountSecurityStore security,
    ReceiptEmailService mailer, IConfiguration configuration) : PageModel
{
    private const string PendingKey = "PendingStaffLogin";
    [BindProperty] public string Email { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty] public string? Code { get; set; }
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    public bool Pending { get; private set; }

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true) return RedirectToPage("/Profile");
        Pending = TempData.Peek(PendingKey) is string;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (User.Identity?.IsAuthenticated == true) return RedirectToPage("/Profile");
        var email = StoreAccounts.NormalizeEmail(Email);
        var account = email is not null && Password is { Length: > 0 and <= 128 }
            ? await accounts.AuthenticateAsync(email, Password, cancellationToken) : null;
        if (account is null)
        {
            ModelState.AddModelError("", "Unable to sign in. Check your credentials or try again later.");
            return Page();
        }
        if (account.StaffProvisioned && StoreAccounts.IsProvisionedId(account.AccountId, configuration))
        {
            var challenge = await security.CreateChallengeAsync("staff-login", account.Email, account, null, cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            if (!await mailer.SendStaffVerificationCodeAsync(account.Email, challenge.Code, timeout.Token))
                await security.DeleteChallengeAsync(challenge.Id, cancellationToken);
            TempData[PendingKey] = challenge.Id;
            return RedirectToPage(new { ReturnUrl = Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : null });
        }
        await accounts.SignInAsync(HttpContext, account);
        return Destination(false);
    }

    public async Task<IActionResult> OnPostVerifyAsync(CancellationToken cancellationToken)
    {
        if (User.Identity?.IsAuthenticated == true) return RedirectToPage("/Profile");
        Pending = true;
        var id = TempData.Peek(PendingKey) as string;
        var challenge = id is null ? null :
            await security.ConsumeChallengeAsync(id, "staff-login", Code, cancellationToken);
        var account = challenge?.AccountId is { } accountId ? await accounts.GetByIdAsync(accountId, cancellationToken) : null;
        if (account is null || account.Disabled || !account.StaffProvisioned ||
            !StoreAccounts.IsProvisionedId(account.AccountId, configuration) ||
            account.SecurityStamp != challenge!.SecurityStamp)
        {
            ModelState.AddModelError("", "Unable to verify. Check the code or start sign-in again.");
            return Page();
        }
        TempData.Remove(PendingKey);
        await accounts.SignInAsync(HttpContext, account, staffVerified: true);
        return Destination(true);
    }

    public async Task<IActionResult> OnPostRestartAsync(CancellationToken cancellationToken)
    {
        if (TempData.Peek(PendingKey) is string id) await security.DeleteChallengeAsync(id, cancellationToken);
        TempData.Remove(PendingKey);
        return RedirectToPage();
    }

    private IActionResult Destination(bool staff) => Url.IsLocalUrl(ReturnUrl)
        ? LocalRedirect(ReturnUrl!) : RedirectToPage(staff ? "/Management" : "/Profile");
}
