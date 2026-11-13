using MongoDB.Bson.Serialization.Attributes;

namespace SmartStore.Models;

public sealed class EmailLog
{
    [BsonId] public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? Reference { get; set; }
    public string Recipient { get; set; } = "";
    public string? Sender { get; set; }
    public string Subject { get; set; } = "";
    public string Status { get; set; } = "pending";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}
