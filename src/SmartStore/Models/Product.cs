using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace SmartStore.Models;

// A product type, identified by its UPC. Each physical unit carries its own EPC (see Item).
// Its stock only changes through receptions (stock in) and checkout sales (stock out),
// never by typing a new number. QuantityInStock counts tagged and untagged units; it is
// always at least the number of this product's in-stock items.
// Older documents may still hold a legacy "epc" field until they are migrated.
[BsonIgnoreExtraElements]
public sealed class Product
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    [BsonElement("name")]
    public required string Name { get; set; }

    [BsonElement("category")]
    public required string Category { get; set; }

    [BsonElement("price")]
    [BsonRepresentation(BsonType.Decimal128)]
    public decimal Price { get; set; }

    // Barcode, digits only, at most 13 characters. Unique.
    [BsonElement("upc")]
    public required string Upc { get; set; }

    [BsonElement("producer")]
    public required string Producer { get; set; }

    [BsonElement("description")]
    [BsonIgnoreIfNull]
    public string? Description { get; set; }

    [BsonElement("imageUrl")]
    [BsonIgnoreIfNull]
    public string? ImageUrl { get; set; }

    [BsonElement("quantityInStock")]
    public int QuantityInStock { get; set; }

    [BsonElement("createdAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAtUtc { get; set; }

    [BsonElement("updatedAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime UpdatedAtUtc { get; set; }
}
