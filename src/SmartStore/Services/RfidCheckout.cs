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
    public string TillId { get; set; } = "local";
}

public sealed record RfidTag(string Epc, string Code, string? ScanId = null, string Source = "reader");
public sealed record RfidBatch(string? Session, string[]? Epcs, Guid? ScanId = null, string Source = "reader");
public sealed record RfidInventory(Item? Item, string? Code, bool CanCheckout);

// Opening a page allocates an owned workflow, never an automatic reader takeover.
public sealed class RfidCheckout(IOptions<RfidOptions> options, TimeProvider clock)
{
    private const string OwnerCookie = "__Host-Northstar.RfidOwner";
    private sealed record Session(DateTimeOffset Expires, string Owner, string Till, bool IsReceiving,
        Dictionary<string, RfidTag> Tags)
    {
        public bool Paired { get; set; }
        public bool UsesLocalPairingFile { get; set; }
    }
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

    public string Open(HttpContext context, bool receiving = false, string? existingToken = null)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!IsReader) return "";
        var owner = Owner(context, create: true)!;
        lock (gate)
        {
            Prune();
            if (existingToken is not null && Owned(existingToken, owner, receiving, out _))
                return existingToken;
            // Reclaim abandoned allocations, but never replace an actively paired tab.
            foreach (var key in sessions.Where(x => x.Value.Owner == owner &&
                         x.Value.IsReceiving == receiving && !x.Value.Paired).Select(x => x.Key).ToArray())
                sessions.Remove(key);
            if (sessions.Values.Count(x => x.Owner == owner) >= 4 || sessions.Count >= 100)
                throw new InvalidOperationException("RFID session capacity reached. Close an unused reader session.");
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            sessions[token] = new(clock.GetUtcNow().AddMinutes(30), owner, options.Value.TillId,
                receiving, new(StringComparer.Ordinal));
            return token;
        }
    }

    public bool PairReader(HttpContext context, string token, bool receiving = false)
    {
        context.Response.Headers.CacheControl = "no-store";
        var peer = context.Connection.RemoteIpAddress;
        if (!IsReader || !context.Request.IsHttps) return false;
        lock (gate)
        {
            Prune();
            if (!Owned(token, Owner(context), receiving, out var session)) return false;
            if (sessions.Any(x => x.Key != token && x.Value.Paired && x.Value.Till == session!.Till))
                return false;
            // Remote operators explicitly authorize an external key+session bridge; they must not
            // change a local bridge's pairing file, including when reached through a trusted proxy.
            if (peer is not null && System.Net.IPAddress.IsLoopback(peer))
            {
                WritePairing(token);
                session!.UsesLocalPairingFile = true;
            }
            session!.Paired = true;
            return true;
        }
    }

    public RfidTag[]? Read(HttpContext context, string token, bool receiving = false)
    {
        context.Response.Headers.CacheControl = "no-store";
        lock (gate)
        {
            Prune();
            return Owned(token, Owner(context), receiving, out var session) ? session!.Tags.Values.ToArray() : null;
        }
    }

    public bool Close(HttpContext context, string token, bool receiving = false)
    {
        context.Response.Headers.CacheControl = "no-store";
        lock (gate)
        {
            Prune();
            if (!Owned(token, Owner(context), receiving, out var session)) return false;
            if (session!.UsesLocalPairingFile) WritePairing("");
            return sessions.Remove(token);
        }
    }

    private bool Owned(string token, string? owner, bool receiving, out Session? session)
    {
        return sessions.TryGetValue(token, out session) && owner is not null &&
            session.Owner == owner && session.IsReceiving == receiving && session.Till == options.Value.TillId;
    }

    private static string? Owner(HttpContext context, bool create = false)
    {
        if (context.Items.TryGetValue(OwnerCookie, out var cached)) return (string)cached!;
        var cookie = context.Request.Cookies[OwnerCookie];
        if (cookie is null || cookie.Length != 64 || !cookie.All(Uri.IsHexDigit))
        {
            if (!create) return null;
            if (!context.Request.IsHttps) throw new InvalidOperationException("Use HTTPS for reader sessions.");
            cookie = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            context.Response.Cookies.Append(OwnerCookie, cookie, new CookieOptions
            {
                HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/",
                IsEssential = true
            });
        }
        var owner = cookie + ":" + (StoreAccounts.AccountIdOf(context.User) ?? "guest");
        context.Items[OwnerCookie] = owner;
        return owner;
    }

    private void Prune()
    {
        foreach (var key in sessions.Where(x => x.Value.Expires <= clock.GetUtcNow()).Select(x => x.Key).ToArray())
        {
            if (sessions[key].UsesLocalPairingFile) WritePairing("");
            sessions.Remove(key);
        }
    }

    private static void WritePairing(string token)
    {
        var path = Environment.GetEnvironmentVariable("RFID_LOCAL_PAIRING_FILE");
        if (string.IsNullOrEmpty(path)) return; // External bridges use the explicitly paired token.
        var temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, token);
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    public async Task<bool> AcceptAsync(RfidBatch batch, Func<string, Task<RfidInventory>> resolve)
    {
        if (!IsReader || batch.Session is null || batch.Epcs is not { Length: >= 1 and <= 100 })
            throw new ArgumentException("Supply a session and between 1 and 100 EPCs.");
        if (batch.Source is not ("reader" or "manual"))
            throw new ArgumentException("RFID event source must be reader or manual.");
        var epcs = batch.Epcs.Select(Normalize).Distinct().ToArray();
        if (batch.ScanId is not null && epcs.Length != 1)
            throw new ArgumentException("Supply exactly one EPC per scan ID.");
        Session? active;
        lock (gate)
        {
            if (!sessions.TryGetValue(batch.Session, out active) || !active.Paired ||
                active.Expires <= clock.GetUtcNow()) return false;
        }
        var mapped = new List<RfidTag>(epcs.Length);
        foreach (var epc in epcs)
        {
            var inventory = await resolve(epc);
            if (inventory.Item is { } item && (item.Epc != epc || !inventory.CanCheckout ||
                    (item.Status != Item.InStock && (item.Status != Item.Sold || item.SoldBy != Item.SoldByGuess))))
                throw new ArgumentException("An EPC is not eligible inventory.");
            if (!active.IsReceiving && (inventory.Item is null || !inventory.CanCheckout ||
                    string.IsNullOrEmpty(inventory.Code)))
                throw new ArgumentException("An EPC has no eligible registered item.");
            mapped.Add(new(epc, active.IsReceiving ? "" : inventory.Code!, Source: batch.Source));
        }
        lock (gate)
        {
            if (!sessions.TryGetValue(batch.Session, out var session) || !ReferenceEquals(session, active) ||
                !session.Paired || session.Expires <= clock.GetUtcNow()) return false;
            var limit = session.IsReceiving ? ReceptionInput.MaxTags : 100;
            if (session.Tags.Keys.Union(epcs).Count() > limit)
                throw new ArgumentException("Session tag limit reached.");
            foreach (var tag in mapped) session.Tags.TryAdd(tag.Epc, tag);
            return true;
        }
    }

    private static string Normalize(string? epc)
    {
        return Item.NormalizeEpc(epc) ??
            throw new ArgumentException("EPCs must contain 4–24 hexadecimal characters in complete bytes.");
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
                    var item = await repo.FindItemAsync(epc, context.RequestAborted);
                    if (item is null) return new RfidInventory(null, null, false);
                    var eligible = await repo.CanCheckoutItemAsync(item, context.RequestAborted);
                    var product = eligible ? await repo.GetByIdAsync(item.ProductId, context.RequestAborted) : null;
                    return new RfidInventory(item, product?.Upc, eligible && product is { ArchivedAtUtc: null });
                }) ? Results.Ok(new { accepted = true }) : Results.NotFound();
            }
            catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException)
            {
                return Results.BadRequest(new { error = ex is ArgumentException ? ex.Message : "Invalid RFID event JSON." });
            }
        });
    }
}
