using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using SmartStore.Services;

namespace SmartStore.Pages;

[EnableRateLimiting("Account")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class RecoverModel(StoreAccounts accounts, AccountSecurityStore security, ReceiptEmailService mailer) : PageModel
{
    private const string PendingKey = "PendingRecovery";
    [BindProperty] public string? Email { get; set; }
    [BindProperty] public string? Code { get; set; }
    [BindProperty] public string? Password { get; set; }
    public bool Pending { get; private set; }
    public bool Completed { get; private set; }

    public void OnGet() => Pending = TempData.Peek(PendingKey) is string;

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var email = StoreAccounts.NormalizeEmail(Email);
        if (email is null)
        {
            ModelState.AddModelError(nameof(Email), "Enter a valid email address.");
            return Page();
        }
        var pendingId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        if (await security.TryUseQuotaAsync("recovery", email, 3, TimeSpan.FromHours(1), cancellationToken) &&
            await security.TryUseQuotaAsync("account-start", "all", 200, TimeSpan.FromHours(1), cancellationToken))
        {
            var account = await accounts.GetByEmailAsync(email, cancellationToken);
            if (account is { Disabled: false })
            {
                var challenge = await security.CreateChallengeAsync("recovery", email, account, null, cancellationToken);
                pendingId = challenge.Id;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                if (!await mailer.SendRecoveryCodeAsync(email, challenge.Code, timeout.Token))
                    await security.DeleteChallengeAsync(challenge.Id, cancellationToken);
            }
        }
        TempData[PendingKey] = pendingId;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostVerifyAsync(CancellationToken cancellationToken)
    {
        Pending = true;
        if (Password is not { Length: >= 12 and <= 128 })
        {
            ModelState.AddModelError(nameof(Password), "Use 12 to 128 characters.");
            return Page();
        }
        var id = TempData.Peek(PendingKey) as string;
        var challenge = id is null ? null : await security.ConsumeChallengeAsync(id, "recovery", Code, cancellationToken);
        if (challenge is null || !await accounts.ResetPasswordAsync(challenge, Password, cancellationToken))
        {
            ModelState.AddModelError("", "Unable to recover the account. Check the code or start again.");
            return Page();
        }
        TempData.Remove(PendingKey);
        Pending = false;
        Completed = true;
        return Page();
    }

    public async Task<IActionResult> OnPostRestartAsync(CancellationToken cancellationToken)
    {
        if (TempData.Peek(PendingKey) is string id) await security.DeleteChallengeAsync(id, cancellationToken);
        TempData.Remove(PendingKey);
        return RedirectToPage();
    }
}
