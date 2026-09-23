using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace SmartStore.Models;

public sealed class Customer
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    [BsonElement("firstName")]
    public required string FirstName { get; set; }

    [BsonElement("lastName")]
    public required string LastName { get; set; }

    [BsonElement("email")]
    public required string Email { get; set; }

    [BsonElement("phone")]
    public required string Phone { get; set; }

    [BsonElement("address")]
    public required CustomerAddress Address { get; set; }

    [BsonElement("createdAtUtc")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAtUtc { get; set; }

    [BsonIgnore]
    public string FullName => $"{FirstName} {LastName}";
}

public sealed class CustomerAddress
{
    [BsonElement("street")]
    public required string Street { get; set; }

    [BsonElement("city")]
    public required string City { get; set; }

    [BsonElement("province")]
    public required string Province { get; set; }

    [BsonElement("postalCode")]
    public required string PostalCode { get; set; }

    [BsonElement("country")]
    public string Country { get; set; } = "Canada";
}
