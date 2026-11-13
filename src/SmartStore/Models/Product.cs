using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace SmartStore.Models;

// A product is defined once. Its stock only changes through receptions (stock in)
// and checkout sales (stock out), never by typing a new number.
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

    // RFID tag code, uppercase hex, at most 24 characters. Unique.
    [BsonElement("epc")]
    public required string Epc { get; set; }

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
