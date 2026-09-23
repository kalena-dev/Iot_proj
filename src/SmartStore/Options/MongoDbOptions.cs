namespace SmartStore.Options;

public sealed class MongoDbOptions
{
    public const string SectionName = "MongoDb";

    public string ConnectionString { get; set; } = string.Empty;

    public string DatabaseName { get; set; } = "smart_store";

    public string CustomersCollectionName { get; set; } = "customers";

    public int ServerSelectionTimeoutSeconds { get; set; } = 8;
}
