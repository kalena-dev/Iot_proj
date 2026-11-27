using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace SmartStore.Models;

// One physical, RFID-tagged unit of a product. Product 1 ── * Item.
// The EPC on the tag identifies this exact unit; the product's UPC identifies its type.
public sealed class Item
{
    public const string InStock = "in_stock";
    public const string Sold = "sold";

    // The tag's EPC, uppercase hex.
    [BsonId]
    public required string Epc { get; set; }

    [BsonElement("productId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public required string ProductId { get; set; }

    [BsonElement("status")]
    public string Status { get; set; } = InStock;

    // Null for tags carried over from the old one-EPC-per-product catalog.
    [BsonElement("receptionId")]
    [BsonRepresentation(BsonType.ObjectId)]
    [BsonIgnoreIfNull]
    public string? ReceptionId { get; set; }

    [BsonElement("receivedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime ReceivedAtUtc { get; set; }

    [BsonElement("saleId")]
    [BsonIgnoreIfNull]
    public string? SaleId { get; set; }

    [BsonElement("soldAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    [BsonIgnoreIfNull]
    public DateTime? SoldAtUtc { get; set; }

    // An explicit EPC identifier (typed or reader-transferred), or a barcode sale's oldest-unit guess.
    // Neither source authenticates a physical tag. Null on older sales is treated as an explicit identifier.
    public const string SoldByScan = "scan";
    public const string SoldByGuess = "guess";

    [BsonElement("soldBy")]
    [BsonIgnoreIfNull]
    public string? SoldBy { get; set; }

    /// <summary>Returns the EPC in stored form, or null if it is not 4–24 hex digits in whole bytes.</summary>
    public static string? NormalizeEpc(string? epc)
    {
        var value = epc?.Trim().ToUpperInvariant() ?? "";
        return value.Length is >= 4 and <= 24 && value.Length % 2 == 0 && value.All(Uri.IsHexDigit) ? value : null;
    }
}
