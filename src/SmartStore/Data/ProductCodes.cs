using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using SmartStore.Models;

namespace SmartStore.Data;

// A barcode that sells a product: its UPC ("upc") or an extra barcode saved at checkout ("barcode").
// RFID tags are items, not codes. Older databases may still hold "epc" entries until migrated.
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
        // Index creation and migration must not run inside a caller's transaction.
        if (session is null) await EnsureIndexesAsync(token);
        var key = code.Trim().ToUpperInvariant();
        if (key.Length == 0) return null;

        var item = await FindItemAsync(key, token, session);
        if (item is not null)
        {
            var owner = await (session is null ? _products.Find(p => p.Id == item.ProductId && p.ArchivedAtUtc == null)
                : _products.Find(session, p => p.Id == item.ProductId && p.ArchivedAtUtc == null)).FirstOrDefaultAsync(token);
            return owner is null ? null : (owner, true);
        }

        var association = await (session is null ? _codes.Find(c => c.Code == key)
            : _codes.Find(session, c => c.Code == key)).FirstOrDefaultAsync(token);
        if (association is null || association.Kind == "epc")
        {
            // Existing catalogs can still check out while the code table is being populated.
            var primary = await FindByUpcAsync(code, token, session);
            return primary is null ? null : (primary, false);
        }
        var product = await (session is null ? _products.Find(p => p.Id == association.ProductId && p.ArchivedAtUtc == null)
            : _products.Find(session, p => p.Id == association.ProductId && p.ArchivedAtUtc == null)).FirstOrDefaultAsync(token);
        return product is null ? null : (product, false);
    }

    public Task<List<ProductCode>> ListCodesAsync(CancellationToken token) =>
        _codes.Find(c => c.Kind != "epc" && c.Kind != "item").SortBy(c => c.Code).ToListAsync(token);

    public async Task AssignCodeAsync(string code, string upc, CancellationToken token)
    {
        var key = code.Trim().ToUpperInvariant();
        if (key.Length is < 1 or > 512 || key.Any(char.IsControl))
            throw new ArgumentException("Enter a code of up to 512 printable characters.");
        await EnsureIndexesAsync(token);
        await MongoTransaction.RunAsync(_client, async (session, ct) =>
        {
            // Serialize assignments with product edits and deletion.
            var product = await _products.FindOneAndUpdateAsync<Product, Product>(session, p => p.Upc == upc && p.ArchivedAtUtc == null,
                Builders<Product>.Update.Set(p => p.UpdatedAtUtc, DateTime.UtcNow), cancellationToken: ct)
                ?? throw new ProductNotFoundException();
            // A registered RFID tag identifies one unit and is never reused as a barcode.
            if (await _items.Find(session, i => i.Epc == key).AnyAsync(ct))
                throw new DuplicateProductCodeException("RFID tag", key);
            var primary = await FindByUpcAsync(code, ct, session);
            if (primary is not null && primary.Id != product.Id) throw new DuplicateProductCodeException("code", key);
            await ClaimCodeAsync(session, product.Id!, key, "barcode", ct);
            return true;
        }, "Product code assignment", key, token);
    }

    private async Task ClaimCodeAsync(IClientSessionHandle session, string productId, string code, string kind, CancellationToken token)
    {
        var key = code.Trim().ToUpperInvariant();
        try
        {
            var compatibleKind = kind == "item"
                ? Builders<ProductCode>.Filter.In(c => c.Kind, new[] { "item", "epc" })
                : Builders<ProductCode>.Filter.In(c => c.Kind, new[] { "upc", "barcode" });
            await _codes.UpdateOneAsync(session, Builders<ProductCode>.Filter.Where(c => c.Code == key && c.ProductId == productId) & compatibleKind,
                Builders<ProductCode>.Update.SetOnInsert(c => c.Kind, kind), new UpdateOptions { IsUpsert = true }, token);
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new DuplicateProductCodeException(kind.ToUpperInvariant(), code);
        }
    }

    private async Task SavePrimaryCodesAsync(IClientSessionHandle session, Product product, CancellationToken token)
    {
        if (await _items.Find(session, i => i.Epc == product.Upc.ToUpperInvariant()).AnyAsync(token))
            throw new DuplicateProductCodeException("UPC", product.Upc);
        await ClaimCodeAsync(session, product.Id!, product.Upc, "upc", token);
    }

    public async Task BackfillCodesAsync(CancellationToken token)
    {
        await EnsureIndexesAsync(token);
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

    // Preserve legacy EPC source fields and aliases for traceability. Eligible tags are copied into Items;
    // surplus or unsupported old identifiers remain private historical data, never silently deleted.
    private async Task MigrateLegacyEpcsAsync(CancellationToken token)
    {
        var raw = _products.Database.GetCollection<BsonDocument>(_products.CollectionNamespace.CollectionName);
        var ids = (await raw.Find(Builders<BsonDocument>.Filter.Exists("epc"))
                .Project(Builders<BsonDocument>.Projection.Include("_id")).ToListAsync(token))
            .Select(d => d["_id"].ToString()!)
            .Union(await (await _codes.DistinctAsync(c => c.ProductId, c => c.Kind == "epc", cancellationToken: token))
                .ToListAsync(token))
            .Distinct()
            .ToList();

        foreach (var id in ids)
        {
            await MongoTransaction.RunAsync(_client, async (session, ct) =>
            {
                var filter = Builders<BsonDocument>.Filter.Eq("_id", ObjectId.Parse(id));
                var document = await raw.Find(session, filter).FirstOrDefaultAsync(ct);
                var aliases = await _codes.Find(session, c => c.ProductId == id && c.Kind == "epc").ToListAsync(ct);
                if (document is not null)
                {
                    var tags = new List<string>();
                    if (document.TryGetValue("epc", out var primary) && primary.IsString) tags.Add(primary.AsString);
                    tags.AddRange(aliases.Select(a => a.Code));
                    var room = document.GetValue("quantityInStock", 0).ToInt32() - (int)await CountTaggedAsync(session, id, ct);
                    foreach (var tag in tags.Select(t => t.Trim().ToUpperInvariant()).Distinct())
                    {
                        if (room <= 0) break;
                        if (Item.NormalizeEpc(tag) is null || await _items.Find(session, i => i.Epc == tag).AnyAsync(ct))
                            continue;
                        // Ambiguous legacy identifiers stay in their original documents, never overwrite a barcode.
                        if (await _products.Find(session, p => p.Upc == tag).AnyAsync(ct)) continue;
                        var claim = await _codes.Find(session, c => c.Code == tag).FirstOrDefaultAsync(ct);
                        if (claim is not null && (claim.ProductId != id || claim.Kind is not ("epc" or "item"))) continue;
                        await ClaimCodeAsync(session, id, tag, "item", ct);
                        await _items.InsertOneAsync(session, new Item
                        {
                            Epc = tag, ProductId = id, Status = Item.InStock, ReceivedAtUtc = DateTime.UtcNow
                        }, cancellationToken: ct);
                        room--;
                    }
                }
                return true;
            }, "EPC migration", id, token);
        }
    }
}
