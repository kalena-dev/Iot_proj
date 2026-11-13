using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Microsoft.Extensions.Options;
using SmartStore.Options;
using SmartStore.Data;
using SmartStore.Models;

namespace SmartStore.Services;

public sealed record DemoProduct(string Code, string Name, decimal Price, int Stock, string Epc);
public sealed record DemoSaleLine(string Code, string Name, decimal Price, int Quantity);
[BsonIgnoreExtraElements]
public sealed class DemoSale
{
    [BsonId] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<DemoSaleLine> Items { get; set; } = [];
    public decimal Total { get; set; }
    public string PaymentStatus { get; set; } = "approved";
    public string? CustomerId { get; set; }
    public string? CustomerEmail { get; set; }
    public int PointsEarned { get; set; }
    public int? PointsBalanceAfter { get; set; }
    public string EmailStatus { get; set; } = "not_requested";
}

// Checkout and product management share the persistent inventory repository.
public sealed class StoreDemo
{
    private readonly IProductRepository _products;
    private readonly IMongoClient _client;
    private readonly ILogger<StoreDemo> _logger;
    private readonly IMongoCollection<DemoSale> _sales;
    private readonly IMongoCollection<Customer> _customers;
    private readonly MongoCustomerRepository _registry;
    private readonly IMongoCollection<EmailLog> _emails;

    public StoreDemo(IMongoClient client, IOptions<MongoDbOptions> options,
        IProductRepository products, MongoCustomerRepository registry, ILogger<StoreDemo> logger)
    {
        _client = client;
        _products = products;
        _registry = registry;
        _logger = logger;
        var db = client.GetDatabase(options.Value.DatabaseName);
        _emails = db.GetCollection<EmailLog>("email_log");
        _sales = db.GetCollection<DemoSale>("demo_sales");
        _customers = db.GetCollection<Customer>(options.Value.CustomersCollectionName);
    }

    public async Task<DemoSale?> FindSaleAsync(string id, CancellationToken cancellationToken) =>
        await _sales.Find(s => s.Id == id).FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<DemoProduct>> GetProductsAsync(CancellationToken cancellationToken)
    {
        var products = await _products.GetAllAsync(cancellationToken);
        return products.Select(p => new DemoProduct(p.Upc, p.Name, p.Price, p.QuantityInStock, p.Epc))
            .OrderBy(p => p.Code).ToList();
    }

