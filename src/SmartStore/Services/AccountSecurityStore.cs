using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using SmartStore.Options;

namespace SmartStore.Services;

public sealed class AccountChallenge
{
    [BsonId] public string Id { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string Email { get; set; } = "";
    public string? AccountId { get; set; }
    public string? SecurityStamp { get; set; }
    public StoreAccount? Registration { get; set; }
    public string CodeHash { get; set; } = "";
    public DateTime ExpiresAtUtc { get; set; }
}

/// <summary>Mongo-backed quotas and single-use challenges survive restarts and multiple web workers.</summary>
public sealed class AccountSecurityStore
{
    private readonly IMongoCollection<BsonDocument> limits;
    private readonly IMongoCollection<AccountChallenge> challenges;
    private readonly TimeProvider clock;
    private readonly Lazy<Task> indexes;

    public AccountSecurityStore(IMongoClient client, IOptions<MongoDbOptions> options, TimeProvider clock)
    {
        var database = client.GetDatabase(options.Value.DatabaseName);
        limits = database.GetCollection<BsonDocument>("account_limits");
        challenges = database.GetCollection<AccountChallenge>("account_challenges");
        this.clock = clock;
        indexes = new Lazy<Task>(InitializeAsync);
    }

    private async Task InitializeAsync()
    {
        await limits.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("ExpiresAtUtc"),
            new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }));
        await challenges.Indexes.CreateOneAsync(new CreateIndexModel<AccountChallenge>(
            Builders<AccountChallenge>.IndexKeys.Ascending(c => c.ExpiresAtUtc),
            new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }));
    }

    public async Task<bool> TryUseQuotaAsync(string scope, string key, int maximum, TimeSpan window,
        CancellationToken cancellationToken)
    {
        await indexes.Value.WaitAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var bucket = now.ToUnixTimeSeconds() / (long)window.TotalSeconds;
        // Do not persist mailbox addresses or user-controlled identifiers in quota keys.
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        var id = $"{scope}:{digest}:{bucket}";
        var filter = Builders<BsonDocument>.Filter.Eq("_id", id) &
            Builders<BsonDocument>.Filter.Lt("Count", maximum);
        var update = Builders<BsonDocument>.Update.Inc("Count", 1)
            .SetOnInsert("ExpiresAtUtc", now.Add(window + window).UtcDateTime);
        try
        {
            await limits.FindOneAndUpdateAsync(filter, update,
                new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true }, cancellationToken);
            return true;
        }
        catch (MongoCommandException exception) when (exception.Code == 11000) { return false; }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        { return false; }
    }

    public async Task<bool> TrySendMailAsync(string email, CancellationToken cancellationToken) =>
        await TryUseQuotaAsync("mail-recipient", email, 5, TimeSpan.FromHours(1), cancellationToken) &&
        await TryUseQuotaAsync("mail-global", "all", 100, TimeSpan.FromHours(1), cancellationToken);

    public async Task<(string Id, string Code)> CreateChallengeAsync(string purpose, string email,
        StoreAccount? account, StoreAccount? registration, CancellationToken cancellationToken)
    {
        await indexes.Value.WaitAsync(cancellationToken);
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        await challenges.InsertOneAsync(new AccountChallenge
        {
            Id = id, Purpose = purpose, Email = email, AccountId = account?.AccountId,
            SecurityStamp = account?.SecurityStamp, Registration = registration,
            CodeHash = HashCode(id, code), ExpiresAtUtc = clock.GetUtcNow().AddMinutes(15).UtcDateTime
        }, cancellationToken: cancellationToken);
        return (id, code);
    }

    public async Task<AccountChallenge?> ConsumeChallengeAsync(string id, string purpose, string? code,
        CancellationToken cancellationToken)
    {
        if (id.Length != 64 || !await TryUseQuotaAsync("verification", id, 5, TimeSpan.FromMinutes(15), cancellationToken))
            return null;
        var normalized = code?.Trim().ToUpperInvariant();
        if (normalized is not { Length: 32 }) return null;
        var hash = HashCode(id, normalized);
        var now = clock.GetUtcNow().UtcDateTime;
        return await challenges.FindOneAndDeleteAsync(c => c.Id == id && c.Purpose == purpose &&
            c.CodeHash == hash && c.ExpiresAtUtc > now, cancellationToken: cancellationToken);
    }

    public Task DeleteChallengeAsync(string id, CancellationToken cancellationToken) =>
        challenges.DeleteOneAsync(c => c.Id == id, cancellationToken);

    private static string HashCode(string id, string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id + ":" + code)));
}
