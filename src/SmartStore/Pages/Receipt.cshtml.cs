using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MongoDB.Driver;
using Microsoft.Extensions.Options;
using SmartStore.Options;
using SmartStore.Services;

namespace SmartStore.Pages;
public sealed class ReceiptModel(IMongoClient client, IOptions<MongoDbOptions> options, ReceiptEmailService email) : PageModel
{
    public string ReceiptText { get; private set; } = "";
    public async Task<IActionResult> OnGetAsync(string id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 40) return NotFound();
        var sale = await client.GetDatabase(options.Value.DatabaseName)
            .GetCollection<DemoSale>("demo_sales").Find(s => s.Id == id && s.PaymentStatus == "approved")
            .FirstOrDefaultAsync(ct);
        if (sale is null) return NotFound();
        ReceiptText = email.FormatReceipt(sale);
        return Page();
    }
}
