using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using SmartStore.Data;
using SmartStore.Models;
using SmartStore.Options;
using SmartStore.Services;

namespace SmartStore.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class MongoReplicaSetFactAttribute : FactAttribute
{
    public MongoReplicaSetFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(MongoTestDatabase.UriVariable)))
        {
            Skip = $"Set {MongoTestDatabase.UriVariable} to a local MongoDB replica-set URI to run persistence tests.";
        }
    }
}

internal sealed class MongoTestDatabase : IAsyncDisposable
{
    public const string UriVariable = "SMARTSTORE_TEST_MONGODB_URI";
    private readonly CancellationTokenSource _lifetime = new(TimeSpan.FromSeconds(60));
    private readonly MongoClient _client;
    private readonly MongoClient _observer;
    private readonly IMongoDatabase _database;
    private readonly IMongoDatabase _observerDatabase;

    private MongoTestDatabase(MongoUrl url)
    {
        Name = $"smart_store_tests_{Guid.NewGuid():N}";
        Gate = new MongoCommandGate(Name);
        var settings = MongoClientSettings.FromUrl(url);
        settings.ApplicationName = Name;
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
        settings.ConnectTimeout = TimeSpan.FromSeconds(5);
        settings.SocketTimeout = TimeSpan.FromSeconds(10);
        settings.WaitQueueTimeout = TimeSpan.FromSeconds(10);
        settings.ReadPreference = ReadPreference.Primary;
        settings.ReadConcern = ReadConcern.Majority;
        settings.WriteConcern = WriteConcern.WMajority;
        settings.ClusterConfigurator = cluster =>
        {
            cluster.Subscribe<CommandStartedEvent>(Gate.CommandStarted);
            cluster.Subscribe<CommandSucceededEvent>(Gate.CommandSucceeded);
            cluster.Subscribe<CommandFailedEvent>(Gate.CommandFailed);
        };
        _client = new MongoClient(settings);
        var observerSettings = settings.Clone();
        observerSettings.ApplicationName = $"{Name}_observer";
        observerSettings.ClusterConfigurator = null;
        _observer = new MongoClient(observerSettings);
        _database = _client.GetDatabase(Name);
        _observerDatabase = _observer.GetDatabase(Name);
        var options = Microsoft.Extensions.Options.Options.Create(new MongoDbOptions { DatabaseName = Name });
        Products = new MongoProductRepository(_client, options);
        Customers = new MongoCustomerRepository(_client, options);
        Store = new StoreDemo(_client, options, Products, Customers, NullLogger<StoreDemo>.Instance);
    }

    public string Name { get; }
    public CancellationToken Token => _lifetime.Token;
    public MongoProductRepository Products { get; }
    public MongoCustomerRepository Customers { get; }
    public StoreDemo Store { get; }
    public MongoCommandGate Gate { get; }

