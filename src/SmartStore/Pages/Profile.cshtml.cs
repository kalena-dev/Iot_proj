using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SmartStore.Services;

namespace SmartStore.Pages;

public sealed class ProfileModel(StoreDemo store, StoreAccounts accounts, ILogger<ProfileModel> logger) : PageModel
{
    public string Email { get; private set; } = "";
    public string AccountId { get; private set; } = "";
    public string FullName { get; private set; } = "";
    public string MembershipNumber { get; private set; } = "";
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
        if (StoreAccounts.AccountIdOf(User) is not { } accountId) return Challenge();
        var account = await accounts.GetByIdAsync(accountId, cancellationToken);
        if (account is null) return Challenge();
        AccountId = accountId;
        Email = account.Email;
        FullName = account.FullName;
        MembershipNumber = account.MembershipNumber;
        var points = store.CustomerPointsAsync(accountId, cancellationToken);
        var sales = store.CustomerHistoryPageAsync(accountId, recentPage, 4, null, null, null, cancellationToken);
        var spending = store.CustomerTotalSpentAsync(accountId, cancellationToken);
        var coupons = store.GetCouponsAsync(accountId, cancellationToken);
        await Task.WhenAll(points, sales, coupons, spending);
        Points = await points;
        var recent = await sales;
        RecentPageCount = (int)Math.Min(int.MaxValue, Math.Max(1, (recent.Count + 3) / 4));
        RecentPage = Math.Clamp(recentPage, 1, RecentPageCount);
        Sales = recent.Sales;
        TotalSpent = await spending;
        CouponCount = (await coupons).Count;
        return Page();
    }

    public async Task<IActionResult> OnPostRedeemCouponAsync(int percent, CancellationToken cancellationToken)
    {
        if (StoreAccounts.AccountIdOf(User) is not { } accountId) return Challenge();

        try
        {
            await store.RedeemCouponAsync(accountId, percent, cancellationToken);
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
