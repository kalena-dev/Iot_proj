using Microsoft.Extensions.Options;
using MongoDB.Driver;
using SmartStore.Models;
using SmartStore.Options;

namespace SmartStore.Data;

public sealed class MongoCustomerRepository : ICustomerRepository
{
    private readonly IMongoCollection<Customer> _customers;
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private bool _indexesReady;

    public MongoCustomerRepository(IMongoClient client, IOptions<MongoDbOptions> options)
    {
        var settings = options.Value;
        var database = client.GetDatabase(settings.DatabaseName);
        _customers = database.GetCollection<Customer>(settings.CustomersCollectionName);
    }

    public async Task AddAsync(Customer customer, CancellationToken cancellationToken = default)
    {
        await EnsureIndexesAsync(cancellationToken);

        try
        {
            await _customers.InsertOneAsync(customer, cancellationToken: cancellationToken);
        }
        catch (MongoWriteException exception)
            when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new CustomerAlreadyExistsException(customer.Email);
        }
    }

    public async Task<IReadOnlyList<Customer>> GetRecentAsync(
        int count,
        CancellationToken cancellationToken = default)
    {
        var safeCount = Math.Clamp(count, 1, 25);

        return await _customers
            .Find(Builders<Customer>.Filter.Empty)
            .SortByDescending(customer => customer.CreatedAtUtc)
            .Limit(safeCount)
            .ToListAsync(cancellationToken);
    }

    private async Task EnsureIndexesAsync(CancellationToken cancellationToken)
    {
        if (_indexesReady)
        {
            return;
        }

        await _indexLock.WaitAsync(cancellationToken);
        try
        {
            if (_indexesReady)
            {
                return;
            }

            var emailIndex = new CreateIndexModel<Customer>(
                Builders<Customer>.IndexKeys.Ascending(customer => customer.Email),
                new CreateIndexOptions
                {
                    Name = "ux_customers_email",
                    Unique = true
                });

            var createdAtIndex = new CreateIndexModel<Customer>(
                Builders<Customer>.IndexKeys.Descending(customer => customer.CreatedAtUtc),
                new CreateIndexOptions { Name = "ix_customers_createdAtUtc" });

            await _customers.Indexes.CreateManyAsync(
                [emailIndex, createdAtIndex],
                cancellationToken: cancellationToken);

            _indexesReady = true;
        }
        finally
        {
            _indexLock.Release();
        }
    }
}
