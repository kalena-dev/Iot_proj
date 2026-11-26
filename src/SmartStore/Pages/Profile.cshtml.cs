using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SmartStore.Services;

namespace SmartStore.Pages;

public sealed class ProfileModel(StoreDemo store, ILogger<ProfileModel> logger) : PageModel
{
    public string Email { get; private set; } = "";
    public int Points { get; private set; }
    public int CouponCount { get; private set; }
    [TempData] public string? CouponMessage { get; set; }
    [TempData] public bool CouponError { get; set; }
    public IReadOnlyList<DemoSale> Sales { get; private set; } = [];
    public decimal TotalSpent { get; private set; }
    public int RecentPage { get; private set; }
    public int RecentPageCount { get; private set; }

    public async Task<IActionResult> OnGetAsync(int recentPage, CancellationToken cancellationToken)
    {
        if (StoreAccounts.EmailOf(User) is not { } email) return Challenge();

        Email = email;
        var points = store.CustomerPointsAsync(Email, cancellationToken);
        var sales = store.CustomerHistoryAsync(Email, cancellationToken, limit: 20);
        var spending = store.CustomerTotalSpentAsync(Email, cancellationToken);
        var coupons = store.GetCouponsAsync(Email, cancellationToken);
        await Task.WhenAll(points, sales, coupons, spending);
        Points = await points;
        var recent = await sales;
        RecentPageCount = Math.Max(1, (recent.Count + 3) / 4);
        RecentPage = Math.Clamp(recentPage, 1, RecentPageCount);
        Sales = recent.Skip((RecentPage - 1) * 4).Take(4).ToArray();
        TotalSpent = await spending;
        CouponCount = (await coupons).Count;
        return Page();
    }

    public async Task<IActionResult> OnPostRedeemCouponAsync(int percent, CancellationToken cancellationToken)
    {
        if (StoreAccounts.EmailOf(User) is not { } email) return Challenge();

        try
        {
            await store.RedeemCouponAsync(email, percent, cancellationToken);
            CouponMessage = $"Your {percent}% coupon is ready to use at self-checkout.";
            CouponError = false;
        }
        catch (InvalidOperationException exception)
        {
            CouponMessage = exception.Message;
            CouponError = true;
        }
        catch (MongoDB.Driver.MongoException exception)
        {
            logger.LogWarning(exception, "Cannot redeem loyalty coupon.");
            CouponMessage = "Your coupon could not be redeemed. Please try again.";
            CouponError = true;
        }
        return RedirectToPage();
    }
}
