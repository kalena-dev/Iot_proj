using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using SmartStore.Models;
using SmartStore.Options;

namespace SmartStore.Data;

public sealed class MongoProductRepository : IProductRepository
{
    private readonly IMongoCollection<Product> _products;
    private readonly IMongoCollection<Reception> _receptions;
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private bool _indexesReady;

    public MongoProductRepository(IMongoClient client, IOptions<MongoDbOptions> options)
    {
        var settings = options.Value;
        var database = client.GetDatabase(settings.DatabaseName);
        _products = database.GetCollection<Product>(settings.ProductsCollectionName);
        _receptions = database.GetCollection<Reception>(settings.ReceptionsCollectionName);
    }

    public async Task<IReadOnlyList<Product>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await _products.Find(FilterDefinition<Product>.Empty)
            .SortBy(p => p.Name)
            .ToListAsync(cancellationToken);

    public async Task<Product?> GetByIdAsync(string id, CancellationToken cancellationToken = default) =>
        IsObjectId(id)
            ? await _products.Find(p => p.Id == id).FirstOrDefaultAsync(cancellationToken)
            : null;

    public async Task<Product?> FindByUpcAsync(string upc, CancellationToken cancellationToken = default)
    {
        var value = upc.Trim();
        return value.Length == 0
            ? null
            : await _products.Find(p => p.Upc == value).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<Product?> FindByEpcAsync(string epc, CancellationToken cancellationToken = default)
    {
        var value = epc.Trim().ToUpperInvariant();
        return value.Length == 0
            ? null
            : await _products.Find(p => p.Epc == value).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task AddAsync(Product product, CancellationToken cancellationToken = default)
    {
        await EnsureIndexesAsync(cancellationToken);
        await ThrowIfCodesTakenAsync(product, cancellationToken);
        product.QuantityInStock = 0;

        try
        {
            await _products.InsertOneAsync(product, cancellationToken: cancellationToken);
        }
        catch (MongoWriteException exception)
            when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            throw ToDuplicateException(exception, product);
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
            .Set(p => p.Epc, product.Epc)
            .Set(p => p.Producer, product.Producer)
            .Set(p => p.Description, product.Description)
            .Set(p => p.ImageUrl, product.ImageUrl)
            .Set(p => p.UpdatedAtUtc, product.UpdatedAtUtc);

        try
        {
            var result = await _products.UpdateOneAsync(p => p.Id == product.Id, update,
                cancellationToken: cancellationToken);
            if (result.MatchedCount == 0) throw new ProductNotFoundException();
        }
        catch (MongoWriteException exception)
            when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            throw ToDuplicateException(exception, product);
        }
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!IsObjectId(id)) return false;
        var result = await _products.DeleteOneAsync(p => p.Id == id, cancellationToken);
        await _receptions.DeleteManyAsync(r => r.ProductId == id, cancellationToken);
        return result.DeletedCount == 1;
    }

