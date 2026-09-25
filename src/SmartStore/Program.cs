using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using SmartStore.Data;
using SmartStore.Infrastructure;
using SmartStore.Options;
using SmartStore.Services;

DotEnvLoader.Load();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();

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

builder.Services.AddSingleton<ICustomerRepository, MongoCustomerRepository>();
builder.Services.AddSingleton<IStatusIndicator, RaspberryPiStatusIndicator>();
builder.Services.AddSingleton<StoreDemo>();

var app = builder.Build();

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
app.UseAuthorization();
app.MapRazorPages();

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
