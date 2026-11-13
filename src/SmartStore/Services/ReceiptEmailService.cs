using System.Net;
using System.Net.Mail;
using System.Text;

namespace SmartStore.Services;

public sealed class ReceiptEmailService(IConfiguration config, ILogger<ReceiptEmailService> logger)
{
    public string FormatReceipt(DemoSale sale)
    {
        var text = new StringBuilder();
        text.AppendLine("SMART STORE RECEIPT");
        text.AppendLine($"Receipt: {sale.Id}");
        text.AppendLine($"Date (UTC): {sale.CreatedAtUtc:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine($"Payment: {sale.PaymentStatus}");
        text.AppendLine(new string('-', 40));
        foreach (var item in sale.Items)
            text.AppendLine($"{item.Name} ({item.Code}) x{item.Quantity} @ ${item.Price:F2} = ${item.Price * item.Quantity:F2}");
        text.AppendLine(new string('-', 40));
        text.AppendLine($"TOTAL: ${sale.Total:F2} CAD");
        text.AppendLine($"Loyalty points earned: {sale.PointsEarned}");
        return text.ToString();
    }

    public async Task<bool> SendAsync(DemoSale sale, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sale.CustomerEmail)) return false;
        if (!bool.TryParse(config["EMAIL_ENABLED"], out var enabled) || !enabled) return false;
        var host = config["SMTP_HOST"];
        var user = config["SMTP_USER"];
        var password = config["SMTP_PASSWORD"];
        var from = config["SMTP_FROM"];
        if (new[] { host, user, password, from }.Any(string.IsNullOrWhiteSpace)) return false;
        try
        {
            using var client = new SmtpClient(host!, int.TryParse(config["SMTP_PORT"], out var port) ? port : 587)
            {
                EnableSsl = true,
                Credentials = new NetworkCredential(user, password)
            };
            using var message = new MailMessage(from!, sale.CustomerEmail,
                $"Smart Store receipt {sale.Id[..8]}", FormatReceipt(sale));
            // SmtpClient has no cancellation-aware send overload.
            await client.SendMailAsync(message).WaitAsync(cancellationToken);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Receipt email delivery failed for sale {SaleId}", sale.Id);
            return false;
        }
    }
}
