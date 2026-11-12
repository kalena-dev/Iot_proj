using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Microsoft.Extensions.Options;
using SmartStore.Options;
using SmartStore.Data;
using SmartStore.Models;

namespace SmartStore.Services;

public sealed record DemoProduct(string Code, string Name, decimal Price, int Stock, string Epc);
public sealed record DemoSaleLine(string Code, string Name, decimal Price, int Quantity);
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

    public StoreDemo(IMongoClient client, IOptions<MongoDbOptions> options,
        IProductRepository products, ILogger<StoreDemo> logger)
    {
        _client = client;
        _products = products;
        _logger = logger;
        var db = client.GetDatabase(options.Value.DatabaseName);
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
        string? customerEmail, CancellationToken cancellationToken)
    {
        if (cart.Count is < 1 or > 20 || cart.Any(x => x.Value is < 1 or > 50))
            throw new InvalidOperationException("Add 1–20 different items with quantities between 1 and 50.");
        if (paymentResult is not ("approve" or "decline"))
            throw new InvalidOperationException("Choose a valid simulated payment result.");

        var productsById = new Dictionary<string, Product>();
        var quantitiesById = new Dictionary<string, int>();
        foreach (var (code, quantity) in cart)
        {
            var scannedCode = code.Trim();
            if (scannedCode.Length is < 1 or > 24)
                throw new InvalidOperationException("Enter a valid UPC or EPC.");
            var product = await _products.FindByUpcAsync(scannedCode, cancellationToken)
                ?? await _products.FindByEpcAsync(scannedCode.ToUpperInvariant(), cancellationToken)
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

        Customer? customer = null;
        if (!string.IsNullOrWhiteSpace(customerEmail))
        {
            customerEmail = customerEmail.Trim().ToLowerInvariant();
            if (customerEmail.Length > 254 ||
                !System.Net.Mail.MailAddress.TryCreate(customerEmail, out var address) ||
                !string.Equals(address.Address, customerEmail, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Enter a valid customer email address.");
            customer = await _customers.Find(c => c.Email == customerEmail,
                    new FindOptions { Collation = new Collation("en", strength: CollationStrength.Secondary) })
                .FirstOrDefaultAsync(cancellationToken);
            if (customer is null)
                throw new InvalidOperationException("Customer not found. Register the email first or leave it blank for guest checkout.");
        }
        var total = lines.Sum(x => x.Price * x.Quantity);
        var approved = paymentResult == "approve";
        var sale = new DemoSale
        {
            Items = lines, Total = total, PaymentStatus = approved ? "approved" : "declined",
            CustomerId = customer?.Id, CustomerEmail = customer?.Email.Trim().ToLowerInvariant(),
            PointsEarned = approved && customer is not null ? checked((int)decimal.Floor(total)) : 0,
            EmailStatus = approved && customer is not null ? "pending" : "not_requested"
        };
        if (!approved)
        {
            // A declined attempt is retained, but never reserves stock or changes points.
            await _sales.InsertOneAsync(sale, cancellationToken: cancellationToken);
            return sale;
        }

        var stock = await _products.DecrementStockManyAsync(quantitiesById, cancellationToken);
        if (!stock.Succeeded)
            throw new InvalidOperationException(stock.Message ?? "Stock changed. Review your cart and try again.");
        try
        {
            using var session = await _client.StartSessionAsync(cancellationToken: cancellationToken);
            // Atlas commits the receipt and loyalty increment together, or neither.
            return await session.WithTransactionAsync(async (transaction, token) =>
            {
                if (customer is not null && sale.PointsEarned > 0)
                {
                    var update = await _customers.UpdateOneAsync(transaction, c => c.Id == customer.Id,
                        Builders<Customer>.Update.Inc(c => c.TotalPoints, sale.PointsEarned),
                        cancellationToken: token);
                    if (update.MatchedCount != 1)
                        throw new InvalidOperationException("Customer no longer exists. Register again or use guest checkout.");
                }
                await _sales.InsertOneAsync(transaction, sale, cancellationToken: token);
                return sale;
            }, new TransactionOptions(writeConcern: WriteConcern.WMajority), cancellationToken);
        }
        catch (Exception exception)
        {
            // A lost commit acknowledgement is not proof of failure. Check before returning stock.
            DemoSale? committed;
            try
            {
                committed = await _sales.WithReadConcern(ReadConcern.Majority)
                    .Find(s => s.Id == sale.Id).FirstOrDefaultAsync(CancellationToken.None);
            }
            catch (Exception confirmationException)
            {
                _logger.LogError("Cannot confirm checkout {SaleId}; stock reservation retained. Failure types: {FailureType}, {ConfirmationType}.",
                    sale.Id, exception.GetType().Name, confirmationException.GetType().Name);
                throw new InvalidOperationException(
                    $"Checkout status is unknown. Do not retry payment; check receipt {sale.Id} with store staff.", exception);
            }
            if (committed is not null) return committed;
            if (exception is MongoException mongoException &&
                mongoException.HasErrorLabel("UnknownTransactionCommitResult"))
            {
                _logger.LogError("Checkout {SaleId} has an unknown commit outcome; stock reservation retained.", sale.Id);
                throw new InvalidOperationException(
                    $"Checkout status is unknown. Do not retry payment; check receipt {sale.Id} with store staff.", exception);
            }

            try { await _products.RestoreStockAsync(quantitiesById, CancellationToken.None); }
            catch (Exception restoreException)
            {
                _logger.LogError("Cannot restore inventory for failed checkout {SaleId}. Failure type: {FailureType}.",
                    sale.Id, restoreException.GetType().Name);
                throw new InvalidOperationException(
                    $"Checkout was not saved, but inventory could not be restored. Contact store staff with reference {sale.Id}.",
                    restoreException);
            }
            throw;
        }
    }

    public Task<List<DemoSale>> CustomerHistoryAsync(string email, CancellationToken cancellationToken) =>
        _sales.Find(s => s.CustomerEmail == email.Trim().ToLowerInvariant())
            .SortByDescending(s => s.CreatedAtUtc).Limit(100).ToListAsync(cancellationToken);

    public Task UpdateEmailStatusAsync(string saleId, string status, CancellationToken cancellationToken) =>
        _sales.UpdateOneAsync(s => s.Id == saleId,
            Builders<DemoSale>.Update.Set(s => s.EmailStatus, status), cancellationToken: cancellationToken);


    public Task<List<DemoSale>> RecentSalesAsync(CancellationToken cancellationToken) =>
        _sales.Find(FilterDefinition<DemoSale>.Empty)
            .SortByDescending(s => s.CreatedAtUtc).Limit(20).ToListAsync(cancellationToken);
}
