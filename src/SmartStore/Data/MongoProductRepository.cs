using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using SmartStore.Models;
using SmartStore.Options;

namespace SmartStore.Data;

public sealed partial class MongoProductRepository : IProductRepository
{
    private readonly IMongoClient _client;
    private readonly IMongoCollection<Product> _products;
    private readonly IMongoCollection<Reception> _receptions;
    private readonly IMongoCollection<Item> _items;
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private bool _indexesReady;

    public MongoProductRepository(IMongoClient client, IOptions<MongoDbOptions> options)
    {
        _client = client;
        var settings = options.Value;
        var database = client.GetDatabase(settings.DatabaseName);
        _codes = database.GetCollection<ProductCode>("product_codes");
        _items = database.GetCollection<Item>("items");
        _products = database.GetCollection<Product>(settings.ProductsCollectionName);
        _receptions = database.GetCollection<Reception>(settings.ReceptionsCollectionName);
    }

    public async Task<IReadOnlyList<Product>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await EnsureIndexesAsync(cancellationToken);
        return await _products.Find(FilterDefinition<Product>.Empty)
            .SortBy(p => p.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<Product?> GetByIdAsync(string id, CancellationToken cancellationToken = default) =>
        IsObjectId(id)
            ? await _products.Find(p => p.Id == id).FirstOrDefaultAsync(cancellationToken)
            : null;

    public async Task<Product?> FindByUpcAsync(string upc, CancellationToken cancellationToken = default,
        IClientSessionHandle? session = null)
    {
        var value = upc.Trim();
        if (value.Length == 0) return null;
        var query = session is null
            ? _products.Find(p => p.Upc == value)
            : _products.Find(session, p => p.Upc == value);
        return await query.FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<Item?> FindItemAsync(string epc, CancellationToken token, IClientSessionHandle? session = null)
    {
        var key = epc.Trim().ToUpperInvariant();
        if (key.Length == 0) return null;
        var query = session is null ? _items.Find(i => i.Epc == key) : _items.Find(session, i => i.Epc == key);
        return await query.FirstOrDefaultAsync(token);
    }

    public Task<List<Item>> ListItemsAsync(string? productId, string? status, int count, CancellationToken token)
    {
        var filter = Builders<Item>.Filter.Empty;
        if (productId is not null) filter &= Builders<Item>.Filter.Eq(i => i.ProductId, productId);
        if (status is not null) filter &= Builders<Item>.Filter.Eq(i => i.Status, status);
        return _items.Find(filter).SortByDescending(i => i.ReceivedAtUtc).ThenBy(i => i.Epc)
            .Limit(Math.Clamp(count, 1, 1000)).ToListAsync(token);
    }

    public async Task<Dictionary<string, int>> CountTaggedInStockAsync(CancellationToken token)
    {
        var counts = await _items.Aggregate()
            .Match(i => i.Status == Item.InStock)
            .Group(i => i.ProductId, g => new { ProductId = g.Key, Count = g.Count() })
            .ToListAsync(token);
        return counts.ToDictionary(c => c.ProductId, c => c.Count);
    }

    public async Task<int> CountTaggedInStockAsync(string productId, CancellationToken token, IClientSessionHandle? session = null) =>
        (int)(session is null
            ? await _items.CountDocumentsAsync(i => i.ProductId == productId && i.Status == Item.InStock, cancellationToken: token)
            : await CountTaggedAsync(session, productId, token));

    public async Task AddAsync(Product product, CancellationToken cancellationToken = default)
    {
        await EnsureIndexesAsync(cancellationToken);
        await ThrowIfCodesTakenAsync(product, cancellationToken);
        product.QuantityInStock = 0;

        try
        {
            product.Id ??= ObjectId.GenerateNewId().ToString();
            await MongoTransaction.RunAsync(_client, async (session, token) =>
            {
                await _products.InsertOneAsync(session, product, cancellationToken: token);
                await SavePrimaryCodesAsync(session, product, token);
                return true;
            }, "Product creation", product.Id, cancellationToken);
        }
        catch (MongoWriteException exception)
            when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new DuplicateProductCodeException("UPC", product.Upc);
        }
    }

    public async Task UpdateAsync(Product product, CancellationToken cancellationToken = default)
    {
        if (!IsObjectId(product.Id)) throw new ProductNotFoundException();
        await EnsureIndexesAsync(cancellationToken);
        await ThrowIfCodesTakenAsync(product, cancellationToken);

        // Update the descriptive fields only, so a sale or reception happening at the
        // same moment cannot be overwritten by an old stock number from the form.
        var update = Builders<Product>.Update
            .Set(p => p.Name, product.Name)
            .Set(p => p.Category, product.Category)
            .Set(p => p.Price, product.Price)
            .Set(p => p.Upc, product.Upc)
            .Set(p => p.Producer, product.Producer)
            .Set(p => p.Description, product.Description)
            .Set(p => p.ImageUrl, product.ImageUrl)
            .Set(p => p.UpdatedAtUtc, product.UpdatedAtUtc);

        try
        {
            await MongoTransaction.RunAsync(_client, async (session, token) =>
            {
                var old = await _products.FindOneAndUpdateAsync<Product, Product>(session, p => p.Id == product.Id,
                    update, cancellationToken: token) ?? throw new ProductNotFoundException();
                var oldUpc = old.Upc.ToUpperInvariant();
                if (oldUpc != product.Upc.ToUpperInvariant())
                    await _codes.DeleteOneAsync(session, c => c.ProductId == product.Id && c.Code == oldUpc, cancellationToken: token);
                await SavePrimaryCodesAsync(session, product, token);
                return true;
            }, "Product update", product.Id!, cancellationToken);
        }
        catch (MongoWriteException exception)
            when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new DuplicateProductCodeException("UPC", product.Upc);
        }
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!IsObjectId(id)) return false;
        return await MongoTransaction.RunAsync(_client, async (session, token) =>
        {
            var result = await _products.DeleteOneAsync(session, p => p.Id == id, cancellationToken: token);
            await _codes.DeleteManyAsync(session, c => c.ProductId == id, cancellationToken: token);
            await _items.DeleteManyAsync(session, i => i.ProductId == id, cancellationToken: token);
            await _receptions.DeleteManyAsync(session, r => r.ProductId == id, cancellationToken: token);
            return result.DeletedCount == 1;
        }, "Product deletion", id, cancellationToken);
    }

    public async Task<Product> ReceiveStockAsync(Reception reception, CancellationToken cancellationToken = default)
    {
        var untagged = reception.Quantity;
        if (untagged < 0) throw new InvalidOperationException("Untagged units can't be negative.");
        var requested = new List<string>();
        foreach (var entry in reception.Epcs)
        {
            var epc = Item.NormalizeEpc(entry)
                ?? throw new InvalidOperationException($"\"{entry}\" is not a valid EPC (4–128 hexadecimal characters).");
            if (!requested.Contains(epc)) requested.Add(epc);
        }
        if (requested.Count == 0 && untagged < 1)
            throw new InvalidOperationException("Scan at least one RFID tag or enter a number of untagged units.");
        if (!IsObjectId(reception.ProductId)) throw new ProductNotFoundException();
        await EnsureIndexesAsync(cancellationToken);
        reception.Id ??= ObjectId.GenerateNewId().ToString();
        if (reception.CreatedAtUtc == default) reception.CreatedAtUtc = DateTime.UtcNow;

        try
        {
            return await MongoTransaction.RunAsync(_client, async (session, token) =>
            {
                // Lock the product first so a concurrent sale or deletion conflicts with this delivery.
                var product = await _products.FindOneAndUpdateAsync<Product, Product>(session,
                    p => p.Id == reception.ProductId,
                    Builders<Product>.Update.Set(p => p.UpdatedAtUtc, DateTime.UtcNow),
                    cancellationToken: token) ?? throw new ProductNotFoundException();

                var existing = await _items.Find(session, i => requested.Contains(i.Epc)).ToListAsync(token);
                foreach (var item in existing)
                {
                    if (item.ProductId != product.Id)
                        throw new InvalidOperationException($"Tag {item.Epc} is already registered to another product.");
                    // A tag a barcode sale only guessed is physically here: put it back, like a rescan.
                    if (IsGuessedSale(item))
                        await ReclaimGuessedAsync(session, item, requested, token);
                    else if (item.Status != Item.InStock)
                        throw new InvalidOperationException($"Tag {item.Epc} was already sold. Remove it from this delivery.");
                }
                var known = existing.Select(i => i.Epc).ToHashSet(StringComparer.Ordinal);
                var fresh = requested.Where(epc => !known.Contains(epc)).ToList();
                if (fresh.Count > 0)
                {
                    var clash = await _codes.Find(session, c => fresh.Contains(c.Code)).FirstOrDefaultAsync(token);
                    if (clash is not null)
                        throw new InvalidOperationException($"{clash.Code} is saved as a product barcode, not an RFID tag.");
                }

                var total = fresh.Count + untagged;
                if (total == 0)
                    throw new InvalidOperationException("Every scanned tag is already in stock. Nothing was received.");

                // Recomputed on each attempt, so a retried transaction never keeps stale values.
                reception.Epcs = fresh;
                reception.Quantity = total;
                if (fresh.Count > 0)
                {
                    await _items.InsertManyAsync(session, fresh.Select(epc => new Item
                    {
                        Epc = epc, ProductId = product.Id!, Status = Item.InStock,
                        ReceptionId = reception.Id, ReceivedAtUtc = reception.CreatedAtUtc
                    }), cancellationToken: token);
                }
                var updated = await _products.FindOneAndUpdateAsync<Product, Product>(session,
                    p => p.Id == reception.ProductId,
                    Builders<Product>.Update.Inc(p => p.QuantityInStock, total),
                    new FindOneAndUpdateOptions<Product> { ReturnDocument = ReturnDocument.After },
                    token) ?? throw new ProductNotFoundException();
                await _receptions.InsertOneAsync(session, reception, cancellationToken: token);
                return updated;
            }, "Delivery receiving", reception.Id, cancellationToken);
        }
        catch (Exception exception) when (IsDuplicateKey(exception))
        {
            throw new InvalidOperationException("One of these tags was just registered by another delivery. Scan the delivery again.");
        }
    }

    public async Task<IReadOnlyList<Reception>> GetReceptionsAsync(
        string productId,
        int count = 50,
        CancellationToken cancellationToken = default)
    {
        if (!IsObjectId(productId)) return [];
        return await _receptions.Find(r => r.ProductId == productId)
            .SortByDescending(r => r.ReceivedOn)
            .ThenByDescending(r => r.CreatedAtUtc)
            .Limit(Math.Clamp(count, 1, 200))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Reception>> GetRecentReceptionsAsync(
        int count = 20,
        CancellationToken cancellationToken = default) =>
        await _receptions.Find(FilterDefinition<Reception>.Empty)
            .SortByDescending(r => r.CreatedAtUtc)
            .Limit(Math.Clamp(count, 1, 200))
            .ToListAsync(cancellationToken);

    public async Task<bool> DeleteReceptionAsync(string receptionId, CancellationToken cancellationToken = default)
    {
        if (!IsObjectId(receptionId)) return false;
        const string alreadySold = "This delivery can't be undone because some of its units have already been sold.";
        return await MongoTransaction.RunAsync(_client, async (session, token) =>
        {
            var reception = await _receptions.FindOneAndDeleteAsync(session,
                r => r.Id == receptionId, cancellationToken: token);
            if (reception is null) return false;
            if (!await _products.Find(session, p => p.Id == reception.ProductId).AnyAsync(token)) return true;

            if (reception.Epcs.Count > 0)
            {
                var removed = await _items.DeleteManyAsync(session, i => reception.Epcs.Contains(i.Epc) &&
                    i.ProductId == reception.ProductId && i.Status == Item.InStock, cancellationToken: token);
                if (removed.DeletedCount != reception.Epcs.Count) throw new InvalidOperationException(alreadySold);
            }
            if (!await DecrementStockAsync(session, reception.ProductId, reception.Quantity, token) ||
                !await StockCoversTagsAsync(session, reception.ProductId, token))
                throw new InvalidOperationException(alreadySold);
            return true;
        }, "Delivery undo", receptionId, cancellationToken);
    }

    public async Task SellAsync(IClientSessionHandle session, string productId, int quantity,
        IReadOnlyCollection<string> epcs, string saleId, CancellationToken cancellationToken = default)
    {
        if (quantity < epcs.Count || quantity < 1) throw new ArgumentOutOfRangeException(nameof(quantity));
        if (epcs.Count > 0)
        {
            var sold = await _items.UpdateManyAsync(session,
                i => epcs.Contains(i.Epc) && i.ProductId == productId && i.Status == Item.InStock,
                Builders<Item>.Update.Set(i => i.Status, Item.Sold).Set(i => i.SaleId, saleId)
                    .Set(i => i.SoldAtUtc, DateTime.UtcNow).Set(i => i.SoldBy, Item.SoldByScan),
                cancellationToken: cancellationToken);
            if (sold.ModifiedCount != epcs.Count)
                throw new InvalidOperationException("A scanned item was already sold. Review your cart and try again.");
        }
        // Untagged units may only come from stock that has no tag; tagged units must be sold by tag.
        if (!await DecrementStockAsync(session, productId, quantity, cancellationToken) ||
            !await StockCoversTagsAsync(session, productId, cancellationToken))
            throw new InvalidOperationException("Stock changed. Review your cart and try again.");
    }

    public async Task<IReadOnlyList<string>> SellOldestFirstAsync(IClientSessionHandle session, string productId,
        int quantity, IReadOnlyCollection<string> scannedEpcs, string saleId, CancellationToken cancellationToken = default)
    {
        if (quantity < scannedEpcs.Count || quantity < 1) throw new ArgumentOutOfRangeException(nameof(quantity));
        var scanned = scannedEpcs.ToList();

        // A scanned tag that an earlier barcode sale only guessed is in the customer's hand, so that guess was wrong.
        foreach (var item in await _items.Find(session, i => scanned.Contains(i.Epc)).ToListAsync(cancellationToken))
            if (item.ProductId == productId && IsGuessedSale(item))
                await ReclaimGuessedAsync(session, item, scanned, cancellationToken);

        // A barcode says which product, not which unit: retire the oldest tags first (first in, first out).
        var guessed = quantity == scanned.Count ? [] : await _items
            .Find(session, i => i.ProductId == productId && i.Status == Item.InStock && !scanned.Contains(i.Epc))
            .SortBy(i => i.ReceivedAtUtc).ThenBy(i => i.Epc).Limit(quantity - scanned.Count)
            .Project(i => i.Epc).ToListAsync(cancellationToken);

        await SellAsync(session, productId, quantity, [.. scanned, .. guessed], saleId, cancellationToken);
        if (guessed.Count > 0)
            await _items.UpdateManyAsync(session, i => guessed.Contains(i.Epc),
                Builders<Item>.Update.Set(i => i.SoldBy, Item.SoldByGuess), cancellationToken: cancellationToken);
        return [.. scanned, .. guessed];
    }

    private static bool IsGuessedSale(Item item) => item.Status == Item.Sold && item.SoldBy == Item.SoldByGuess;

    // Puts a guessed-sold tag back in stock and moves the earlier sale's guess to another in-stock unit of the
    // product (oldest first, never one in keep). With no tag left, the earlier sale is counted as an untagged unit.
    // Stock does not change: the earlier sale still removed one unit, only which unit is corrected.
    private async Task ReclaimGuessedAsync(IClientSessionHandle session, Item item, IReadOnlyCollection<string> keep,
        CancellationToken token)
    {
        var replacement = await _items
            .Find(session, i => i.ProductId == item.ProductId && i.Status == Item.InStock && !keep.Contains(i.Epc))
            .SortBy(i => i.ReceivedAtUtc).ThenBy(i => i.Epc).FirstOrDefaultAsync(token);
        if (replacement is not null)
        {
            var moved = await _items.UpdateOneAsync(session, i => i.Epc == replacement.Epc && i.Status == Item.InStock,
                Builders<Item>.Update.Set(i => i.Status, Item.Sold).Set(i => i.SaleId, item.SaleId)
                    .Set(i => i.SoldAtUtc, item.SoldAtUtc).Set(i => i.SoldBy, Item.SoldByGuess),
                cancellationToken: token);
            if (moved.ModifiedCount != 1) throw new InvalidOperationException("Stock changed. Review your cart and try again.");
        }
        else
        {
            var product = await _products.Find(session, p => p.Id == item.ProductId).FirstOrDefaultAsync(token);
            if (product is null || product.QuantityInStock <= await CountTaggedAsync(session, item.ProductId, token))
                throw new InvalidOperationException(
                    $"Tag {item.Epc} is marked as sold, and no other unit of this product is left to take its place.");
        }

        var reopened = await _items.UpdateOneAsync(session,
            i => i.Epc == item.Epc && i.Status == Item.Sold && i.SoldBy == Item.SoldByGuess,
            Builders<Item>.Update.Set(i => i.Status, Item.InStock)
                .Unset(i => i.SaleId).Unset(i => i.SoldAtUtc).Unset(i => i.SoldBy),
            cancellationToken: token);
        if (reopened.ModifiedCount != 1) throw new InvalidOperationException("Stock changed. Review your cart and try again.");
    }

    public async Task<bool> DecrementStockAsync(IClientSessionHandle session, string productId, int quantity,
        CancellationToken cancellationToken = default)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Stock changes must join an active transaction.");
        if (quantity < 1 || !IsObjectId(productId)) return false;
        var result = await _products.UpdateOneAsync(session,
            p => p.Id == productId && p.QuantityInStock >= quantity,
            Builders<Product>.Update
                .Inc(p => p.QuantityInStock, -quantity)
                .Set(p => p.UpdatedAtUtc, DateTime.UtcNow),
            cancellationToken: cancellationToken);
        return result.ModifiedCount == 1;
    }

    // Stock counts tagged and untagged units, so it can never be below the in-stock tag count.
    private async Task<bool> StockCoversTagsAsync(IClientSessionHandle session, string productId, CancellationToken token)
    {
        var product = await _products.Find(session, p => p.Id == productId).FirstOrDefaultAsync(token);
        return product is not null && product.QuantityInStock >= await CountTaggedAsync(session, productId, token);
    }

    private Task<long> CountTaggedAsync(IClientSessionHandle session, string productId, CancellationToken token) =>
        _items.CountDocumentsAsync(session, i => i.ProductId == productId && i.Status == Item.InStock, cancellationToken: token);

    private async Task ThrowIfCodesTakenAsync(Product product, CancellationToken cancellationToken)
    {
        // Store codes in one format so lookups and the unique index always match.
        product.Upc = product.Upc.Trim();

        var otherProducts = product.Id is null
            ? Builders<Product>.Filter.Empty
            : Builders<Product>.Filter.Ne(p => p.Id, product.Id);

        if (await _products.Find(otherProducts & Builders<Product>.Filter.Eq(p => p.Upc, product.Upc))
                .AnyAsync(cancellationToken))
            throw new DuplicateProductCodeException("UPC", product.Upc);
    }

    private static bool IsDuplicateKey(Exception exception) => exception switch
    {
        MongoWriteException write => write.WriteError?.Category == ServerErrorCategory.DuplicateKey,
        MongoBulkWriteException bulk => bulk.WriteErrors.Any(e => e.Category == ServerErrorCategory.DuplicateKey),
        _ => false
    };

    private static bool IsObjectId(string? id) => id is not null && ObjectId.TryParse(id, out _);

    private async Task EnsureIndexesAsync(CancellationToken cancellationToken)
    {
        if (_indexesReady) return;

        await _indexLock.WaitAsync(cancellationToken);
        try
        {
            if (_indexesReady) return;

            // Products no longer carry an EPC. The old unique index would reject every
            // second product once the field is gone, so it is dropped before anything else.
            try { await _products.Indexes.DropOneAsync("ux_products_epc", cancellationToken); }
            catch (MongoCommandException exception)
                when (exception.CodeName is "IndexNotFound" or "NamespaceNotFound") { }

            await _products.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<Product>(
                    Builders<Product>.IndexKeys.Ascending(p => p.Upc),
                    new CreateIndexOptions { Name = "ux_products_upc", Unique = true }),
                new CreateIndexModel<Product>(
                    Builders<Product>.IndexKeys.Ascending(p => p.Category),
                    new CreateIndexOptions { Name = "ix_products_category" })
            ], cancellationToken: cancellationToken);

            await _receptions.Indexes.CreateOneAsync(
                new CreateIndexModel<Reception>(
                    Builders<Reception>.IndexKeys
                        .Ascending(r => r.ProductId)
                        .Descending(r => r.ReceivedOn),
                    new CreateIndexOptions { Name = "ix_receptions_product_receivedOn" }),
                cancellationToken: cancellationToken);

            await _items.Indexes.CreateOneAsync(
                new CreateIndexModel<Item>(
                    Builders<Item>.IndexKeys.Ascending(i => i.ProductId).Ascending(i => i.Status),
                    new CreateIndexOptions { Name = "ix_items_product_status" }),
                cancellationToken: cancellationToken);

            await MigrateLegacyEpcsAsync(cancellationToken);
            _indexesReady = true;
        }
        finally
        {
            _indexLock.Release();
        }
    }
}
