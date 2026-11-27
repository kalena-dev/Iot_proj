using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Microsoft.Extensions.Options;
using SmartStore.Options;
using SmartStore.Data;
using SmartStore.Models;

namespace SmartStore.Services;

public sealed record DemoProduct(string ProductId, string Code, string Name, decimal Price, int Stock);
public sealed record DemoSaleLine(string Code, string Name,
    [property: BsonRepresentation(BsonType.Decimal128)] decimal Price, int Quantity)
{
    public string? ProductId { get; init; }
    public string IdentifierSource { get; init; } = "entered_identifier";
    public List<string> Epcs { get; init; } = [];
    [BsonIgnore] public string ProductKey => ProductId ?? "legacy:" + Code.ToUpperInvariant();
}
public sealed record CheckoutAttempt(string Id, string OwnerId, string? AccountId);
public sealed record SpendingBucket(DateTime StartUtc, decimal Total);
public sealed record SaleHistoryPage(List<DemoSale> Sales, long Count, decimal Total, string? MatchedProductId);
public sealed record ProductSalesTotal(string ProductKey, string Code, string Name, long Units);
public sealed record ReportDay(DateOnly Date, int Customers);
public sealed record SalesReport(decimal Revenue, List<ProductSalesTotal> Products, int Customers,
    int ReturningCustomers, List<ReportDay> Days);

public sealed class AccountBalance
{
    [BsonId] public string AccountId { get; set; } = "";
    public int Points { get; set; }
}
public sealed record LoyaltyReward(int Points, int Percent);
public sealed record LoyaltyCoupon([property: BsonId] string Id, string? Email, int Percent)
{
    public string? SaleId { get; init; }
    public string? AccountId { get; init; }
}

[BsonIgnoreExtraElements]
public sealed class DemoSale
{
    [BsonId] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<DemoSaleLine> Items { get; set; } = [];
    [BsonRepresentation(BsonType.Decimal128)]
    public decimal Total { get; set; }
    public int CouponPercent { get; set; }
    [BsonRepresentation(BsonType.Decimal128)]
    public decimal CouponDiscount { get; set; }
    public string PaymentStatus { get; set; } = "approved";
    public string? CustomerId { get; set; }
    public string? CustomerEmail { get; set; }
    public string? AccountId { get; set; }
    public string? ReceiptOwner { get; set; }
    public int PointsEarned { get; set; }
    public int? PointsBalanceAfter { get; set; }
    public string EmailStatus { get; set; } = "not_requested";
}

// Checkout and product management share the persistent inventory repository.
public sealed class StoreDemo
{
    public static IReadOnlyList<LoyaltyReward> Rewards { get; } =
        Array.AsReadOnly(new LoyaltyReward[] { new(250, 10), new(500, 25) });

    private readonly IProductRepository _products;
    private readonly IMongoClient _client;
    private readonly ILogger<StoreDemo> _logger;
    private readonly IMongoCollection<DemoSale> _sales;
    private readonly IMongoCollection<AccountBalance> _balances;
    private readonly IMongoCollection<EmailLog> _emails;
    private readonly IMongoCollection<LoyaltyCoupon> _coupons;
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private bool _indexesReady;

    public StoreDemo(IMongoClient client, IOptions<MongoDbOptions> options,
        IProductRepository products, ILogger<StoreDemo> logger)
    {
        _client = client;
        _products = products;
        _logger = logger;
        var db = client.GetDatabase(options.Value.DatabaseName);
        _emails = db.GetCollection<EmailLog>("email_log");
        _sales = db.GetCollection<DemoSale>("demo_sales");
        _balances = db.GetCollection<AccountBalance>("account_balances");
        _coupons = db.GetCollection<LoyaltyCoupon>("loyalty_coupons");
    }

    public Task<DemoSale?> FindSaleAsync(string id, CancellationToken token) =>
        _sales.Find(s => s.Id == id).FirstOrDefaultAsync(token)!;

