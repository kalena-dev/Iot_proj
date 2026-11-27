using System.Globalization;
using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using SmartStore.Options;

namespace SmartStore.Services;

public sealed class StoreAccount
{
    // Keep the legacy email _id: existing password records are never replaced by migration.
    [BsonId] public required string Email { get; init; }
    public required string PasswordHash { get; set; }
    public string AccountId { get; set; } = "";
    public string FullName { get; set; } = "";
    public string MembershipNumber { get; set; } = "";
    public string SecurityStamp { get; set; } = "";
    public bool Disabled { get; set; }
    public bool StaffProvisioned { get; set; }
    public DateTime? EmailVerifiedAtUtc { get; set; }
}

public sealed class StoreAccounts
{
    public const string StampClaim = "northstar:stamp";
    public const string StaffClaim = "northstar:staff-authenticated";
    public static readonly TimeSpan StaffLifetime = TimeSpan.FromMinutes(30);
    private readonly IMongoCollection<StoreAccount> accounts;
    private readonly IPasswordHasher<StoreAccount> passwords;
    private readonly AccountSecurityStore security;
    private readonly TimeProvider clock;
    private readonly Lazy<Task> indexes;

    public StoreAccounts(IMongoClient client, IOptions<MongoDbOptions> options,
        IPasswordHasher<StoreAccount> passwords, AccountSecurityStore security, TimeProvider clock)
    {
        accounts = client.GetDatabase(options.Value.DatabaseName).GetCollection<StoreAccount>("store_accounts");
        this.passwords = passwords;
        this.security = security;
        this.clock = clock;
        indexes = new Lazy<Task>(InitializeAsync);
    }

    private Task InitializeAsync() => accounts.Indexes.CreateManyAsync(
    [
        new CreateIndexModel<StoreAccount>(Builders<StoreAccount>.IndexKeys.Ascending(a => a.AccountId),
            new CreateIndexOptions<StoreAccount> { Unique = true,
                PartialFilterExpression = new BsonDocument("AccountId", new BsonDocument("$type", "string")) }),
        new CreateIndexModel<StoreAccount>(Builders<StoreAccount>.IndexKeys.Ascending(a => a.MembershipNumber),
            new CreateIndexOptions<StoreAccount> { Unique = true,
                PartialFilterExpression = new BsonDocument("MembershipNumber", new BsonDocument("$type", "string")) })
    ]);

    public static string? NormalizeEmail(string? value)
    {
        var email = value?.Trim().ToLowerInvariant();
        return email is { Length: > 0 and <= 254 } && MailAddress.TryCreate(email, out var address)
            && address.Address == email ? email : null;
    }

