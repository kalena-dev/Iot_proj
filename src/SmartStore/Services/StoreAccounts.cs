using System.Net.Mail;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using SmartStore.Options;

namespace SmartStore.Services;

public sealed class StoreAccount
{
    [BsonId]
    public required string Email { get; init; }
    public required string PasswordHash { get; set; }
}

public sealed class StoreAccounts(IMongoClient client, IOptions<MongoDbOptions> options,
    IPasswordHasher<StoreAccount> passwords)
{
    private readonly IMongoCollection<StoreAccount> accounts = client.GetDatabase(options.Value.DatabaseName)
        .GetCollection<StoreAccount>("store_accounts");

    public static string? NormalizeEmail(string? value)
    {
        var email = value?.Trim().ToLowerInvariant();
        return email is { Length: > 0 and <= 254 } && MailAddress.TryCreate(email, out var address)
            && address.Address == email ? email : null;
    }

    public static string? EmailOf(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.Email);

    public static bool IsStaff(ClaimsPrincipal user, IConfiguration configuration) =>
        user.Identity?.IsAuthenticated == true && EmailOf(user) is { } email
        && email == NormalizeEmail(configuration["AUTH_ADMIN_EMAIL"]);

    public static Task SignInAsync(HttpContext context, string email)
    {
        var identity = new ClaimsIdentity(
            [new(ClaimTypes.NameIdentifier, email), new(ClaimTypes.Email, email)],
            CookieAuthenticationDefaults.AuthenticationScheme);
        return context.SignInAsync(new ClaimsPrincipal(identity));
    }

    public StoreAccount Create(string email, string password)
    {
        var account = new StoreAccount { Email = email, PasswordHash = "" };
        account.PasswordHash = passwords.HashPassword(account, password);
        return account;
    }

    public async Task<bool> SaveAsync(StoreAccount account, CancellationToken cancellationToken)
    {
        try
        {
            await accounts.InsertOneAsync(account, cancellationToken: cancellationToken);
            return true;
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public async Task<bool> VerifyPasswordAsync(string email, string password, CancellationToken cancellationToken)
    {
        var account = await accounts.Find(a => a.Email == email).FirstOrDefaultAsync(cancellationToken);
        if (account is null)
        {
            // Perform equivalent password hashing work for an unknown account.
            _ = Create(email, password);
            return false;
        }

        var result = passwords.VerifyHashedPassword(account, account.PasswordHash, password);
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            var oldHash = account.PasswordHash;
            var newHash = passwords.HashPassword(account, password);
            await accounts.UpdateOneAsync(a => a.Email == email && a.PasswordHash == oldHash,
                Builders<StoreAccount>.Update.Set(a => a.PasswordHash, newHash),
                cancellationToken: cancellationToken);
        }
        return result != PasswordVerificationResult.Failed;
    }
}