    public async Task<DemoSale> CheckoutAsync(Dictionary<string, int> cart, string paymentResult,
        string? customerEmail, CancellationToken cancellationToken, string? receiptSender = null)
    {
        if (cart.Count is < 1 or > 20 || cart.Any(x => x.Value is < 1 or > 50))
            throw new InvalidOperationException("Add 1–20 different items with quantities between 1 and 50.");
        if (paymentResult is not ("approve" or "decline"))
            throw new InvalidOperationException("Choose a valid simulated payment result.");
        if (cart.Keys.Any(code => code.Trim().Length is < 1 or > 512))
            throw new InvalidOperationException("Enter a valid UPC or EPC.");

        var normalizedEmail = string.IsNullOrWhiteSpace(customerEmail) ? null : customerEmail.Trim().ToLowerInvariant();
        if (normalizedEmail is not null &&
            (normalizedEmail.Length > 254 ||
             !System.Net.Mail.MailAddress.TryCreate(normalizedEmail, out var address) ||
             !string.Equals(address.Address, normalizedEmail, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Enter a valid customer email address.");

        if (paymentResult == "approve" && normalizedEmail is not null)
            await _registry.EnsureIndexesAsync(cancellationToken);

        // Keep the identity stable if the driver retries the transaction callback.
        var saleId = Guid.NewGuid().ToString("N");
        var createdAtUtc = DateTime.UtcNow;
        try
        {
            return await MongoTransaction.RunAsync(_client, async (session, token) =>
            {
                // Retry-local state and reads use the same snapshot as every stock write.
                var productsById = new Dictionary<string, Product>();
                var quantitiesById = new Dictionary<string, int>();
                foreach (var (code, quantity) in cart)
                {
                    var scannedCode = code.Trim();
                    var product = (await _products.ResolveCodeAsync(scannedCode, token, session))?.Product
                        ?? throw new InvalidOperationException($"Unknown item code: {scannedCode}.");
                    var productId = product.Id ?? throw new InvalidOperationException("The product has no persistent identity.");
                    quantitiesById.TryGetValue(productId, out var previousQuantity);
                    var combinedQuantity = previousQuantity + quantity;
                    if (combinedQuantity > 50)
                        throw new InvalidOperationException("Each product is limited to 50 units per checkout.");
                    if (product.QuantityInStock < combinedQuantity)
                        throw new InvalidOperationException($"Not enough {product.Name} in stock.");
                    productsById[productId] = product;
                    quantitiesById[productId] = combinedQuantity;
                }
                var lines = quantitiesById.Select(item =>
                {
                    var product = productsById[item.Key];
                    return new DemoSaleLine(product.Upc, product.Name, product.Price, item.Value);
                }).ToList();

                var total = lines.Sum(x => x.Price * x.Quantity);
                var approved = paymentResult == "approve";
                var points = approved && normalizedEmail is not null ? checked((int)decimal.Floor(total)) : 0;
                Customer? customer = null;
                if (normalizedEmail is not null)
                {
                    customer = approved
                        ? await CreditAsync(session, normalizedEmail, points, createdAtUtc, token)
                        : await _customers.Find(session, c => c.Email == normalizedEmail,
                            new FindOptions { Collation = EmailCollation }).FirstOrDefaultAsync(token);
                }
                var sale = new DemoSale
                {
                    Id = saleId, CreatedAtUtc = createdAtUtc,
                    Items = lines, Total = total, PaymentStatus = approved ? "approved" : "declined",
                    CustomerId = customer?.Id, CustomerEmail = normalizedEmail,
                    PointsEarned = points, PointsBalanceAfter = customer?.TotalPoints,
                    EmailStatus = approved && normalizedEmail is not null ? "pending" : "not_requested"
                };
                if (approved)
                {
                    foreach (var (productId, quantity) in quantitiesById)
                    {
                        if (!await _products.DecrementStockAsync(session, productId, quantity, token))
                            throw new InvalidOperationException("Stock changed. Review your cart and try again.");
                    }
                }
                await _sales.InsertOneAsync(session, sale, cancellationToken: token);
                if (sale.EmailStatus == "pending")
                    await _emails.InsertOneAsync(session, ReceiptLog(sale, receiptSender), cancellationToken: token);
                return sale;
            }, "Checkout", saleId, cancellationToken);
        }
        catch (TransactionOutcomeUnknownException)
        {
            // A durable receipt proves all inventory and points writes committed with it.
            using var confirmation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try
            {
                var committed = await _sales.WithReadConcern(ReadConcern.Majority)
                    .WithReadPreference(ReadPreference.Primary)
                    .Find(s => s.Id == saleId).FirstOrDefaultAsync(confirmation.Token);
                if (committed is not null) return committed;
            }
            catch (Exception exception) when (exception is MongoException or TimeoutException or OperationCanceledException)
            {
                _logger.LogWarning("Cannot confirm checkout {SaleId}. Failure type: {FailureType}.",
                    saleId, exception.GetType().Name);
            }
            _logger.LogError("Checkout {SaleId} has an unknown commit outcome. No compensating writes were issued.", saleId);
            throw;
        }
    }

    public Task<List<DemoSale>> CustomerHistoryAsync(string email, CancellationToken cancellationToken) =>
        _sales.Find(s => s.CustomerEmail == email.Trim().ToLowerInvariant())
            .SortByDescending(s => s.CreatedAtUtc).Limit(100).ToListAsync(cancellationToken);

    private static readonly Collation EmailCollation = new("en", strength: CollationStrength.Secondary);

    private Task<Customer> CreditAsync(IClientSessionHandle session, string email, int points, DateTime created, CancellationToken token)
    {
        var update = Builders<Customer>.Update.SetOnInsert(c => c.Email, email)
            .SetOnInsert(c => c.CreatedAtUtc, created).SetOnInsert(c => c.CheckoutOnly, true)
            .Inc(c => c.TotalPoints, points);
        return _customers.FindOneAndUpdateAsync<Customer>(session, c => c.Email == email, update,
            new FindOneAndUpdateOptions<Customer> { IsUpsert = true, ReturnDocument = ReturnDocument.After, Collation = EmailCollation }, token);
    }

    private static EmailLog ReceiptLog(DemoSale sale, string? sender) => new()
    {
        Id = sale.Id, Kind = "receipt", Reference = sale.Id, Recipient = sale.CustomerEmail!,
        Sender = sender, Subject = $"Smart Store receipt {sale.Id[..8]}",
        Status = sale.EmailStatus, CreatedAtUtc = sale.CreatedAtUtc
    };

    public Task UpdateEmailStatusAsync(string saleId, string status, CancellationToken cancellationToken) =>
        MongoTransaction.RunAsync(_client, async (session, token) =>
        {
            await _emails.UpdateOneAsync(session, e => e.Id == saleId,
                Builders<EmailLog>.Update.Set(e => e.Status, status).Set(e => e.CompletedAtUtc, DateTime.UtcNow),
                cancellationToken: token);
            return await _sales.UpdateOneAsync(session, s => s.Id == saleId,
                Builders<DemoSale>.Update.Set(s => s.EmailStatus, status), cancellationToken: token);
        }, "Email status", saleId, cancellationToken);

    public async Task BackfillCheckoutRecordsAsync(CancellationToken token)
    {
        await _products.BackfillCodesAsync(token);
        await _registry.EnsureIndexesAsync(token);
        var db = _sales.Database;
        // A view gives Atlas users a stable transactions name without copying any orders.
        await db.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "create", "transactions" }, { "viewOn", _sales.CollectionNamespace.CollectionName }, { "pipeline", new BsonArray() }
        }, cancellationToken: token);
        var candidates = await _sales.Find(s => s.PaymentStatus == "approved" && s.CustomerId == null && s.CustomerEmail != null)
            .Project(s => s.Id).ToListAsync(token);
        foreach (var id in candidates)
            await MongoTransaction.RunAsync(_client, async (session, ct) =>
            {
                var sale = await _sales.Find(session, s => s.Id == id && s.CustomerId == null).FirstOrDefaultAsync(ct);
                if (sale is null || string.IsNullOrWhiteSpace(sale.CustomerEmail)) return false;
                var points = checked((int)decimal.Floor(sale.Total));
                var customer = await CreditAsync(session, sale.CustomerEmail.Trim().ToLowerInvariant(), points, sale.CreatedAtUtc, ct);
                await _sales.UpdateOneAsync(session, s => s.Id == id, Builders<DemoSale>.Update
                    .Set(s => s.CustomerId, customer.Id).Set(s => s.PointsEarned, points), cancellationToken: ct);
                return true;
            }, "Loyalty backfill", id, token);

        var alerts = db.GetCollection<BsonDocument>("fridge_alerts");
        foreach (var alert in await alerts.Find(new BsonDocument("emailState", "sent")).ToListAsync(token))
        {
            var reference = alert["_id"].AsString;
            if (await _emails.Find(e => e.Kind == "temperature-alert" && e.Reference == reference).AnyAsync(token)) continue;
            var created = System.Xml.XmlConvert.ToDateTime(alert["createdAt"].AsString, System.Xml.XmlDateTimeSerializationMode.Utc);
            await InsertHistoricalEmailAsync(new EmailLog
            {
                Id = "alert-" + reference, Kind = "temperature-alert", Reference = reference, Status = "sent",
                Recipient = alert["recipient"].AsString, CreatedAtUtc = created,
                Subject = $"{alert["fridge"]} temperature alert [Northstar:{reference}]"
            }, token);
        }

        var emailed = await _sales.Find(s => s.CustomerEmail != null &&
            (s.EmailStatus == "sent" || s.EmailStatus == "failed" || s.EmailStatus == "pending")).ToListAsync(token);
        foreach (var sale in emailed)
            await InsertHistoricalEmailAsync(ReceiptLog(sale, null), token);
    }