    public static async Task<MongoTestDatabase> CreateAsync()
    {
        var uri = Environment.GetEnvironmentVariable(UriVariable);
        if (string.IsNullOrWhiteSpace(uri))
            throw new InvalidOperationException($"{UriVariable} must be configured; these tests have no default server.");
        if (!uri.StartsWith("mongodb://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Persistence tests require a mongodb:// URI with loopback hosts; SRV/Atlas connections are not allowed.");
        var url = new MongoUrl(uri);
        if (url.Servers.Any(server => !IsLoopback(server.Host)))
            throw new InvalidOperationException("Persistence tests only allow localhost or loopback IP addresses, never a remote/Atlas database.");

        var database = new MongoTestDatabase(url);
        try
        {
            var hello = await database._client.GetDatabase("admin").RunCommandAsync<BsonDocument>(
                new BsonDocument("hello", 1), cancellationToken: database.Token);
            if (!hello.TryGetValue("setName", out var setName) || string.IsNullOrWhiteSpace(setName.AsString))
                throw new InvalidOperationException("Persistence tests require a replica set; a standalone mongod cannot run these transactions.");

            foreach (var collection in new[] { "products", "receptions", "customers", "demo_sales", "email_log" })
                await database._database.CreateCollectionAsync(collection, cancellationToken: database.Token);
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    public static Product NewProduct(int ordinal = 1, decimal price = 12.75m) => new()
    {
        Name = $"Integration product {ordinal}",
        Category = "Integration tests",
        Price = price,
        Upc = (1_000_000_000_000L + ordinal).ToString(CultureInfo.InvariantCulture),
        Epc = $"A{ordinal:X23}",
        Producer = "Test producer",
        CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        UpdatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    public async Task<Product> AddProductAsync(int stock = 0, int ordinal = 1, decimal price = 12.75m)
    {
        var product = NewProduct(ordinal, price);
        await Products.AddAsync(product, Token);
        if (stock > 0)
            await Products.ReceiveStockAsync(NewReception(product, stock), Token);
        return product;
    }

    public static Reception NewReception(Product product, int quantity, int day = 1) => new()
    {
        ProductId = product.Id ?? throw new InvalidOperationException("Save the product before receiving it."),
        Quantity = quantity,
        ReceivedOn = new DateTime(2026, 1, day, 0, 0, 0, DateTimeKind.Utc),
        ExpiresOn = new DateTime(2026, 2, day, 0, 0, 0, DateTimeKind.Utc),
        CreatedAtUtc = new DateTime(2026, 1, day, 12, 0, 0, DateTimeKind.Utc),
        Note = $"Delivery {day}"
    };

    public async Task<Customer> AddCustomerAsync(int points = 7)
    {
        var customer = new Customer
        {
            FirstName = "Integration",
            LastName = "Customer",
            Email = "customer@example.test",
            Phone = "5145550101",
            Address = new CustomerAddress
            {
                Street = "1 Test Street", City = "Montreal", Province = "QC", PostalCode = "H2X 1Y4"
            },
            TotalPoints = points,
            CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        await Customers.AddAsync(customer, Token);
        return customer;
    }

    // All assertions read through a separate client, outside the operation's transaction.
    public Task<List<Product>> ReadProductsAsync() =>
        _observerDatabase.GetCollection<Product>("products").Find(FilterDefinition<Product>.Empty).ToListAsync(Token);

    public async Task<Product?> ReadProductAsync(Product product) =>
        await _observerDatabase.GetCollection<Product>("products").Find(p => p.Id == product.Id).FirstOrDefaultAsync(Token);

    public Task<Customer> ReadCustomerAsync(Customer customer) =>
        _observerDatabase.GetCollection<Customer>("customers").Find(c => c.Id == customer.Id).SingleAsync(Token);

    public Task<List<DemoSale>> ReadSalesAsync() =>
        _observerDatabase.GetCollection<DemoSale>("demo_sales").Find(FilterDefinition<DemoSale>.Empty).ToListAsync(Token);

    public Task<List<Reception>> ReadReceptionsAsync(Product product) =>
        _observerDatabase.GetCollection<Reception>("receptions").Find(r => r.ProductId == product.Id).ToListAsync(Token);

    public Task RejectWritesAsync(string collection) => _database.RunCommandAsync<BsonDocument>(
        new BsonDocument
        {
            { "collMod", collection },
            { "validator", new BsonDocument("$jsonSchema", new BsonDocument
                {
                    { "bsonType", "object" },
                    { "required", new BsonArray { "rejectedByIntegrationTest" } }
                })
            },
            { "validationLevel", "strict" },
            { "validationAction", "error" }
        }, cancellationToken: Token);

    public async ValueTask DisposeAsync()
    {
        Gate.Release();
        _lifetime.Cancel();
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            // Name is generated here, never read from the URI or application configuration.
            await _client.DropDatabaseAsync(Name, cleanup.Token);
        }
        finally
        {
            _client.Dispose();
            _observer.Dispose();
            Gate.Dispose();
            _lifetime.Dispose();
        }
    }

    private static bool IsLoopback(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);
}

// Monitoring is only a scheduling seam: assertions concern externally visible persisted state.
internal sealed class MongoCommandGate(string databaseName) : IDisposable
{
    private static readonly TimeSpan PauseLimit = TimeSpan.FromSeconds(15);
    private readonly ConcurrentDictionary<int, byte> _requests = new();
    private readonly TaskCompletionSource<bool> _paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _released = new(false);
    private string? _command;
    private int _armed;
    private int _used;
    private int _timedOut;

    public bool TimedOut => Volatile.Read(ref _timedOut) != 0;

    public void ArmAfterProductWrite(string command)
    {
        if (command is not ("update" or "delete")) throw new ArgumentOutOfRangeException(nameof(command));
        if (Interlocked.Exchange(ref _used, 1) != 0) throw new InvalidOperationException("A test gate can only be armed once.");
        _command = command;
        Volatile.Write(ref _armed, 1);
    }

    public void CommandStarted(CommandStartedEvent command)
    {
        if (Volatile.Read(ref _armed) == 1 && command.DatabaseNamespace.DatabaseName == databaseName &&
            command.CommandName == _command && command.Command.GetValue(command.CommandName, BsonNull.Value) == "products")
        {
            _requests.TryAdd(command.RequestId, 0);
        }
    }

    public void CommandSucceeded(CommandSucceededEvent command)
    {
        if (!_requests.TryRemove(command.RequestId, out _) || command.Reply.GetValue("n", 0).ToInt64() < 1 ||
            Interlocked.CompareExchange(ref _armed, 0, 1) != 1)
            return;
        _paused.TrySetResult(true);
        if (!_released.Wait(PauseLimit)) Volatile.Write(ref _timedOut, 1);
    }

    public void CommandFailed(CommandFailedEvent command) => _requests.TryRemove(command.RequestId, out _);

    public Task WaitUntilPausedAsync(CancellationToken cancellationToken) =>
        _paused.Task.WaitAsync(PauseLimit, cancellationToken);

    public void Release() => _released.Set();

    public void Dispose()
    {
        Release();
        _released.Dispose();
    }
}
