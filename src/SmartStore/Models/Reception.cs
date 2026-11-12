using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace SmartStore.Models;

// One delivery of a product. Product 1 ── * Reception.
public sealed class Reception
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    [BsonElement("productId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public required string ProductId { get; set; }

    [BsonElement("quantity")]
    public int Quantity { get; set; }

    // Calendar date only, stored as midnight UTC.
    [BsonElement("receivedOn")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime ReceivedOn { get; set; }

    [BsonElement("expiresOn")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    [BsonIgnoreIfNull]
    public DateTime? ExpiresOn { get; set; }

    [BsonElement("note")]
    [BsonIgnoreIfNull]
    public string? Note { get; set; }

    [BsonElement("createdAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAtUtc { get; set; }
}