    private Task InsertHistoricalEmailAsync(EmailLog record, CancellationToken token)
    {
        var insert = Builders<EmailLog>.Update.SetOnInsert(e => e.Kind, record.Kind)
            .SetOnInsert(e => e.Reference, record.Reference).SetOnInsert(e => e.Status, record.Status)
            .SetOnInsert(e => e.Recipient, record.Recipient).SetOnInsert(e => e.Subject, record.Subject)
            .SetOnInsert(e => e.CreatedAtUtc, record.CreatedAtUtc);
        return _emails.UpdateOneAsync(e => e.Id == record.Id, insert, new UpdateOptions { IsUpsert = true }, token);
    }

    public Task<List<EmailLog>> ListEmailsAsync(int skip, int count, CancellationToken token) =>
        _emails.Find(FilterDefinition<EmailLog>.Empty).SortByDescending(e => e.CreatedAtUtc).ThenByDescending(e => e.Id)
            .Skip(skip).Limit(count).ToListAsync(token);

    public Task<List<DemoSale>> ListTransactionsAsync(int skip, int count, CancellationToken token) =>
        _sales.Find(FilterDefinition<DemoSale>.Empty).SortByDescending(s => s.CreatedAtUtc).ThenByDescending(s => s.Id)
            .Skip(skip).Limit(count).ToListAsync(token);

    public Task<List<DemoSale>> RecentSalesAsync(CancellationToken cancellationToken) =>
        _sales.Find(FilterDefinition<DemoSale>.Empty)
            .SortByDescending(s => s.CreatedAtUtc).Limit(20).ToListAsync(cancellationToken);
}
