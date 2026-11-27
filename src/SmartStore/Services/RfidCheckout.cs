using System.Security.Cryptography;
using System.Text;
using SmartStore.Data;
using SmartStore.Models;
using Microsoft.Extensions.Options;

namespace SmartStore.Services;

public sealed class RfidOptions
{
    public string Mode { get; set; } = "Simulation";
    public string ApiKey { get; set; } = "";
}

public sealed record RfidTag(string Epc, string Code, string? ScanId = null);
public sealed record RfidBatch(string? Session, string[]? Epcs, Guid? ScanId = null);

// One process, one checkout session per page. No native reader SDK dependencies.
public sealed class RfidCheckout(IOptions<RfidOptions> options, TimeProvider clock)
{
    private sealed record Session(DateTimeOffset Expires, bool IsReceiving, Dictionary<string, RfidTag> Tags);
    private readonly Dictionary<string, Session> sessions = new();
    private readonly object gate = new();
    public bool IsSimulation => options.Value.Mode == "Simulation";
    public bool IsReader => options.Value.Mode == "Reader";

    public bool Authenticate(string key)
    {
        var expected = options.Value.ApiKey;
        return IsReader && expected.Length >= 32 &&
            CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(key)),
                SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
    }

    public string Open(bool receiving = false)
    {
        lock (gate)
        {
            foreach (var key in sessions.Where(x => x.Value.Expires <= clock.GetUtcNow()).Select(x => x.Key).ToArray())
                sessions.Remove(key);
            if (sessions.Count >= 100) throw new InvalidOperationException("RFID session capacity reached. Try again later.");
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            sessions[token] = new(clock.GetUtcNow().AddMinutes(30), receiving, new(StringComparer.Ordinal));
            return token;
        }
    }

    public string PairLocal(HttpContext context, bool receiving = false)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!IsReader) return "";
        var token = Open(receiving);
        var pairingFile = Environment.GetEnvironmentVariable("RFID_LOCAL_PAIRING_FILE");
        var peer = context.Connection.RemoteIpAddress;
        if (!string.IsNullOrEmpty(pairingFile) && peer is not null && System.Net.IPAddress.IsLoopback(peer))
        {
            var temporaryFile = pairingFile + "." + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporaryFile, token);
            File.Move(temporaryFile, pairingFile, overwrite: true);
        }
        return token;
    }

    public RfidTag[]? Read(string token, bool receiving = false)
    {
        lock (gate)
            return sessions.TryGetValue(token, out var session) && session.IsReceiving == receiving &&
                session.Expires > clock.GetUtcNow()
                ? session.Tags.Values.ToArray() : null;
    }

    public async Task<bool> AcceptAsync(RfidBatch batch, Func<string, Task<string?>> resolve)
    {
        if (!IsReader || batch.Session is null || batch.Epcs is not { Length: >= 1 and <= 100 })
            throw new ArgumentException("Supply a session and between 1 and 100 EPCs.");
        // Validate the whole batch before changing session state.
        var epcs = batch.Epcs.Select(Normalize).Distinct().ToArray();
        if (batch.ScanId is not null && epcs.Length != 1)
            throw new ArgumentException("Supply exactly one EPC per scan ID.");
        Session? active;
        lock (gate)
        {
            if (!sessions.TryGetValue(batch.Session, out active) || active.Expires <= clock.GetUtcNow())
                return false;
        }
        var mapped = new List<RfidTag>();
        foreach (var epc in epcs)
        {
            var code = active.IsReceiving ? "" :
                await resolve(epc) ?? throw new ArgumentException("An EPC has no stored product mapping.");
            mapped.Add(new(epc, code, active.IsReceiving ? null : batch.ScanId?.ToString("N")));
        }
        lock (gate)
        {
            if (!sessions.TryGetValue(batch.Session, out var session) || session.Expires <= clock.GetUtcNow()) return false;
            var limit = session.IsReceiving ? ReceptionInput.MaxTags : 100;
            var keys = mapped.Select(tag => tag.ScanId is { } id ? "scan:" + id : tag.Epc);
            if (session.Tags.Keys.Union(keys).Count() > limit)
                throw new ArgumentException("Session tag limit reached.");
            foreach (var tag in mapped) session.Tags.TryAdd(tag.ScanId is { } id ? "scan:" + id : tag.Epc, tag);
            return true;
        }
    }

    private static string Normalize(string? epc)
    {
        var value = epc?.Trim().ToUpperInvariant() ?? "";
        if (value.Length is < 4 or > 128 || value.Length % 2 != 0 || !value.All(Uri.IsHexDigit))
            throw new ArgumentException("EPCs must contain 4–128 hexadecimal characters in complete bytes.");
        return value;
    }
}

public static class RfidEndpoints
{
    public static void MapRfid(this WebApplication app)
    {
        app.MapPost("/api/rfid/events", async (HttpContext context, RfidCheckout rfid) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!rfid.Authenticate(context.Request.Headers["X-Rfid-Key"].ToString())) return Results.Unauthorized();
            // Bound the body even for chunked requests.
            using var buffer = new MemoryStream();
            var bytes = new byte[4096];
            while (true)
            {
                var count = await context.Request.Body.ReadAsync(bytes, context.RequestAborted);
                if (count == 0) break;
                if (buffer.Length + count > 16384) return Results.StatusCode(413);
                buffer.Write(bytes, 0, count);
            }
            try
            {
                var batch = System.Text.Json.JsonSerializer.Deserialize<RfidBatch>(buffer.ToArray(),
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
                if (batch is null) return Results.BadRequest();
                return await rfid.AcceptAsync(batch, async epc =>
                {
                    var repo = context.RequestServices.GetRequiredService<IProductRepository>();
                    return (await repo.ResolveCodeAsync(epc, context.RequestAborted))?.Product.Upc;
                }) ? Results.Ok(new { accepted = true }) : Results.NotFound();
            }
            catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException)
            {
                return Results.BadRequest(new { error = ex is ArgumentException ? ex.Message : "Invalid RFID event JSON." });
            }
        });
    }
}
