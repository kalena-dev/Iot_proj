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

    public StoreDemo(IMongoClient client, IOptions<MongoDbOptions> options) =>
        _sales = client.GetDatabase(options.Value.DatabaseName).GetCollection<DemoSale>("demo_sales");

    public IReadOnlyList<DemoProduct> Products
    {
        get
        {
            _lock.Wait();
            try { return _products.Values.OrderBy(p => p.Code).ToList(); }
            finally { _lock.Release(); }
        }
    }

    public async Task<DemoSale> CheckoutAsync(Dictionary<string, int> cart, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (cart.Count is < 1 or > 20 || cart.Any(x => x.Value is < 1 or > 50))
                throw new InvalidOperationException("Add at least one item with a valid quantity.");

            var lines = new List<DemoSaleLine>();
            foreach (var (code, quantity) in cart)
            {
                if (!_products.TryGetValue(code, out var product))
                    throw new InvalidOperationException($"Unknown item code: {code}.");
                if (product.Stock < quantity)
                    throw new InvalidOperationException($"Not enough {product.Name} in stock.");
                lines.Add(new DemoSaleLine(product.Code, product.Name, product.Price, quantity));
            }

            var sale = new DemoSale { Items = lines, Total = lines.Sum(x => x.Price * x.Quantity) };
            // Only reduce stock once the sale has been saved successfully.
            await _sales.InsertOneAsync(sale, cancellationToken: cancellationToken);
            foreach (var line in lines)
                _products[line.Code] = _products[line.Code] with { Stock = _products[line.Code].Stock - line.Quantity };
            return sale;
        }
        finally { _lock.Release(); }
    }

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
