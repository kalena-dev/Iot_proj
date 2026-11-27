using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using SmartStore.Services;

namespace SmartStore.Pages;

[EnableRateLimiting("Account")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class LoginModel(StoreAccounts accounts, IConfiguration configuration) : PageModel
{
    [BindProperty, Required, EmailAddress, StringLength(254)]
    public string Email { get; set; } = "";

    [BindProperty, Required, StringLength(128)]
    public string Password { get; set; } = "";

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public IActionResult OnGet() => User.Identity?.IsAuthenticated == true
        ? RedirectToPage("/Profile") : Page();

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (User.Identity?.IsAuthenticated == true) return RedirectToPage("/Profile");
        if (!ModelState.IsValid) return Page();

        var email = StoreAccounts.NormalizeEmail(Email);
        if (email is null || !await accounts.VerifyPasswordAsync(email, Password, cancellationToken))
        {
            ModelState.AddModelError("", "Email or password is incorrect.");
            return Page();
        }

        await StoreAccounts.SignInAsync(HttpContext, email);
        if (Url.IsLocalUrl(ReturnUrl)) return LocalRedirect(ReturnUrl!);
        return RedirectToPage(email == StoreAccounts.NormalizeEmail(configuration["AUTH_ADMIN_EMAIL"])
            ? "/Management" : "/Profile");
    }
}
