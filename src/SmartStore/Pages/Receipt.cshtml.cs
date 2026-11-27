using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SmartStore.Services;

namespace SmartStore.Pages;

public sealed class ReceiptModel(StoreDemo store, ReceiptEmailService email, IConfiguration configuration) : PageModel
{
    public string ReceiptText { get; private set; } = "";

    public async Task<IActionResult> OnGetAsync(string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 40) return NotFound();
        var sale = await store.FindSaleAsync(id, cancellationToken);
        if (sale is null || sale.PaymentStatus != "approved") return NotFound();
        if (!StoreAccounts.IsStaff(User, configuration) &&
            (StoreAccounts.EmailOf(User) is not { } owner ||
             !string.Equals(sale.CustomerEmail, owner, StringComparison.OrdinalIgnoreCase)))
            return NotFound();

        ReceiptText = email.FormatReceipt(sale);
        return Page();
    }
}
