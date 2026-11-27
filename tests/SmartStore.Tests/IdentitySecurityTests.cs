using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using SmartStore.Options;
using SmartStore.Pages;
using SmartStore.Services;

namespace SmartStore.Tests;

public sealed class IdentitySecurityTests
{
    private const string Password = "correct horse battery staple";
    private static readonly string AccountId = Guid.NewGuid().ToString("N");
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("  CUSTOMER@example.test  ", "customer@example.test")]
    [InlineData("person <person@example.test>", null)]
    [InlineData("not-an-address", null)]
    public void EmailNormalizationDoesNotAcceptDisplayNames(string input, string? expected) =>
        Assert.Equal(expected, StoreAccounts.NormalizeEmail(input));

    [Fact]
    public void EmailAllowlistAndLegacyEmailSubjectNeverGrantStaffOrStableOwnership()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "staff@example.test"),
            new Claim(ClaimTypes.Email, "staff@example.test")], "Cookies"));
        var config = Configuration(new() { ["AUTH_ADMIN_EMAIL"] = "staff@example.test", ["AUTH_ADMIN_EMAILS"] = "staff@example.test" });
        Assert.Null(StoreAccounts.AccountIdOf(user));
        Assert.False(StoreAccounts.IsStaffAt(user, config, Now));
    }

    [Fact]
    public void AddingAnAccountIdNeverElevatesAnExistingCustomerSession()
    {
        var account = Account();
        var principal = StoreAccounts.PrincipalFor(account, Now);
        Assert.Equal(AccountId, StoreAccounts.AccountIdOf(principal));
        Assert.False(StoreAccounts.IsStaffAt(principal, StaffConfiguration(), Now));
        account.StaffProvisioned = true;
        Assert.False(StoreAccounts.IsStaffAt(principal, StaffConfiguration(), Now));
        Assert.False(StoreAccounts.IsStaffAt(StoreAccounts.PrincipalFor(account, Now), StaffConfiguration(), Now));
    }

    [Fact]
    public void StaffRequiresProvisioningExplicitIdAndFreshSecondFactor()
    {
        var account = Account();
        var notProvisioned = StoreAccounts.PrincipalFor(account, Now, staffVerified: true);
        Assert.False(StoreAccounts.IsStaffAt(notProvisioned, StaffConfiguration(), Now));
        account.StaffProvisioned = true;
        var principal = StoreAccounts.PrincipalFor(account, Now, staffVerified: true);
        Assert.True(StoreAccounts.IsStaffAt(principal, StaffConfiguration(), Now));
        Assert.False(StoreAccounts.IsStaffAt(principal, Configuration(new()), Now));
        Assert.False(StoreAccounts.IsStaffAt(principal, StaffConfiguration(), Now.AddSeconds(-1)));
        Assert.False(StoreAccounts.IsStaffAt(principal, StaffConfiguration(), Now.Add(StoreAccounts.StaffLifetime)));
    }

    [Theory]
    [InlineData("http", "127.0.0.1", "GET", "/Login", false)]
    [InlineData("http", "127.0.0.1", "GET", "/Checkout", false)]
    [InlineData("http", "127.0.0.1", "POST", "/Register", false)]
    [InlineData("http", "127.0.0.1", "GET", "/health", true)]
    [InlineData("http", "192.0.2.1", "GET", "/health", false)]
    [InlineData("http", "127.0.0.1", "GET", "/css/site.css", true)]
    [InlineData("http", "127.0.0.1", "GET", "/css-trick/Login", false)]
    [InlineData("https", "192.0.2.1", "POST", "/Register", true)]
    public void HttpCannotDowngradeAuthentication(string scheme, string remote, string method, string path, bool allowed)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;
        context.Request.Method = method;
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        // A request header is not a trusted proxy and cannot change the effective scheme.
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        Assert.Equal(allowed, AccountTransportSecurity.Allows(context));
    }

    [Fact]
    public async Task InvalidRegistrationRequiresNameBeforeAccessingDatabase()
    {
        var services = Services(new MongoClient("mongodb://127.0.0.1:1"), "not_accessed", new Clock());
        var page = Prepare(new RegisterModel(services.Accounts, services.Security, Mailer(services.Security))
        { Email = "new@example.test", Password = Password, FullName = " " });
        Assert.IsType<PageResult>(await page.OnPostAsync(default));
        Assert.Contains(nameof(RegisterModel.FullName), page.ModelState.Keys);
    }

    [MongoReplicaSetFact]
    public async Task RealRegistrationVerificationSavesNameAndMembershipAndIssuesSecureCookie()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var (accounts, security, _) = Services(Client(), db.Name, new Clock());
        var candidate = accounts.Create("registered@example.test", Password, "Registered Member");
        var token = await security.CreateChallengeAsync("registration", candidate.Email, null, candidate, db.Token);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options => options.Cookie.SecurePolicy = CookieSecurePolicy.Always);
        using var provider = services.BuildServiceProvider();
        var page = Prepare(new RegisterModel(accounts, security, Mailer(security)) { Code = token.Code });
        page.HttpContext.RequestServices = provider;
        page.HttpContext.Request.Scheme = "https";
        page.TempData["PendingRegistration"] = token.Id;
        var result = Assert.IsType<RedirectToPageResult>(await page.OnPostVerifyAsync(db.Token));
        Assert.Equal("/Profile", result.PageName);
        var saved = (await accounts.GetByEmailAsync(candidate.Email, db.Token))!;
        Assert.Equal("Registered Member", saved.FullName);
        Assert.Equal(candidate.MembershipNumber, saved.MembershipNumber);
        Assert.Equal(candidate.AccountId, saved.AccountId);
        Assert.Contains(page.Response.Headers.SetCookie, header => header!.Contains("secure", StringComparison.OrdinalIgnoreCase));
        Assert.Null(await security.ConsumeChallengeAsync(token.Id, "registration", token.Code, db.Token));
    }

    [MongoReplicaSetFact]
    public async Task RegistrationGeneratesImmutableMembershipAndDoesNotClaimEmailHistory()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var (accounts, security, database) = Services(Client(), db.Name, new Clock());
        var legacy = new BsonDocument { ["_id"] = "member@example.test", ["LoyaltyPoints"] = 450 };
        await database.GetCollection<BsonDocument>("customers").InsertOneAsync(legacy, cancellationToken: db.Token);
        var first = accounts.Create("member@example.test", Password, "  Ada Member  ");
        var second = accounts.Create("other@example.test", Password, "Other Member");
        Assert.True(await accounts.SaveAsync(first, db.Token));
        Assert.True(await accounts.SaveAsync(second, db.Token));
        Assert.False(await accounts.SaveAsync(accounts.Create(first.Email, Password, "Impostor"), db.Token));
        Assert.Equal("Ada Member", first.FullName);
        Assert.NotEqual(first.AccountId, second.AccountId);
        Assert.NotEqual(first.MembershipNumber, second.MembershipNumber);
        Assert.StartsWith("NS-", first.MembershipNumber);
        Assert.NotEqual(Password, first.PasswordHash);
        Assert.Equal(first.AccountId, (await accounts.AuthenticateAsync(first.Email, Password, db.Token))!.AccountId);
        var preserved = await database.GetCollection<BsonDocument>("customers").Find(legacy).SingleAsync(db.Token);
        Assert.Equal(450, preserved["LoyaltyPoints"].AsInt32);
        Assert.Equal(0, await database.GetCollection<BsonDocument>("account_balances").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: db.Token));
    }

    [MongoReplicaSetFact]
    public async Task LegacyCredentialsMigrateOnceWithoutChangingHashOrClaimingLegacyData()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var clock = new Clock();
        var client = Client();
        var (accounts, _, database) = Services(client, db.Name, clock);
        var old = Account();
        old.PasswordHash = new PasswordHasher<StoreAccount>().HashPassword(old, Password);
        await database.GetCollection<BsonDocument>("store_accounts").InsertOneAsync(
            new BsonDocument { ["_id"] = old.Email, ["PasswordHash"] = old.PasswordHash }, cancellationToken: db.Token);
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => accounts.AuthenticateAsync(old.Email, Password, db.Token)));
        Assert.All(results, account => Assert.NotNull(account));
        Assert.Single(results.Select(a => a!.AccountId).Distinct());
        Assert.All(results, account => Assert.False(account!.StaffProvisioned));
        var saved = await accounts.GetByEmailAsync(old.Email, db.Token);
        Assert.Equal(old.PasswordHash, saved!.PasswordHash);
        Assert.False(string.IsNullOrEmpty(saved.SecurityStamp));
        Assert.False(string.IsNullOrEmpty(saved.MembershipNumber));
        Assert.Equal("", saved.FullName);
        Assert.True(await accounts.UpdateNameAsync(saved.AccountId, "Legacy Member", db.Token));
        Assert.Equal("Legacy Member", (await accounts.GetByIdAsync(saved.AccountId, db.Token))!.FullName);
    }

    [MongoReplicaSetFact]
    public async Task StampRevocationDisableAndEnableInvalidateCopiedTickets()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var (accounts, _, _) = Services(Client(), db.Name, new Clock());
        var account = accounts.Create("sessions@example.test", Password, "Session Member");
        await accounts.SaveAsync(account, db.Token);
        var principal = StoreAccounts.PrincipalFor(account, Now);
        Assert.True(await accounts.ValidatePrincipalAsync(principal, Configuration(new()), db.Token));
        await accounts.RevokeSessionsAsync(account.AccountId, db.Token);
        Assert.False(await accounts.ValidatePrincipalAsync(principal, Configuration(new()), db.Token));
        account = (await accounts.GetByIdAsync(account.AccountId, db.Token))!;
        principal = StoreAccounts.PrincipalFor(account, Now);
        Assert.True(await accounts.SetDisabledAsync(account.AccountId, true, db.Token));
        Assert.Null(await accounts.AuthenticateAsync(account.Email, Password, db.Token));
        Assert.False(await accounts.ValidatePrincipalAsync(principal, Configuration(new()), db.Token));
        Assert.True(await accounts.SetDisabledAsync(account.AccountId, false, db.Token));
        Assert.False(await accounts.ValidatePrincipalAsync(principal, Configuration(new()), db.Token));
        Assert.NotNull(await accounts.AuthenticateAsync(account.Email, Password, db.Token));
    }

    [MongoReplicaSetFact]
    public async Task PasswordPacingPersistsAcrossWorkersAndExpires()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var clock = new Clock();
        var client = Client();
        var (accounts, _, _) = Services(client, db.Name, clock);
        var otherWorker = Services(client, db.Name, clock).Accounts;
        var account = accounts.Create("paced@example.test", Password, "Paced Member");
        await accounts.SaveAsync(account, db.Token);
        for (var i = 0; i < 10; i++) Assert.Null(await accounts.AuthenticateAsync(account.Email, "incorrect", db.Token));
        Assert.Null(await otherWorker.AuthenticateAsync(account.Email, Password, db.Token));
        clock.Advance(TimeSpan.FromMinutes(16));
        Assert.NotNull(await otherWorker.AuthenticateAsync(account.Email, Password, db.Token));
    }

    [MongoReplicaSetFact]
    public async Task PersistedQuotasAreAtomicAndMailHasRecipientAndGlobalLimits()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var clock = new Clock();
        var client = Client();
        var first = Services(client, db.Name, clock).Security;
        var second = Services(client, db.Name, clock).Security;
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => (i % 2 == 0 ? first : second)
            .TryUseQuotaAsync("atomic", "account", 3, TimeSpan.FromHours(1), db.Token)));
        Assert.Equal(3, results.Count(x => x));
        for (var i = 0; i < 5; i++) Assert.True(await first.TrySendMailAsync("one@example.test", db.Token));
        Assert.False(await second.TrySendMailAsync("one@example.test", db.Token));
        var global = await Task.WhenAll(Enumerable.Range(0, 100).Select(i => second.TrySendMailAsync($"recipient{i}@example.test", db.Token)));
        Assert.Equal(95, global.Count(x => x));
        clock.Advance(TimeSpan.FromHours(1));
        Assert.True(await second.TrySendMailAsync("one@example.test", db.Token));
    }

    [MongoReplicaSetFact]
    public async Task ChallengesAreHashedPurposeBoundExpiringAttemptLimitedAndSingleUse()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var clock = new Clock();
        var (_, security, database) = Services(Client(), db.Name, clock);
        var token = await security.CreateChallengeAsync("recovery", "member@example.test", null, null, db.Token);
        var stored = await database.GetCollection<BsonDocument>("account_challenges").Find(new BsonDocument("_id", token.Id)).SingleAsync(db.Token);
        Assert.DoesNotContain(token.Code, stored.ToJson());
        Assert.Null(await security.ConsumeChallengeAsync(token.Id, "registration", token.Code, db.Token));
        var winners = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => security.ConsumeChallengeAsync(token.Id, "recovery", token.Code.ToLowerInvariant(), db.Token)));
        Assert.Single(winners, result => result is not null);
        var limited = await security.CreateChallengeAsync("recovery", "member@example.test", null, null, db.Token);
        for (var i = 0; i < 5; i++) Assert.Null(await security.ConsumeChallengeAsync(limited.Id, "recovery", "wrong", db.Token));
        Assert.Null(await security.ConsumeChallengeAsync(limited.Id, "recovery", limited.Code, db.Token));
        var expired = await security.CreateChallengeAsync("recovery", "member@example.test", null, null, db.Token);
        clock.Advance(TimeSpan.FromMinutes(15));
        Assert.Null(await security.ConsumeChallengeAsync(expired.Id, "recovery", expired.Code, db.Token));
    }

    [MongoReplicaSetFact]
    public async Task RecoveryRotatesStampRejectsStaleChallengesAndPreservesAccountIdentity()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var (accounts, security, _) = Services(Client(), db.Name, new Clock());
        var account = accounts.Create("recovery@example.test", Password, "Recovery Member");
        await accounts.SaveAsync(account, db.Token);
        var token = await security.CreateChallengeAsync("recovery", account.Email, account, null, db.Token);
        var stale = await security.CreateChallengeAsync("recovery", account.Email, account, null, db.Token);
        var challenge = await security.ConsumeChallengeAsync(token.Id, "recovery", token.Code, db.Token);
        Assert.True(await accounts.ResetPasswordAsync(challenge!, "a replacement password", db.Token));
        var staleChallenge = await security.ConsumeChallengeAsync(stale.Id, "recovery", stale.Code, db.Token);
        Assert.False(await accounts.ResetPasswordAsync(staleChallenge!, "another replacement password", db.Token));
        Assert.False(await accounts.ValidatePrincipalAsync(StoreAccounts.PrincipalFor(account, Now), Configuration(new()), db.Token));
        Assert.Null(await accounts.AuthenticateAsync(account.Email, Password, db.Token));
        var recovered = await accounts.AuthenticateAsync(account.Email, "a replacement password", db.Token);
        Assert.Equal(account.AccountId, recovered!.AccountId);
        Assert.Equal(account.MembershipNumber, recovered.MembershipNumber);
    }

    [MongoReplicaSetFact]
    public async Task LegacyRecoveryWorksForMissingStampAndDoesNotOverwriteCredentialsWithoutCode()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var (accounts, security, database) = Services(Client(), db.Name, new Clock());
        var old = Account();
        old.PasswordHash = new PasswordHasher<StoreAccount>().HashPassword(old, Password);
        await database.GetCollection<BsonDocument>("store_accounts").InsertOneAsync(
            new BsonDocument { ["_id"] = old.Email, ["PasswordHash"] = old.PasswordHash }, cancellationToken: db.Token);
        var existing = (await accounts.GetByEmailAsync(old.Email, db.Token))!;
        var token = await security.CreateChallengeAsync("recovery", old.Email, existing, null, db.Token);
        var challenge = await security.ConsumeChallengeAsync(token.Id, "recovery", token.Code, db.Token);
        Assert.True(await accounts.ResetPasswordAsync(challenge!, "legacy replacement password", db.Token));
        var recovered = await accounts.AuthenticateAsync(old.Email, "legacy replacement password", db.Token);
        Assert.NotNull(recovered);
        Assert.True(Guid.TryParseExact(recovered.AccountId, "N", out _));
        Assert.StartsWith("NS-", recovered.MembershipNumber);
    }

    [MongoReplicaSetFact]
    public async Task ProvisioningRevokesOldSessionAndFreshStaffTicketIsValidatedServerSide()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var (accounts, security, _) = Services(Client(), db.Name, new Clock());
        var account = accounts.Create("staff@example.test", Password, "Staff Member");
        await accounts.SaveAsync(account, db.Token);
        var config = Configuration(new() { ["AUTH_ADMIN_ACCOUNT_IDS"] = account.AccountId });
        var customer = StoreAccounts.PrincipalFor(account, Now);
        Assert.False(await accounts.ProvisionStaffAsync(account.AccountId, Configuration(new()), db.Token));
        Assert.True(await accounts.ProvisionStaffAsync(account.AccountId, config, db.Token));
        Assert.False(await accounts.ValidatePrincipalAsync(customer, config, db.Token));
        account = (await accounts.AuthenticateAsync(account.Email, Password, db.Token))!;
        Assert.False(StoreAccounts.IsStaffAt(StoreAccounts.PrincipalFor(account, Now), config, Now));
        var token = await security.CreateChallengeAsync("staff-login", account.Email, account, null, db.Token);
        Assert.NotNull(await security.ConsumeChallengeAsync(token.Id, "staff-login", token.Code, db.Token));
        var staff = StoreAccounts.PrincipalFor(account, Now, staffVerified: true);
        Assert.True(await accounts.ValidatePrincipalAsync(staff, config, db.Token));
        Assert.False(await accounts.ValidatePrincipalAsync(staff, Configuration(new()), db.Token));
        await accounts.SetDisabledAsync(account.AccountId, true, db.Token);
        Assert.False(await accounts.ValidatePrincipalAsync(staff, config, db.Token));
    }

    [MongoReplicaSetFact]
    public async Task RegistrationAndRecoveryResponsesDoNotDiscloseAccountExistence()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var (accounts, security, _) = Services(Client(), db.Name, new Clock());
        var existing = accounts.Create("existing@example.test", Password, "Existing Member");
        await accounts.SaveAsync(existing, db.Token);
        var mailer = Mailer(security); // Explicitly disabled: no real SMTP connections in this test.
        foreach (var email in new[] { existing.Email, "absent@example.test" })
        {
            var registration = Prepare(new RegisterModel(accounts, security, mailer) { Email = email, Password = Password, FullName = "Member" });
            Assert.IsType<RedirectToPageResult>(await registration.OnPostAsync(db.Token));
            Assert.True(registration.ModelState.IsValid);
            var recovery = Prepare(new RecoverModel(accounts, security, mailer) { Email = email });
            Assert.IsType<RedirectToPageResult>(await recovery.OnPostAsync(db.Token));
            Assert.True(recovery.ModelState.IsValid);
        }
    }

    private static StoreAccount Account() => new()
    {
        Email = "member@example.test", PasswordHash = "unused", AccountId = AccountId,
        FullName = "Test Member", MembershipNumber = "NS-TEST", SecurityStamp = "test-security-stamp"
    };

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    private static IConfiguration StaffConfiguration() => Configuration(new() { ["AUTH_ADMIN_ACCOUNT_IDS"] = AccountId });
    private static MongoClient Client() => new(Environment.GetEnvironmentVariable(MongoTestDatabase.UriVariable)!);

    private static (StoreAccounts Accounts, AccountSecurityStore Security, IMongoDatabase Database) Services(
        IMongoClient client, string name, Clock clock)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MongoDbOptions { DatabaseName = name });
        var security = new AccountSecurityStore(client, options, clock);
        var accounts = new StoreAccounts(client, options, new PasswordHasher<StoreAccount>(), security, clock);
        return (accounts, security, client.GetDatabase(name));
    }

    private static ReceiptEmailService Mailer(AccountSecurityStore security) =>
        new(Configuration(new() { ["EMAIL_ENABLED"] = "false" }), NullLogger<ReceiptEmailService>.Instance, security);

    private static T Prepare<T>(T page) where T : PageModel
    {
        var context = new DefaultHttpContext();
        page.PageContext = new PageContext { HttpContext = context };
        page.TempData = new TempDataDictionary(context, new MemoryTempData());
        return page;
    }

    private sealed class MemoryTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = Now;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan amount) => now += amount;
    }
}
