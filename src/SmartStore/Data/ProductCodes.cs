using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using SmartStore.Models;

namespace SmartStore.Data;

public sealed class ProductCode
{
    [BsonId] public string Code { get; set; } = "";
    [BsonElement("productId"), BsonRepresentation(BsonType.ObjectId)] public string ProductId { get; set; } = "";
    [BsonElement("kind")] public string Kind { get; set; } = "";
}

public sealed partial class MongoProductRepository
{
    private readonly IMongoCollection<ProductCode> _codes;

    public async Task<(Product Product, bool IsTag)?> ResolveCodeAsync(string code, CancellationToken token,
        IClientSessionHandle? session = null)
    {
        var key = code.Trim().ToUpperInvariant();
        var association = await (session is null ? _codes.Find(c => c.Code == key)
            : _codes.Find(session, c => c.Code == key)).FirstOrDefaultAsync(token);
        if (association is null)
        {
            // Existing catalogs can still check out while the code table is being populated.
            var primary = await FindByUpcAsync(code, token, session) ?? await FindByEpcAsync(key, token, session);
            return primary is null ? null : (primary, primary.Upc != code.Trim());
        }
        var product = await (session is null ? _products.Find(p => p.Id == association.ProductId)
            : _products.Find(session, p => p.Id == association.ProductId)).FirstOrDefaultAsync(token);
        return product is null ? null : (product, association.Kind == "epc");
    }

    public Task<List<ProductCode>> ListCodesAsync(CancellationToken token) =>
        _codes.Find(FilterDefinition<ProductCode>.Empty).SortBy(c => c.Code).ToListAsync(token);

    public Task AssignCodeAsync(string code, string upc, CancellationToken token)
    {
        var key = code.Trim().ToUpperInvariant();
        if (key.Length is < 1 or > 512 || key.Any(char.IsControl))
            throw new ArgumentException("Enter a code of up to 512 printable characters.");
        return MongoTransaction.RunAsync(_client, async (session, ct) =>
        {
            // Serialize assignments with product edits and deletion.
            var product = await _products.FindOneAndUpdateAsync<Product, Product>(session, p => p.Upc == upc,
                Builders<Product>.Update.Set(p => p.UpdatedAtUtc, DateTime.UtcNow), cancellationToken: ct)
                ?? throw new ProductNotFoundException();
            var primary = await FindByUpcAsync(code, ct, session) ?? await FindByEpcAsync(key, ct, session);
            if (primary is not null && primary.Id != product.Id) throw new DuplicateProductCodeException("code", key);
            var kind = key.Length is >= 4 and <= 128 && key.Length % 2 == 0 && key.All(Uri.IsHexDigit) ? "epc" : "barcode";
            await ClaimCodeAsync(session, product.Id!, key, kind, ct);
            return true;
        }, "Product code assignment", key, token);
    }

    private async Task ClaimCodeAsync(IClientSessionHandle session, string productId, string code, string kind, CancellationToken token)
    {
        var key = code.Trim().ToUpperInvariant();
        try
        {
            await _codes.UpdateOneAsync(session, c => c.Code == key && c.ProductId == productId,
                Builders<ProductCode>.Update.SetOnInsert(c => c.Kind, kind), new UpdateOptions { IsUpsert = true }, token);
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new DuplicateProductCodeException(kind.ToUpperInvariant(), code);
        }
    }

    private async Task SavePrimaryCodesAsync(IClientSessionHandle session, Product product, CancellationToken token)
    {
        await ClaimCodeAsync(session, product.Id!, product.Upc, "upc", token);
        await ClaimCodeAsync(session, product.Id!, product.Epc, "epc", token);
    }

    public async Task BackfillCodesAsync(CancellationToken token)
    {
        await _codes.Indexes.CreateOneAsync(new CreateIndexModel<ProductCode>(
            Builders<ProductCode>.IndexKeys.Ascending(c => c.ProductId)), cancellationToken: token);
        foreach (var id in await _products.Find(FilterDefinition<Product>.Empty).Project(p => p.Id).ToListAsync(token))
            await MongoTransaction.RunAsync(_client, async (session, ct) =>
            {
                var product = await _products.Find(session, p => p.Id == id).FirstOrDefaultAsync(ct);
                if (product is not null) await SavePrimaryCodesAsync(session, product, ct);
                return true;
            }, "Product code migration", id!, token);
    }
}
