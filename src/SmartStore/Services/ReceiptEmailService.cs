using System.Net;
using System.Net.Mail;
using System.Text;

namespace SmartStore.Services;

public sealed class ReceiptEmailService(IConfiguration config, ILogger<ReceiptEmailService> logger,
    AccountSecurityStore security)
{
    public string? Sender => config["SMTP_FROM"];

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
        if (sale.CouponPercent > 0)
        {
            text.AppendLine($"Subtotal: ${sale.Total + sale.CouponDiscount:F2} CAD");
            text.AppendLine($"Loyalty coupon ({sale.CouponPercent}%): -${sale.CouponDiscount:F2} CAD");
        }
        else
        {
            text.AppendLine("Loyalty coupon: None");
        }
        text.AppendLine($"TOTAL: ${sale.Total:F2} CAD");
        text.AppendLine($"Loyalty points earned: {sale.PointsEarned}");
        if (sale.PointsBalanceAfter is int balance) text.AppendLine($"Loyalty balance after purchase: {balance}");
        return text.ToString();
    }

    public Task<bool> SendAsync(DemoSale sale, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(sale.CustomerEmail) ? Task.FromResult(false)
            : SendMessageAsync(sale.CustomerEmail, $"Smart Store receipt {sale.Id[..8]}", FormatReceipt(sale), cancellationToken);

    public Task<bool> SendRegistrationCodeAsync(string email, string code, CancellationToken cancellationToken) =>
        SendMessageAsync(email, "Verify your Northstar Smart Store account",
            $"Enter this code in the browser where you started registration:\n\n{code}\n\nThe code expires after 15 minutes.",
            cancellationToken);

    public Task<bool> SendRecoveryCodeAsync(string email, string code, CancellationToken cancellationToken) =>
        SendMessageAsync(email, "Recover your Northstar Smart Store account",
            $"Enter this code in the browser where you requested recovery:\n\n{code}\n\nThe code expires after 15 minutes. If you did not request this, ignore this email.",
            cancellationToken);

    public Task<bool> SendStaffVerificationCodeAsync(string email, string code, CancellationToken cancellationToken) =>
        SendMessageAsync(email, "Verify your Northstar staff sign-in",
            $"Enter this code in the browser where you entered your password:\n\n{code}\n\nThe code expires after 15 minutes. Never share this code.",
            cancellationToken);

    private async Task<bool> SendMessageAsync(string recipient, string subject, string text,
        CancellationToken cancellationToken)
    {
        var normalizedRecipient = StoreAccounts.NormalizeEmail(recipient);
        if (normalizedRecipient is null) return false;
        if (!bool.TryParse(config["EMAIL_ENABLED"], out var enabled) || !enabled) return false;
        var host = config["SMTP_HOST"];
        var user = config["SMTP_USER"];
        var password = config["SMTP_PASSWORD"];
        var from = config["SMTP_FROM"];
        if (new[] { host, user, password, from }.Any(string.IsNullOrWhiteSpace)) return false;
        if (!await security.TrySendMailAsync(normalizedRecipient, cancellationToken)) return false;
        var smtpPort = int.TryParse(config["SMTP_PORT"], out var configuredPort) ? configuredPort : 587;
        // System.Net.Mail uses STARTTLS, not implicit TLS on port 465.
        if (smtpPort == 465) return false;
        try
        {
            using var client = new SmtpClient(host!, smtpPort)
            {
                EnableSsl = true,
                Credentials = new NetworkCredential(user, password)
            };
            using var message = new MailMessage(from!, normalizedRecipient, subject, text);
            await client.SendMailAsync(message, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is SmtpException or InvalidOperationException or ArgumentException or OperationCanceledException)
        {
            // SMTP exceptions can contain addresses and server responses; do not log their text.
            logger.LogWarning("SmartStore email delivery failed ({FailureType}).", exception.GetType().Name);
            return false;
        }
    }
}
