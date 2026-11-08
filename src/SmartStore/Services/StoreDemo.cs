using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Microsoft.Extensions.Options;
using SmartStore.Options;

namespace SmartStore.Services;

public sealed record DemoProduct(string Code, string Name, decimal Price, int Stock);
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

// Small classroom demo: stock is reset when the server restarts; sales are saved to Atlas.
public sealed class StoreDemo
{
    private readonly Dictionary<string, DemoProduct> _products = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1001"] = new("1001", "Apple", 0.99m, 20),
        ["1002"] = new("1002", "Bread", 3.49m, 12),
        ["1003"] = new("1003", "Milk", 4.29m, 10),
        ["1004"] = new("1004", "Chocolate bar", 1.79m, 25)
    };
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly IMongoCollection<DemoSale> _sales;
    private readonly IMongoCollection<SmartStore.Models.Customer> _customers;

    public StoreDemo(IMongoClient client, IOptions<MongoDbOptions> options)
    {
        var db = client.GetDatabase(options.Value.DatabaseName);
        _sales = db.GetCollection<DemoSale>("demo_sales");
        _customers = db.GetCollection<SmartStore.Models.Customer>(options.Value.CustomersCollectionName);
    }

    public IReadOnlyList<DemoProduct> Products
    {
        get
        {
            _lock.Wait();
            try { return _products.Values.OrderBy(p => p.Code).ToList(); }
            finally { _lock.Release(); }
        }
    }

    // The in-memory catalog is temporary until the inventory module supplies a repository.
    // A lock protects simultaneous checkouts within one server process only.
    public async Task<DemoSale> CheckoutAsync(Dictionary<string, int> cart, string paymentResult,
        string? customerEmail, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (cart.Count is < 1 or > 20 || cart.Any(x => x.Value is < 1 or > 50))
                throw new InvalidOperationException("Add 1–20 different items with quantities between 1 and 50.");
            if (paymentResult is not ("approve" or "decline"))
                throw new InvalidOperationException("Choose a valid simulated payment result.");

            var lines = new List<DemoSaleLine>();
            foreach (var (code, quantity) in cart)
            {
                if (!_products.TryGetValue(code, out var product))
                    throw new InvalidOperationException($"Unknown item code: {code}.");
                if (product.Stock < quantity)
                    throw new InvalidOperationException($"Not enough {product.Name} in stock.");
                lines.Add(new DemoSaleLine(product.Code, product.Name, product.Price, quantity));
            }
            SmartStore.Models.Customer? customer = null;
            if (!string.IsNullOrWhiteSpace(customerEmail))
            {
                customerEmail = customerEmail.Trim().ToLowerInvariant();
                if (!System.Net.Mail.MailAddress.TryCreate(customerEmail, out var address) ||
                    !string.Equals(address.Address, customerEmail, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Enter a valid customer email address.");
                customer = await _customers.Find(c => c.Email == customerEmail)
                    .FirstOrDefaultAsync(cancellationToken);
                if (customer is null)
                    throw new InvalidOperationException("Customer not found. Register the email first or leave it blank for guest checkout.");
            }
            var total = lines.Sum(x => x.Price * x.Quantity);
            var approved = paymentResult == "approve";
            var sale = new DemoSale
            {
                Items = lines, Total = total, PaymentStatus = approved ? "approved" : "declined",
                CustomerId = customer?.Id, CustomerEmail = customer?.Email,
                PointsEarned = approved && customer is not null ? (int)decimal.Floor(total) : 0,
                EmailStatus = approved && customer is not null ? "pending" : "not_requested"
            };
            // Persist declined attempts as checkout records, without affecting stock or points.
            await _sales.InsertOneAsync(sale, cancellationToken: cancellationToken);
            if (!approved) return sale;
            foreach (var line in lines)
                _products[line.Code] = _products[line.Code] with { Stock = _products[line.Code].Stock - line.Quantity };
            if (customer is not null && sale.PointsEarned > 0)
                await _customers.UpdateOneAsync(c => c.Id == customer.Id,
                    Builders<SmartStore.Models.Customer>.Update.Inc(c => c.TotalPoints, sale.PointsEarned),
                    cancellationToken: cancellationToken);
            return sale;
        }
        finally { _lock.Release(); }
    }

    public Task<List<DemoSale>> CustomerHistoryAsync(string email, CancellationToken cancellationToken) =>
        _sales.Find(s => s.CustomerEmail == email.Trim().ToLowerInvariant())
            .SortByDescending(s => s.CreatedAtUtc).Limit(100).ToListAsync(cancellationToken);

    public Task UpdateEmailStatusAsync(string saleId, string status, CancellationToken cancellationToken) =>
        _sales.UpdateOneAsync(s => s.Id == saleId,
            Builders<DemoSale>.Update.Set(s => s.EmailStatus, status), cancellationToken: cancellationToken);

    public async Task SetStockAsync(string code, int stock, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (stock < 0 || stock > 9999 || !_products.TryGetValue(code, out var product))
                throw new InvalidOperationException("Choose a valid item and stock amount (0–9999).");
            _products[code] = product with { Stock = stock };
        }
        finally { _lock.Release(); }
    }

    public Task<List<DemoSale>> RecentSalesAsync(CancellationToken cancellationToken) =>
        _sales.Find(FilterDefinition<DemoSale>.Empty)
            .SortByDescending(s => s.CreatedAtUtc).Limit(20).ToListAsync(cancellationToken);
}