    public async Task<IReadOnlyList<DemoProduct>> GetProductsAsync(CancellationToken token) =>
        (await _products.GetAllAsync(token))
            .Where(p => p.ArchivedAtUtc == null)
            .Select(p => new DemoProduct(p.Id!, p.Upc, p.Name, p.Price, p.QuantityInStock))
            .OrderBy(p => p.Code).ToList();

    public async Task<DemoSale> CheckoutAsync(Dictionary<string, int> cart, string paymentResult,
        string? customerEmail, CheckoutAttempt attempt, CancellationToken cancellationToken,
        string? receiptSender = null, string? couponId = null)
    {
        if (!Guid.TryParseExact(attempt.Id, "N", out _) || attempt.OwnerId.Length is < 16 or > 256 ||
            (attempt.AccountId is { } account && (!Guid.TryParseExact(account, "N", out _) || attempt.OwnerId != "account:" + account)) ||
            (attempt.AccountId is null && !attempt.OwnerId.StartsWith("guest:", StringComparison.Ordinal)))
            throw new InvalidOperationException("This checkout attempt is invalid. Reload checkout.");
        var saleId = Hash("checkout:" + attempt.OwnerId + ":" + attempt.Id)[..32];
        await EnsureIndexesAsync(cancellationToken);
        var original = await FindSaleAsync(saleId, cancellationToken);
        if (original is not null) return original;

        if (cart.Count is < 1 or > 100 || cart.Any(x => x.Value is < 1 or > 50))
            throw new InvalidOperationException("Add 1–100 scanned codes with quantities between 1 and 50.");
        if (paymentResult is not ("approve" or "decline"))
            throw new InvalidOperationException("Choose a valid simulated payment result.");
        if (cart.Keys.Any(code => code.Trim().Length is < 1 or > 512))
            throw new InvalidOperationException("Enter a valid UPC or EPC.");
        var normalizedEmail = string.IsNullOrWhiteSpace(customerEmail) ? null : customerEmail.Trim().ToLowerInvariant();
        if (normalizedEmail is not null && (normalizedEmail.Length > 254 ||
            !System.Net.Mail.MailAddress.TryCreate(normalizedEmail, out var address) ||
            !string.Equals(address.Address, normalizedEmail, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Enter a valid receipt email address.");

        // Initialize inventory indexes/migration before entering a transaction, never inside its callback.
        await _products.ResolveCodeAsync(cart.First().Key, cancellationToken);
        var createdAtUtc = DateTime.UtcNow;
        for (var duplicateRetry = 0; ; duplicateRetry++)
        {
            try
            {
                return await MongoTransaction.RunAsync(_client, async (session, token) =>
                {
                    var committed = await _sales.Find(session, s => s.Id == saleId).FirstOrDefaultAsync(token);
                    if (committed is not null) return committed;
                    var productsById = new Dictionary<string, Product>();
                    var quantitiesById = new Dictionary<string, int>();
                    var tagsById = new Dictionary<string, List<string>>();
                    foreach (var (code, quantity) in cart)
                    {
                        var scannedCode = code.Trim();
                        var resolved = await _products.ResolveCodeAsync(scannedCode, token, session)
                            ?? throw new InvalidOperationException($"Unknown or retired item code: {scannedCode}.");
                        var product = resolved.Product;
                        var productId = product.Id ?? throw new InvalidOperationException("The product has no persistent identity.");
                        if (resolved.IsTag)
                        {
                            if (quantity != 1) throw new InvalidOperationException("Each RFID tag is one unit. Enter every unit's own tag.");
                            var item = await _products.FindItemAsync(scannedCode, token, session);
                            if (!await _products.CanCheckoutItemAsync(item, token, session))
                                throw new InvalidOperationException($"Tag {scannedCode} is not available for checkout.");
                            if (!tagsById.TryGetValue(productId, out var tags)) tagsById[productId] = tags = [];
                            var epc = scannedCode.ToUpperInvariant();
                            if (tags.Contains(epc)) throw new InvalidOperationException($"Tag {epc} is in the cart twice.");
                            tags.Add(epc);
                        }
                        var combined = quantitiesById.GetValueOrDefault(productId) + quantity;
                        if (combined > 50) throw new InvalidOperationException("Each product is limited to 50 units per checkout.");
                        if (product.QuantityInStock < combined)
                            throw new InvalidOperationException($"Not enough {product.Name} in stock.");
                        productsById[productId] = product;
                        quantitiesById[productId] = combined;
                    }
                    if (productsById.Count > 20) throw new InvalidOperationException("Add at most 20 different products per checkout.");
                    var lines = quantitiesById.Select(entry =>
                    {
                        var product = productsById[entry.Key];
                        return new DemoSaleLine(product.Upc, product.Name, product.Price, entry.Value)
                        {
                            ProductId = entry.Key, Epcs = [.. (tagsById.GetValueOrDefault(entry.Key) ?? [])],
                            IdentifierSource = "entered_identifier"
                        };
                    }).ToList();
                    var approved = paymentResult == "approve";
                    LoyaltyCoupon? coupon = null;
                    if (!string.IsNullOrEmpty(couponId))
                    {
                        if (attempt.AccountId is null || !Guid.TryParseExact(couponId, "N", out _))
                            throw new InvalidOperationException("Choose a coupon from your account.");
                        var available = AvailableCoupons(attempt.AccountId) & Builders<LoyaltyCoupon>.Filter.Eq(c => c.Id, couponId);
                        coupon = approved
                            ? await _coupons.FindOneAndUpdateAsync(session, available,
                                Builders<LoyaltyCoupon>.Update.Set(c => c.SaleId, saleId), cancellationToken: token)
                            : await _coupons.Find(session, available).FirstOrDefaultAsync(token);
                        if (coupon is null) throw new InvalidOperationException("That coupon is no longer available for this account.");
                    }
                    var subtotal = lines.Sum(x => x.Price * x.Quantity);
                    var discount = decimal.Round(subtotal * (coupon?.Percent ?? 0) / 100, 2, MidpointRounding.AwayFromZero);
                    var total = subtotal - discount;
                    var points = approved && attempt.AccountId is not null ? checked((int)decimal.Floor(total)) : 0;
                    AccountBalance? balance = null;
                    if (attempt.AccountId is { } accountId)
                        balance = approved
                            ? await _balances.FindOneAndUpdateAsync<AccountBalance>(session, b => b.AccountId == accountId,
                                Builders<AccountBalance>.Update.Inc(b => b.Points, points),
                                new FindOneAndUpdateOptions<AccountBalance> { IsUpsert = true, ReturnDocument = ReturnDocument.After }, token)
                            : await _balances.Find(session, b => b.AccountId == accountId).FirstOrDefaultAsync(token);
                    var sale = new DemoSale
                    {
                        Id = saleId, CreatedAtUtc = createdAtUtc, Items = lines, Total = total,
                        PaymentStatus = approved ? "approved" : "declined", CouponPercent = coupon?.Percent ?? 0,
                        CouponDiscount = discount, AccountId = attempt.AccountId, CustomerEmail = normalizedEmail,
                        ReceiptOwner = attempt.AccountId is null ? Hash(attempt.OwnerId) : null,
                        PointsEarned = points, PointsBalanceAfter = attempt.AccountId is null ? null : balance?.Points ?? 0,
                        EmailStatus = approved && normalizedEmail is not null ? "pending" : "not_requested"
                    };
                    if (approved)
                        foreach (var (productId, quantity) in quantitiesById)
                            await _products.SellOldestFirstAsync(session, productId, quantity,
                                tagsById.GetValueOrDefault(productId) ?? [], saleId, token);
                    await _sales.InsertOneAsync(session, sale, cancellationToken: token);
                    if (sale.EmailStatus == "pending")
                        await _emails.InsertOneAsync(session, ReceiptLog(sale, receiptSender), cancellationToken: token);
                    return sale;
                }, "Checkout", saleId, cancellationToken);
            }
            catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
            {
                // Concurrent copies of this attempt converge on its committed sale. Concurrent first account
                // purchases can instead collide on balance creation; retry the complete aborted transaction.
                var committed = await FindSaleAsync(saleId, cancellationToken);
                if (committed is not null) return committed;
                if (duplicateRetry >= 2) throw;
            }
            catch (TransactionOutcomeUnknownException)
            {
                using var confirmation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                try
                {
                    var committed = await _sales.WithReadConcern(ReadConcern.Majority).WithReadPreference(ReadPreference.Primary)
                        .Find(s => s.Id == saleId).FirstOrDefaultAsync(confirmation.Token);
                    if (committed is not null) return committed;
                }
                catch (Exception exception) when (exception is MongoException or TimeoutException or OperationCanceledException)
                {
                    _logger.LogWarning("Cannot confirm checkout {SaleId}; failure type {FailureType}.", saleId, exception.GetType().Name);
                }
                throw;
            }
        }
    }

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static FilterDefinition<DemoSale> HistoryFilter(string accountId, DateTime? startUtc, DateTime? endUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        var f = Builders<DemoSale>.Filter;
        var filter = f.Eq(s => s.AccountId, accountId) & f.Eq(s => s.PaymentStatus, "approved");
        if (startUtc is { } start) filter &= f.Gte(s => s.CreatedAtUtc, start);
        if (endUtc is { } end) filter &= f.Lt(s => s.CreatedAtUtc, end);
        return filter;
    }

    public async Task<SaleHistoryPage> CustomerHistoryPageAsync(string accountId, int page, int pageSize,
        string? product, DateTime? startUtc, DateTime? endUtc, CancellationToken token)
    {
        var period = HistoryFilter(accountId, startUtc, endUtc);
        var filter = period;
        string? matchedProductId = null;
        if (!string.IsNullOrWhiteSpace(product))
        {
            var search = product.Trim();
            if (search.Length > 200) throw new ArgumentException("Use at most 200 characters to find a product.");
            matchedProductId = (await _products.ResolveCodeAsync(search, token))?.Product.Id
                ?? (await _products.GetByIdAsync(search, token))?.Id
                ?? (await _products.FindItemAsync(search, token))?.ProductId;
            var line = Builders<DemoSaleLine>.Filter;
            var byCode = line.Regex(i => i.Code,
                new BsonRegularExpression("^" + System.Text.RegularExpressions.Regex.Escape(search) + "$", "i"));
            if (matchedProductId is not null)
                byCode &= line.Eq(i => i.ProductId, null);
            var match = line.Regex(i => i.Name,
                new BsonRegularExpression(System.Text.RegularExpressions.Regex.Escape(search), "i")) |
                byCode | line.Eq(i => i.ProductId, search);
            if (matchedProductId is not null) match |= line.Eq(i => i.ProductId, matchedProductId);
            filter &= Builders<DemoSale>.Filter.ElemMatch(s => s.Items, match);
        }
        var size = Math.Clamp(pageSize, 1, 50);
        var countTask = _sales.CountDocumentsAsync(filter, cancellationToken: token);
        var totalTask = _sales.Aggregate().Match(period).AppendStage<BsonDocument>(TotalStage()).FirstOrDefaultAsync(token);
        await Task.WhenAll(countTask, totalTask);
        var count = await countTask;
        var actualPage = Math.Clamp((long)page, 1, Math.Max(1, (count + size - 1) / size));
        var skip = (actualPage - 1) * size;
        if (skip > int.MaxValue) throw new ArgumentException("Choose an earlier history page.");
        var sales = await _sales.Find(filter).SortByDescending(s => s.CreatedAtUtc).ThenByDescending(s => s.Id)
            .Skip((int)skip).Limit(size).ToListAsync(token);
        return new(sales, count, (await totalTask)?["total"].ToDecimal() ?? 0, matchedProductId);
    }

    public async Task<decimal> CustomerTotalSpentAsync(string accountId, CancellationToken token) =>
        (await _sales.Aggregate().Match(HistoryFilter(accountId, null, null))
            .AppendStage<BsonDocument>(TotalStage()).FirstOrDefaultAsync(token))?["total"].ToDecimal() ?? 0;

    private static BsonDocument TotalStage() => new("$group", new BsonDocument
    {
        { "_id", 1 }, { "total", new BsonDocument("$sum", new BsonDocument("$toDecimal", "$Total")) }
    });

    public Task<int> CustomerPointsAsync(string accountId, CancellationToken token) =>
        _balances.Find(b => b.AccountId == accountId).Project(b => b.Points).FirstOrDefaultAsync(token);

    private static FilterDefinition<LoyaltyCoupon> AvailableCoupons(string accountId) =>
        Builders<LoyaltyCoupon>.Filter.Where(c => c.AccountId == accountId && c.SaleId == null);

    public Task<List<LoyaltyCoupon>> GetCouponsAsync(string accountId, CancellationToken token) =>
        _coupons.Find(AvailableCoupons(accountId)).SortByDescending(c => c.Percent).ThenBy(c => c.Id).ToListAsync(token);

    public async Task RedeemCouponAsync(string accountId, int percent, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        var reward = Rewards.SingleOrDefault(r => r.Percent == percent)
            ?? throw new InvalidOperationException("Choose an available loyalty reward.");
        var coupon = new LoyaltyCoupon(Guid.NewGuid().ToString("N"), null, reward.Percent) { AccountId = accountId };
        await MongoTransaction.RunAsync(_client, async (session, ct) =>
        {
            var debit = await _balances.UpdateOneAsync(session, b => b.AccountId == accountId && b.Points >= reward.Points,
                Builders<AccountBalance>.Update.Inc(b => b.Points, -reward.Points), cancellationToken: ct);
            if (debit.ModifiedCount != 1) throw new InvalidOperationException($"You need {reward.Points} loyalty points for this coupon.");
            await _coupons.InsertOneAsync(session, coupon, cancellationToken: ct);
            return coupon;
        }, "Coupon redemption", coupon.Id, token);
    }

    // Pending -> sending is a durable single sender claim. A lost SMTP acknowledgement is never auto-retried.
    public Task<bool> ClaimReceiptEmailAsync(string saleId, CancellationToken token) =>
        MongoTransaction.RunAsync(_client, async (session, ct) =>
        {
            var sale = await _sales.UpdateOneAsync(session, s => s.Id == saleId && s.EmailStatus == "pending",
                Builders<DemoSale>.Update.Set(s => s.EmailStatus, "sending"), cancellationToken: ct);
            if (sale.ModifiedCount != 1) return false;
            await _emails.UpdateOneAsync(session, e => e.Id == saleId,
                Builders<EmailLog>.Update.Set(e => e.Status, "sending"), cancellationToken: ct);
            return true;
        }, "Receipt email claim", saleId, token);

    private static EmailLog ReceiptLog(DemoSale sale, string? sender) => new()
    {
        Id = sale.Id, Kind = "receipt", Reference = sale.Id, Recipient = sale.CustomerEmail!, Sender = sender,
        Subject = $"Smart Store receipt {sale.Id[..8]}", Status = sale.EmailStatus, CreatedAtUtc = sale.CreatedAtUtc
    };

    public Task UpdateEmailStatusAsync(string saleId, string status, CancellationToken token)
    {
        if (status is not ("sent" or "failed")) throw new ArgumentException("Invalid email status.", nameof(status));
        return MongoTransaction.RunAsync(_client, async (session, ct) =>
        {
            var sale = await _sales.UpdateOneAsync(session, s => s.Id == saleId && s.EmailStatus == "sending",
                Builders<DemoSale>.Update.Set(s => s.EmailStatus, status), cancellationToken: ct);
            if (sale.ModifiedCount != 1) return false;
            await _emails.UpdateOneAsync(session, e => e.Id == saleId,
                Builders<EmailLog>.Update.Set(e => e.Status, status).Set(e => e.CompletedAtUtc, DateTime.UtcNow), cancellationToken: ct);
            return true;
        }, "Email status", saleId, token);
    }

    public async Task BackfillCheckoutRecordsAsync(CancellationToken token)
    {
        await _products.BackfillCodesAsync(token);
        await EnsureIndexesAsync(token);
        // Existing receipts, customer balances and email-only coupons remain unclaimed historical data.
        try
        {
            await _sales.Database.RunCommandAsync<BsonDocument>(new BsonDocument
            {
                { "create", "transactions" }, { "viewOn", _sales.CollectionNamespace.CollectionName }, { "pipeline", new BsonArray() }
            }, cancellationToken: token);
        }
        catch (MongoCommandException exception) when (exception.CodeName == "NamespaceExists") { }
        using var cursor = await _sales.Find(s => s.CustomerEmail != null &&
            (s.EmailStatus == "sent" || s.EmailStatus == "failed" || s.EmailStatus == "pending" || s.EmailStatus == "sending"))
            .ToCursorAsync(token);
        while (await cursor.MoveNextAsync(token))
            foreach (var sale in cursor.Current)
            {
                var record = ReceiptLog(sale, null);
                await _emails.UpdateOneAsync(e => e.Id == record.Id, Builders<EmailLog>.Update
                    .SetOnInsert(e => e.Kind, record.Kind).SetOnInsert(e => e.Reference, record.Reference)
                    .SetOnInsert(e => e.Status, record.Status).SetOnInsert(e => e.Recipient, record.Recipient)
                    .SetOnInsert(e => e.Subject, record.Subject).SetOnInsert(e => e.CreatedAtUtc, record.CreatedAtUtc),
                    new UpdateOptions { IsUpsert = true }, token);
            }
    }

    public Task<List<EmailLog>> ListEmailsAsync(int skip, int count, CancellationToken token) =>
        _emails.Find(FilterDefinition<EmailLog>.Empty).SortByDescending(e => e.CreatedAtUtc).ThenByDescending(e => e.Id)
            .Skip(Math.Max(0, skip)).Limit(Math.Clamp(count, 1, 100)).ToListAsync(token);

    public Task<List<DemoSale>> ListTransactionsAsync(int skip, int count, CancellationToken token) =>
        _sales.Find(FilterDefinition<DemoSale>.Empty).SortByDescending(s => s.CreatedAtUtc).ThenByDescending(s => s.Id)
            .Skip(Math.Max(0, skip)).Limit(Math.Clamp(count, 1, 100)).ToListAsync(token);

    public Task<List<DemoSale>> RecentSalesAsync(CancellationToken token) => ListTransactionsAsync(0, 20, token);

    public async Task<List<SpendingBucket>> CustomerSpendingAsync(string accountId, DateTime startUtc, DateTime endUtc, CancellationToken token)
    {
        ValidatePeriod(startUtc, endUtc);
        var rows = await _sales.Aggregate().Match(HistoryFilter(accountId, startUtc, endUtc))
            .AppendStage<BsonDocument>(new BsonDocument("$group", new BsonDocument
            {
                { "_id", new BsonDocument("$dateTrunc", new BsonDocument { { "date", "$CreatedAtUtc" }, { "unit", "hour" } }) },
                { "total", new BsonDocument("$sum", new BsonDocument("$toDecimal", "$Total")) }
            })).Sort(new BsonDocument("_id", 1)).ToListAsync(token);
        return rows.Select(row => new SpendingBucket(row["_id"].ToUniversalTime(), row["total"].ToDecimal())).ToList();
    }

    public async Task<SalesReport> ReportAsync(DateTime startUtc, DateTime endUtc, CancellationToken token)
    {
        ValidatePeriod(startUtc, endUtc);
        await EnsureIndexesAsync(token);
        var period = new BsonDocument { { "PaymentStatus", "approved" },
            { "CreatedAtUtc", new BsonDocument { { "$gte", startUtc }, { "$lt", endUtc } } } };
        var rows = await _sales.Aggregate().Match(period).AppendStage<BsonDocument>(new BsonDocument("$facet", new BsonDocument
        {
            { "revenue", new BsonArray { TotalStage() } },
            { "products", new BsonArray
                {
                    new BsonDocument("$sort", new BsonDocument { { "CreatedAtUtc", 1 }, { "_id", 1 } }),
                    new BsonDocument("$unwind", "$Items"),
                    new BsonDocument("$group", new BsonDocument
                    {
                        { "_id", new BsonDocument("$ifNull", new BsonArray { "$Items.ProductId",
                            new BsonDocument("$concat", new BsonArray { "legacy:", new BsonDocument("$toUpper", "$Items.Code") }) }) },
                        { "code", new BsonDocument("$last", "$Items.Code") }, { "name", new BsonDocument("$last", "$Items.Name") },
                        { "units", new BsonDocument("$sum", "$Items.Quantity") }
                    })
                } },
            { "days", new BsonArray
                {
                    new BsonDocument("$project", new BsonDocument { { "owner", ReportOwner() }, { "day",
                        new BsonDocument("$dateToString", new BsonDocument { { "format", "%Y-%m-%d" }, { "date", "$CreatedAtUtc" }, { "timezone", ReportTimeZone() } }) } }),
                    new BsonDocument("$match", new BsonDocument("owner", new BsonDocument("$ne", BsonNull.Value))),
                    new BsonDocument("$group", new BsonDocument("_id", new BsonDocument { { "owner", "$owner" }, { "day", "$day" } })),
                    new BsonDocument("$group", new BsonDocument { { "_id", "$_id.day" }, { "count", new BsonDocument("$sum", 1) } })
                } }
        })).SingleAsync(token);
        var customers = await _sales.Aggregate().Match(new BsonDocument { { "PaymentStatus", "approved" },
                { "CreatedAtUtc", new BsonDocument("$lt", endUtc) } })
            .AppendStage<BsonDocument>(new BsonDocument("$project", new BsonDocument { { "owner", ReportOwner() }, { "created", "$CreatedAtUtc" } }))
            .Match(new BsonDocument("owner", new BsonDocument("$ne", BsonNull.Value)))
            .AppendStage<BsonDocument>(new BsonDocument("$group", new BsonDocument { { "_id", "$owner" },
                { "first", new BsonDocument("$min", "$created") }, { "last", new BsonDocument("$max", "$created") } }))
            .Match(new BsonDocument("last", new BsonDocument("$gte", startUtc)))
            .AppendStage<BsonDocument>(new BsonDocument("$group", new BsonDocument { { "_id", 1 },
                { "count", new BsonDocument("$sum", 1) }, { "returning", new BsonDocument("$sum",
                    new BsonDocument("$cond", new BsonArray { new BsonDocument("$lt", new BsonArray { "$first", startUtc }), 1, 0 })) } }))
            .FirstOrDefaultAsync(token);
        var revenue = rows["revenue"].AsBsonArray;
        return new(revenue.Count == 0 ? 0 : revenue[0]["total"].ToDecimal(),
            rows["products"].AsBsonArray.Select(p => new ProductSalesTotal(p["_id"].AsString, p["code"].AsString,
                p["name"].AsString, p["units"].ToInt64())).ToList(),
            customers?["count"].ToInt32() ?? 0, customers?["returning"].ToInt32() ?? 0,
            rows["days"].AsBsonArray.Select(d => new ReportDay(DateOnly.ParseExact(d["_id"].AsString, "yyyy-MM-dd"), d["count"].ToInt32())).ToList());
    }

    private static BsonValue ReportOwner() => new BsonDocument("$ifNull", new BsonArray { "$AccountId",
        new BsonDocument("$ifNull", new BsonArray { "$CustomerId",
            new BsonDocument("$cond", new BsonArray { new BsonDocument("$ne", new BsonArray {
                new BsonDocument("$ifNull", new BsonArray { "$CustomerEmail", BsonNull.Value }), BsonNull.Value }),
                new BsonDocument("$concat", new BsonArray { "legacy:", new BsonDocument("$toLower", "$CustomerEmail") }), BsonNull.Value }) }) });

    private static string ReportTimeZone() => TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var iana)
        ? iana : TimeZoneInfo.Local.Id;

    private static void ValidatePeriod(DateTime start, DateTime end)
    {
        if (start >= end || end - start > TimeSpan.FromDays(367))
            throw new ArgumentException("Choose a report period of at most one year.");
    }

    private async Task EnsureIndexesAsync(CancellationToken token)
    {
        if (_indexesReady) return;
        await _indexLock.WaitAsync(token);
        try
        {
            if (_indexesReady) return;
            await _sales.Indexes.CreateManyAsync([
                new CreateIndexModel<DemoSale>(Builders<DemoSale>.IndexKeys.Ascending(s => s.AccountId).Ascending(s => s.PaymentStatus)
                    .Descending(s => s.CreatedAtUtc).Descending(s => s.Id), new CreateIndexOptions { Name = "ix_sales_account_history" }),
                new CreateIndexModel<DemoSale>(Builders<DemoSale>.IndexKeys.Ascending(s => s.PaymentStatus).Ascending(s => s.CreatedAtUtc),
                    new CreateIndexOptions { Name = "ix_sales_reports" })
            ], cancellationToken: token);
            await _coupons.Indexes.CreateOneAsync(new CreateIndexModel<LoyaltyCoupon>(Builders<LoyaltyCoupon>.IndexKeys
                .Ascending(c => c.AccountId).Ascending(c => c.SaleId)), cancellationToken: token);
            _indexesReady = true;
        }
        finally { _indexLock.Release(); }
    }
}

public static class CheckoutAccess
{
    private const string OwnerCookie = "northstar.checkout-owner";

    public static string Owner(HttpContext context, bool create)
    {
        if (StoreAccounts.AccountIdOf(context.User) is { } accountId) return "account:" + accountId;
        var cookie = context.Request.Cookies[OwnerCookie];
        if (cookie is { Length: 64 } && cookie.All(Uri.IsHexDigit)) return "guest:" + cookie;
        if (!create) return "";
        cookie = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        context.Response.Cookies.Append(OwnerCookie, cookie, new CookieOptions
        {
            HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, IsEssential = true, MaxAge = TimeSpan.FromHours(8)
        });
        return "guest:" + cookie;
    }

    private static string GuestOwner(HttpContext context)
    {
        var cookie = context.Request.Cookies[OwnerCookie];
        return cookie is { Length: 64 } && cookie.All(Uri.IsHexDigit) ? "guest:" + cookie : "";
    }

    public static bool Owns(HttpContext context, DemoSale sale) =>
        sale.AccountId is { } accountId ? StoreAccounts.AccountIdOf(context.User) == accountId :
        sale.ReceiptOwner is { } receiptOwner && GuestOwner(context) is { Length: > 0 } owner &&
            CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(receiptOwner), Encoding.UTF8.GetBytes(StoreDemo.Hash(owner)));
}