    public static string? AccountIdOf(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true && user.FindFirstValue(ClaimTypes.NameIdentifier) is { } id &&
        Guid.TryParseExact(id, "N", out _) ? id : null;

    public static string? EmailOf(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.Email);

    public static bool IsProvisionedId(string accountId, IConfiguration configuration) =>
        configuration["AUTH_ADMIN_ACCOUNT_IDS"]?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Contains(accountId, StringComparer.Ordinal) == true;

    public static bool IsStaff(ClaimsPrincipal user, IConfiguration configuration) =>
        IsStaffAt(user, configuration, DateTimeOffset.UtcNow);

    public static bool IsStaffAt(ClaimsPrincipal user, IConfiguration configuration, DateTimeOffset now)
    {
        if (AccountIdOf(user) is not { } id || !IsProvisionedId(id, configuration) ||
            !long.TryParse(user.FindFirstValue(StaffClaim), NumberStyles.None, CultureInfo.InvariantCulture, out var issued))
            return false;
        var age = now.ToUnixTimeSeconds() - issued;
        return age >= 0 && age < (long)StaffLifetime.TotalSeconds;
    }

    public static ClaimsPrincipal PrincipalFor(StoreAccount account, DateTimeOffset now, bool staffVerified = false)
    {
        if (!Guid.TryParseExact(account.AccountId, "N", out _) || string.IsNullOrEmpty(account.SecurityStamp) || account.Disabled)
            throw new InvalidOperationException("Account is not eligible for a session.");
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, account.AccountId), new(ClaimTypes.Email, account.Email),
            new(ClaimTypes.Name, account.FullName), new(StampClaim, account.SecurityStamp)
        };
        if (staffVerified && account.StaffProvisioned)
            claims.Add(new(StaffClaim, now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    public Task SignInAsync(HttpContext context, StoreAccount account, bool staffVerified = false) =>
        context.SignInAsync(PrincipalFor(account, clock.GetUtcNow(), staffVerified));

    public async Task<bool> ValidatePrincipalAsync(ClaimsPrincipal principal, IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (AccountIdOf(principal) is not { } id) return false;
        var account = await GetByIdAsync(id, cancellationToken);
        if (account is null || account.Disabled || account.SecurityStamp != principal.FindFirstValue(StampClaim))
            return false;
        return !principal.HasClaim(c => c.Type == StaffClaim) ||
            (account.StaffProvisioned && IsStaffAt(principal, configuration, clock.GetUtcNow()));
    }

    public StoreAccount Create(string email, string password, string fullName)
    {
        var account = new StoreAccount
        {
            Email = email, PasswordHash = "", AccountId = Guid.NewGuid().ToString("N"),
            FullName = fullName.Trim(), MembershipNumber = NewMembershipNumber(), SecurityStamp = NewStamp(),
            EmailVerifiedAtUtc = clock.GetUtcNow().UtcDateTime
        };
        account.PasswordHash = passwords.HashPassword(account, password);
        return account;
    }

    public async Task<bool> SaveAsync(StoreAccount account, CancellationToken cancellationToken)
    {
        await indexes.Value.WaitAsync(cancellationToken);
        try
        {
            await accounts.InsertOneAsync(account, cancellationToken: cancellationToken);
            return true;
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        { return false; }
    }

    public Task<StoreAccount?> GetByIdAsync(string accountId, CancellationToken cancellationToken) =>
        accounts.Find(a => a.AccountId == accountId).FirstOrDefaultAsync(cancellationToken)!;

    public Task<StoreAccount?> GetByEmailAsync(string email, CancellationToken cancellationToken) =>
        accounts.Find(a => a.Email == email).FirstOrDefaultAsync(cancellationToken)!;

    public async Task<StoreAccount?> AuthenticateAsync(string email, string password, CancellationToken cancellationToken)
    {
        if (!await security.TryUseQuotaAsync("password", email, 10, TimeSpan.FromMinutes(15), cancellationToken))
            return null;
        var account = await GetByEmailAsync(email, cancellationToken);
        if (account is null)
        {
            _ = passwords.HashPassword(new StoreAccount { Email = email, PasswordHash = "" }, password);
            return null;
        }
        var result = passwords.VerifyHashedPassword(account, account.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed || account.Disabled) return null;
        var authenticatedHash = account.PasswordHash;
        await indexes.Value.WaitAsync(cancellationToken);
        if (string.IsNullOrEmpty(account.AccountId))
        {
            // Only the authenticated credential record gains an ID; no email-based history/balance claim occurs.
            var missingId = Builders<StoreAccount>.Filter.Eq(a => a.Email, email) &
                Builders<StoreAccount>.Filter.Eq(a => a.PasswordHash, authenticatedHash) &
                Builders<StoreAccount>.Filter.Ne(a => a.Disabled, true) &
                (Builders<StoreAccount>.Filter.Exists(a => a.AccountId, false) |
                 Builders<StoreAccount>.Filter.Eq(a => a.AccountId, ""));
            await accounts.UpdateOneAsync(missingId, Builders<StoreAccount>.Update
                .Set(a => a.AccountId, Guid.NewGuid().ToString("N"))
                .Set(a => a.MembershipNumber, NewMembershipNumber()).Set(a => a.SecurityStamp, NewStamp()),
                cancellationToken: cancellationToken);
            account = (await GetByEmailAsync(email, cancellationToken))!;
            if (account.Disabled || account.PasswordHash != authenticatedHash) return null;
        }
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            var oldHash = account.PasswordHash;
            var newHash = passwords.HashPassword(account, password);
            await accounts.UpdateOneAsync(a => a.Email == email && a.PasswordHash == oldHash,
                Builders<StoreAccount>.Update.Set(a => a.PasswordHash, newHash), cancellationToken: cancellationToken);
        }
        return account;
    }

    public async Task<bool> ResetPasswordAsync(AccountChallenge challenge, string password, CancellationToken cancellationToken)
    {
        var account = await GetByEmailAsync(challenge.Email, cancellationToken);
        if (account is null || account.Disabled || account.SecurityStamp != challenge.SecurityStamp ||
            account.AccountId != challenge.AccountId) return false;
        // Legacy accounts are recoverable without assigning any historical email-owned data.
        var update = Builders<StoreAccount>.Update.Set(a => a.PasswordHash, passwords.HashPassword(account, password))
            .Set(a => a.SecurityStamp, NewStamp()).Set(a => a.EmailVerifiedAtUtc, clock.GetUtcNow().UtcDateTime);
        if (string.IsNullOrEmpty(account.AccountId))
        {
            await indexes.Value.WaitAsync(cancellationToken);
            update = update.Set(a => a.AccountId, Guid.NewGuid().ToString("N"))
                .Set(a => a.MembershipNumber, NewMembershipNumber());
        }
        var unchanged = Builders<StoreAccount>.Filter.Eq(a => a.Email, account.Email) &
            Builders<StoreAccount>.Filter.Ne(a => a.Disabled, true) &
            Builders<StoreAccount>.Filter.Eq(a => a.PasswordHash, account.PasswordHash);
        unchanged &= string.IsNullOrEmpty(account.SecurityStamp)
            ? Builders<StoreAccount>.Filter.Exists(a => a.SecurityStamp, false) |
              Builders<StoreAccount>.Filter.Eq(a => a.SecurityStamp, "")
            : Builders<StoreAccount>.Filter.Eq(a => a.SecurityStamp, account.SecurityStamp);
        var result = await accounts.UpdateOneAsync(unchanged, update, cancellationToken: cancellationToken);
        return result.ModifiedCount == 1;
    }

    public Task RevokeSessionsAsync(string accountId, CancellationToken cancellationToken) =>
        accounts.UpdateOneAsync(a => a.AccountId == accountId,
            Builders<StoreAccount>.Update.Set(a => a.SecurityStamp, NewStamp()), cancellationToken: cancellationToken);

    public async Task<bool> SetDisabledAsync(string accountId, bool disabled, CancellationToken cancellationToken)
    {
        var result = await accounts.UpdateOneAsync(a => a.AccountId == accountId,
            Builders<StoreAccount>.Update.Set(a => a.Disabled, disabled).Set(a => a.SecurityStamp, NewStamp()),
            cancellationToken: cancellationToken);
        return result.MatchedCount == 1;
    }

    public async Task<bool> ProvisionStaffAsync(string accountId, IConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!IsProvisionedId(accountId, configuration)) return false;
        var enabledAccount = Builders<StoreAccount>.Filter.Eq(a => a.AccountId, accountId) &
            Builders<StoreAccount>.Filter.Ne(a => a.Disabled, true);
        var result = await accounts.UpdateOneAsync(enabledAccount,
            Builders<StoreAccount>.Update.Set(a => a.StaffProvisioned, true).Set(a => a.SecurityStamp, NewStamp()),
            cancellationToken: cancellationToken);
        return result.MatchedCount == 1;
    }

    public async Task<bool> UpdateNameAsync(string accountId, string fullName, CancellationToken cancellationToken)
    {
        var enabledAccount = Builders<StoreAccount>.Filter.Eq(a => a.AccountId, accountId) &
            Builders<StoreAccount>.Filter.Ne(a => a.Disabled, true);
        var result = await accounts.UpdateOneAsync(enabledAccount,
            Builders<StoreAccount>.Update.Set(a => a.FullName, fullName.Trim()), cancellationToken: cancellationToken);
        return result.MatchedCount == 1;
    }

    private static string NewStamp() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static string NewMembershipNumber() => "NS-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(10));
}
