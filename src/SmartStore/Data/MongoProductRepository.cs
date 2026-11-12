using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using SmartStore.Models;
using SmartStore.Options;

namespace SmartStore.Data;

public sealed class MongoProductRepository : IProductRepository
{
    private readonly IMongoClient _client;
    private readonly IMongoCollection<Product> _products;
    private readonly IMongoCollection<Reception> _receptions;
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private bool _indexesReady;

    public MongoProductRepository(IMongoClient client, IOptions<MongoDbOptions> options)
    {
        _client = client;
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

    public async Task<Product?> FindByEpcAsync(string epc, CancellationToken cancellationToken = default,
        IClientSessionHandle? session = null)
    {
        var value = epc.Trim().ToUpperInvariant();
        if (value.Length == 0) return null;
        var query = session is null
            ? _products.Find(p => p.Epc == value)
            : _products.Find(session, p => p.Epc == value);
        return await query.FirstOrDefaultAsync(cancellationToken);
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
        return await MongoTransaction.RunAsync(_client, async (session, token) =>
        {
            var result = await _products.DeleteOneAsync(session, p => p.Id == id, cancellationToken: token);
            await _receptions.DeleteManyAsync(session, r => r.ProductId == id, cancellationToken: token);
            return result.DeletedCount == 1;
        }, "Product deletion", id, cancellationToken);
    }

    public async Task<Product> ReceiveStockAsync(Reception reception, CancellationToken cancellationToken = default)
    {
        if (reception.Quantity < 1) throw new InvalidOperationException("Quantity must be at least 1.");
        if (!IsObjectId(reception.ProductId)) throw new ProductNotFoundException();
        await EnsureIndexesAsync(cancellationToken);
        reception.Id ??= ObjectId.GenerateNewId().ToString();

        return await MongoTransaction.RunAsync(_client, async (session, token) =>
        {
            var updated = await _products.FindOneAndUpdateAsync<Product, Product>(session,
                p => p.Id == reception.ProductId,
                Builders<Product>.Update
                    .Inc(p => p.QuantityInStock, reception.Quantity)
                    .Set(p => p.UpdatedAtUtc, DateTime.UtcNow),
                new FindOneAndUpdateOptions<Product> { ReturnDocument = ReturnDocument.After },
                token) ?? throw new ProductNotFoundException();
            await _receptions.InsertOneAsync(session, reception, cancellationToken: token);
            return updated;
        }, "Delivery receiving", reception.Id, cancellationToken);
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
        return await MongoTransaction.RunAsync(_client, async (session, token) =>
        {
            var reception = await _receptions.FindOneAndDeleteAsync(session,
                r => r.Id == receptionId, cancellationToken: token);
            if (reception is null) return false;

            if (!await DecrementStockAsync(session, reception.ProductId, reception.Quantity, token) &&
                await _products.Find(session, p => p.Id == reception.ProductId).AnyAsync(token))
            {
                throw new InvalidOperationException(
                    "This delivery can't be undone because some of its units have already been sold.");
            }
            return true;
        }, "Delivery undo", receptionId, cancellationToken);
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