    public async Task<Product> ReceiveStockAsync(Reception reception, CancellationToken cancellationToken = default)
    {
        if (reception.Quantity < 1) throw new InvalidOperationException("Quantity must be at least 1.");
        if (!IsObjectId(reception.ProductId)) throw new ProductNotFoundException();
        await EnsureIndexesAsync(cancellationToken);

        // The two collections cannot be updated atomically without a transaction.
        // Compensate a failed stock update even when the request was cancelled.
        await _receptions.InsertOneAsync(reception, cancellationToken: cancellationToken);
        try
        {
            var updated = await _products.FindOneAndUpdateAsync(
                p => p.Id == reception.ProductId,
                Builders<Product>.Update.Inc(p => p.QuantityInStock, reception.Quantity),
                new FindOneAndUpdateOptions<Product> { ReturnDocument = ReturnDocument.After },
                cancellationToken);

            return updated ?? throw new ProductNotFoundException();
        }
        catch (Exception receiveException)
        {
            try
            {
                await _receptions.DeleteOneAsync(r => r.Id == reception.Id, CancellationToken.None);
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException(
                    "Receiving stock failed and its delivery history could not be rolled back. Check stock and history before retrying.",
                    receiveException, rollbackException);
            }

            throw;
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
        // Claim the delivery atomically. Only the caller that removed the history
        // document may adjust stock; concurrent/repeated undo requests do nothing.
        var reception = await _receptions.FindOneAndDeleteAsync(
            r => r.Id == receptionId, cancellationToken: cancellationToken);
        if (reception is null) return false;

        try
        {
            if (!await DecrementStockAsync(reception.ProductId, reception.Quantity, cancellationToken)
                && await _products.Find(p => p.Id == reception.ProductId).AnyAsync(cancellationToken))
            {
                throw new InvalidOperationException(
                    "This delivery can't be undone because some of its units have already been sold.");
            }

            return true;
        }
        catch (Exception undoException)
        {
            try
            {
                await _receptions.InsertOneAsync(reception, cancellationToken: CancellationToken.None);
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException(
                    "Undoing the delivery failed and its history could not be restored. Check stock and history before retrying.",
                    undoException, rollbackException);
            }

            throw;
        }
    }

    public async Task<bool> DecrementStockAsync(string productId, int quantity, CancellationToken cancellationToken = default)
    {
        if (quantity < 1 || !IsObjectId(productId)) return false;
        var result = await _products.UpdateOneAsync(
            p => p.Id == productId && p.QuantityInStock >= quantity,
            Builders<Product>.Update
                .Inc(p => p.QuantityInStock, -quantity)
                .Set(p => p.UpdatedAtUtc, DateTime.UtcNow),
            cancellationToken: cancellationToken);
        return result.ModifiedCount == 1;
    }

    public async Task<StockDecrementResult> DecrementStockManyAsync(
        IReadOnlyDictionary<string, int> quantitiesByProductId,
        CancellationToken cancellationToken = default)
    {
        if (quantitiesByProductId.Count == 0)
            return new StockDecrementResult(false, Message: "The cart is empty.");

        foreach (var (productId, quantity) in quantitiesByProductId)
        {
            if (quantity < 1)
                return new StockDecrementResult(false, productId, "Every quantity must be at least 1.");
            if (!IsObjectId(productId))
                return new StockDecrementResult(false, productId, "The cart contains an unknown product.");
        }

        // Take each item out one at a time (each step is atomic and refuses to go below 0).
        // If one fails, put back everything taken so far, so it's all or nothing.
        var taken = new Dictionary<string, int>();
        foreach (var (productId, quantity) in quantitiesByProductId)
        {
            bool ok;
            try
            {
                ok = await DecrementStockAsync(productId, quantity, cancellationToken);
            }
            catch
            {
                await RestoreStockAsync(taken, CancellationToken.None);
                throw;
            }

            if (ok)
            {
                taken[productId] = quantity;
                continue;
            }

            await RestoreStockAsync(taken, CancellationToken.None);
            var product = await GetByIdAsync(productId, CancellationToken.None);
            var message = product is null
                ? "A product in the cart no longer exists."
                : $"Not enough {product.Name} in stock ({product.QuantityInStock} left, {quantity} requested).";
            return new StockDecrementResult(false, productId, message);
        }

        return StockDecrementResult.Success;
    }

    public async Task RestoreStockAsync(
        IReadOnlyDictionary<string, int> quantitiesByProductId,
        CancellationToken cancellationToken = default)
    {
        var updates = quantitiesByProductId
            .Where(item => item.Value > 0 && IsObjectId(item.Key))
            .Select(item => new UpdateOneModel<Product>(
                Builders<Product>.Filter.Eq(p => p.Id, item.Key),
                Builders<Product>.Update
                    .Inc(p => p.QuantityInStock, item.Value)
                    .Set(p => p.UpdatedAtUtc, DateTime.UtcNow)))
            .ToList();

        if (updates.Count > 0)
            await _products.BulkWriteAsync(updates, cancellationToken: cancellationToken);
    }

    private async Task ThrowIfCodesTakenAsync(Product product, CancellationToken cancellationToken)
    {
        // Store codes in one format so lookups and the unique indexes always match.
        product.Upc = product.Upc.Trim();
        product.Epc = product.Epc.Trim().ToUpperInvariant();

        var otherProducts = product.Id is null
            ? Builders<Product>.Filter.Empty
            : Builders<Product>.Filter.Ne(p => p.Id, product.Id);

        if (await _products.Find(otherProducts & Builders<Product>.Filter.Eq(p => p.Upc, product.Upc))
                .AnyAsync(cancellationToken))
            throw new DuplicateProductCodeException("UPC", product.Upc);

        if (await _products.Find(otherProducts & Builders<Product>.Filter.Eq(p => p.Epc, product.Epc))
                .AnyAsync(cancellationToken))
            throw new DuplicateProductCodeException("EPC", product.Epc);
    }

    private static DuplicateProductCodeException ToDuplicateException(MongoWriteException exception, Product product) =>
        exception.Message.Contains("ux_products_epc", StringComparison.Ordinal)
            ? new DuplicateProductCodeException("EPC", product.Epc)
            : new DuplicateProductCodeException("UPC", product.Upc);

    private static bool IsObjectId(string? id) => id is not null && ObjectId.TryParse(id, out _);

    private async Task EnsureIndexesAsync(CancellationToken cancellationToken)
    {
        if (_indexesReady) return;

        await _indexLock.WaitAsync(cancellationToken);
        try
        {
            if (_indexesReady) return;

            await _products.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<Product>(
                    Builders<Product>.IndexKeys.Ascending(p => p.Upc),
                    new CreateIndexOptions { Name = "ux_products_upc", Unique = true }),
                new CreateIndexModel<Product>(
                    Builders<Product>.IndexKeys.Ascending(p => p.Epc),
                    new CreateIndexOptions { Name = "ux_products_epc", Unique = true }),
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

            _indexesReady = true;
        }
        finally
        {
            _indexLock.Release();
        }
    }
}
