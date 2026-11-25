using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using SmartStore.Services;

namespace SmartStore.Pages;

[EnableRateLimiting("Account")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class RegisterModel(StoreAccounts accounts, ReceiptEmailService mailer, TimeProvider clock) : PageModel
{
    private const string PendingKey = "PendingAccount";
    private sealed record PendingAccount(StoreAccount Account, string Code, DateTimeOffset Expires);

    [BindProperty] public string? Email { get; set; }
    [BindProperty] public string? Password { get; set; }
    [BindProperty] public string? Code { get; set; }

    public string? PendingEmail { get; private set; }

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true) return RedirectToPage("/Profile");
        PendingEmail = ReadPending()?.Account.Email;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (User.Identity?.IsAuthenticated == true) return RedirectToPage("/Profile");
        var email = StoreAccounts.NormalizeEmail(Email);
        if (email is null) ModelState.AddModelError(nameof(Email), "Enter a valid email address.");
        if (Password is not { Length: >= 12 and <= 128 })
            ModelState.AddModelError(nameof(Password), "Use 12 to 128 characters.");
        if (!ModelState.IsValid) return Page();

        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        if (!await mailer.SendRegistrationCodeAsync(email!, code, timeout.Token))
        {
            ModelState.AddModelError("", "Verification email could not be sent. Try again or contact store staff.");
            return Page();
        }

        var pending = new PendingAccount(accounts.Create(email!, Password!), code, clock.GetUtcNow().AddMinutes(15));
        TempData[PendingKey] = JsonSerializer.Serialize(pending);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostVerifyAsync(CancellationToken cancellationToken)
    {
        if (User.Identity?.IsAuthenticated == true) return RedirectToPage("/Profile");
        var pending = ReadPending();
        if (pending is null)
        {
            ModelState.AddModelError("", "The verification code expired or the registration session ended. Start again.");
            return Page();
        }

        PendingEmail = pending.Account.Email;
        if (Code?.Trim().ToUpperInvariant() is not { Length: 32 } submitted ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(submitted), Encoding.UTF8.GetBytes(pending.Code)))
        {
            ModelState.AddModelError(nameof(Code), "Enter the verification code from your email.");
            return Page();
        }

        var created = await accounts.SaveAsync(pending.Account, cancellationToken);
        TempData.Remove(PendingKey);
        if (!created)
        {
            PendingEmail = null;
            ModelState.AddModelError("", "An account already exists for this email. Sign in to continue.");
            return Page();
        }

        await StoreAccounts.SignInAsync(HttpContext, pending.Account.Email);
        return RedirectToPage("/Profile");
    }

    public IActionResult OnPostRestart()
    {
        TempData.Remove(PendingKey);
        return RedirectToPage();
    }

    private PendingAccount? ReadPending()
    {
        if (TempData.Peek(PendingKey) is not string stored) return null;
        var pending = JsonSerializer.Deserialize<PendingAccount>(stored);
        if (pending is not null && pending.Expires > clock.GetUtcNow()) return pending;
        TempData.Remove(PendingKey);
        return null;
    }
}
