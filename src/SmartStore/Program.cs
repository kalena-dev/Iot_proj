using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using SmartStore.Data;
using SmartStore.Infrastructure;
using SmartStore.Options;
using SmartStore.Services;

DotEnvLoader.Load();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    foreach (var page in new[] { "/Index", "/Management", "/Reports", "/InventoryReport", "/Records", "/Refrigerators", "/Sensor" })
        options.Conventions.AuthorizePage(page, "Staff");
    options.Conventions.AuthorizeFolder("/Products", "Staff");
    foreach (var page in new[] { "/Login", "/Register", "/Error" })
        options.Conventions.AllowAnonymousToPage(page);
});
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.LoginPath = "/Login";
    options.AccessDeniedPath = "/Profile";
    options.Cookie.Name = "Northstar.Account";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
});
builder.Services.AddAuthorization(options => options.AddPolicy("Staff",
    policy => policy.RequireAuthenticatedUser().RequireAssertion(context =>
        StoreAccounts.IsStaff(context.User, builder.Configuration))));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("Account", context => HttpMethods.IsPost(context.Request.Method)
        ? RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 8, Window = TimeSpan.FromMinutes(1) })
        : RateLimitPartition.GetNoLimiter("read"));
});
builder.Services.AddSingleton<IPasswordHasher<StoreAccount>, PasswordHasher<StoreAccount>>();
builder.Services.AddSingleton<StoreAccounts>();
builder.Services.AddHttpClient("Msp01", client => client.Timeout = TimeSpan.FromSeconds(4));
builder.Services.AddOptions<RfidOptions>()
    .Bind(builder.Configuration.GetSection("Rfid"))
    .Validate(o => o.Mode is "Simulation" or "Reader", "Rfid:Mode must be Simulation or Reader.")
    .Validate(o => o.Mode != "Reader" || o.ApiKey.Length >= 32,
        "Reader mode requires Rfid:ApiKey with at least 32 characters.")
    .ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<RfidCheckout>();
builder.Services.AddHttpClient<IotServiceClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Iot:BaseUrl"] ?? "http://127.0.0.1:3100/");
    client.Timeout = TimeSpan.FromSeconds(10);
    var apiKey = builder.Configuration["IOT_API_KEY"];
    if (!string.IsNullOrWhiteSpace(apiKey)) client.DefaultRequestHeaders.Add("X-Iot-Key", apiKey);
});

builder.Services
    .AddOptions<MongoDbOptions>()
    .Bind(builder.Configuration.GetSection(MongoDbOptions.SectionName))
    .PostConfigure(options =>
    {
        var environmentConnectionString = builder.Configuration["MONGODB_URI"];
        if (!string.IsNullOrWhiteSpace(environmentConnectionString))
        {
            options.ConnectionString = environmentConnectionString;
        }
    });

builder.Services
    .AddOptions<GpioOptions>()
    .Bind(builder.Configuration.GetSection(GpioOptions.SectionName));

builder.Services.AddSingleton<IMongoClient>(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<MongoDbOptions>>().Value;
    if (string.IsNullOrWhiteSpace(options.ConnectionString))
    {
        throw new InvalidOperationException(
            "MongoDB is not configured. Set MONGODB_URI in the environment or in an untracked .env file.");
    }

    var settings = MongoClientSettings.FromConnectionString(options.ConnectionString);
    settings.ServerSelectionTimeout = TimeSpan.FromSeconds(options.ServerSelectionTimeoutSeconds);
    settings.ConnectTimeout = TimeSpan.FromSeconds(options.ServerSelectionTimeoutSeconds);
    return new MongoClient(settings);
});

builder.Services.AddSingleton<MongoCustomerRepository>();
builder.Services.AddSingleton<ICustomerRepository>(sp => sp.GetRequiredService<MongoCustomerRepository>());
builder.Services.AddSingleton<IProductRepository, MongoProductRepository>();
builder.Services.AddSingleton<IStatusIndicator, RaspberryPiStatusIndicator>();
builder.Services.AddSingleton<StoreDemo>();
builder.Services.AddSingleton<ReceiptEmailService>();

var app = builder.Build();
if (args.Contains("--backfill-checkout"))
{
    await app.Services.GetRequiredService<StoreDemo>().BackfillCheckoutRecordsAsync(CancellationToken.None);
    return;
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

if (builder.Configuration.GetValue<bool>("Security:UseHttpsRedirection"))
{
    app.UseHttpsRedirection();
}

app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true)
        context.Response.Headers.CacheControl = "no-store";
    await next(context);
});
app.UseAuthorization();
app.MapRazorPages();
app.MapRfid();

app.MapGet("/health", async (
    IMongoClient mongoClient,
    IOptions<MongoDbOptions> options,
    CancellationToken cancellationToken) =>
{
    try
    {
        var database = mongoClient.GetDatabase(options.Value.DatabaseName);
        await database.RunCommandAsync<BsonDocument>(
            new BsonDocument("ping", 1),
            cancellationToken: cancellationToken);

        return Results.Ok(new
        {
            status = "healthy",
            database = "connected",
            timestampUtc = DateTime.UtcNow
        });
    }
    catch
    {
        return Results.Json(
            new
            {
                status = "unhealthy",
                database = "unavailable",
                timestampUtc = DateTime.UtcNow
            },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.Run();
