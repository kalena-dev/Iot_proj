using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using SmartStore.Services;

namespace SmartStore.Pages;

[EnableRateLimiting("Account")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class RegisterModel(StoreAccounts accounts, AccountSecurityStore security, ReceiptEmailService mailer) : PageModel
{
    private const string PendingKey = "PendingRegistration";
    [BindProperty] public string? FullName { get; set; }
    [BindProperty] public string? Email { get; set; }
    [BindProperty] public string? Password { get; set; }
    [BindProperty] public string? Code { get; set; }
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
        if (email is null) ModelState.AddModelError(nameof(Email), "Enter a valid email address.");
        if (string.IsNullOrWhiteSpace(FullName) || FullName.Trim().Length > 100)
            ModelState.AddModelError(nameof(FullName), "Enter your name (up to 100 characters).");
        if (Password is not { Length: >= 12 and <= 128 })
            ModelState.AddModelError(nameof(Password), "Use 12 to 128 characters.");
        if (!ModelState.IsValid) return Page();

        // Every accepted request gets the same response, including duplicates and exhausted quotas.
        var pendingId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        if (await security.TryUseQuotaAsync("registration", email!, 3, TimeSpan.FromHours(1), cancellationToken) &&
            await security.TryUseQuotaAsync("account-start", "all", 200, TimeSpan.FromHours(1), cancellationToken))
        {
            var candidate = accounts.Create(email!, Password!, FullName!);
            if (await accounts.GetByEmailAsync(email!, cancellationToken) is null)
            {
                var challenge = await security.CreateChallengeAsync("registration", email!, null, candidate, cancellationToken);
                pendingId = challenge.Id;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                if (!await mailer.SendRegistrationCodeAsync(email!, challenge.Code, timeout.Token))
                    await security.DeleteChallengeAsync(challenge.Id, cancellationToken);
            }
        }
        TempData[PendingKey] = pendingId;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostVerifyAsync(CancellationToken cancellationToken)
    {
        if (User.Identity?.IsAuthenticated == true) return RedirectToPage("/Profile");
        Pending = true;
        var id = TempData.Peek(PendingKey) as string;
        var challenge = id is null ? null :
            await security.ConsumeChallengeAsync(id, "registration", Code, cancellationToken);
        if (challenge?.Registration is not { } account || !await accounts.SaveAsync(account, cancellationToken))
        {
            ModelState.AddModelError("", "Unable to verify. Check the code, or start again, sign in or recover your account.");
            return Page();
        }
        TempData.Remove(PendingKey);
        await accounts.SignInAsync(HttpContext, account);
        return RedirectToPage("/Profile");
    }

    public async Task<IActionResult> OnPostRestartAsync(CancellationToken cancellationToken)
    {
        if (TempData.Peek(PendingKey) is string id) await security.DeleteChallengeAsync(id, cancellationToken);
        TempData.Remove(PendingKey);
        return RedirectToPage();
    }
}
