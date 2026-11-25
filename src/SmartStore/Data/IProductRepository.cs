using MongoDB.Driver;
using SmartStore.Models;

namespace SmartStore.Data;

// A product is a type (one UPC). Each RFID-tagged unit is an Item keyed by its EPC.
// Product lookups can join checkout's session. Stock changes must join its active transaction.
public interface IProductRepository
{
    /// <summary>
    /// Resolves a scanned code: a registered item EPC (IsTag, whatever its status),
    /// a saved barcode alias, or a UPC.
    /// </summary>
    Task<(Product Product, bool IsTag)?> ResolveCodeAsync(string code, CancellationToken token, IClientSessionHandle? session = null);

    Task<Item?> FindItemAsync(string epc, CancellationToken token, IClientSessionHandle? session = null);

    /// <summary>Items of one product (or all products when null), newest first.</summary>
    Task<List<Item>> ListItemsAsync(string? productId, string? status, int count, CancellationToken token);

    /// <summary>Number of in-stock tagged items per product id.</summary>
    Task<Dictionary<string, int>> CountTaggedInStockAsync(CancellationToken token);

    /// <summary>Number of in-stock tagged items of one product.</summary>
    Task<int> CountTaggedInStockAsync(string productId, CancellationToken token, IClientSessionHandle? session = null);

    Task<List<ProductCode>> ListCodesAsync(CancellationToken token);

    /// <summary>Saves an extra barcode for a product. RFID tags are registered by receiving, not here.</summary>
    Task AssignCodeAsync(string code, string upc, CancellationToken token);

    /// <summary>Creates indexes and moves legacy product-level EPCs into items. Safe to repeat.</summary>
    Task BackfillCodesAsync(CancellationToken token);

    Task<IReadOnlyList<Product>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<Product?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    Task<Product?> FindByUpcAsync(string upc, CancellationToken cancellationToken = default,
        IClientSessionHandle? session = null);

    /// <exception cref="DuplicateProductCodeException">UPC already used.</exception>
    Task AddAsync(Product product, CancellationToken cancellationToken = default);

    /// <exception cref="DuplicateProductCodeException">UPC already used by another product.</exception>
    /// <exception cref="ProductNotFoundException">The product no longer exists.</exception>
    Task UpdateAsync(Product product, CancellationToken cancellationToken = default);

    /// <summary>Deletes the product, its items and its reception history. Returns false if it did not exist.</summary>
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a delivery in one transaction: registers each new EPC in <see cref="Reception.Epcs"/> as an
    /// in-stock item, adds those plus <see cref="Reception.Quantity"/> untagged units to stock, and saves
    /// the reception. Tags already in stock for this product are skipped. On return the reception holds
    /// only the newly registered EPCs and the total quantity added.
    /// </summary>
    /// <exception cref="ProductNotFoundException">The product no longer exists.</exception>
    /// <exception cref="InvalidOperationException">A tag belongs to another product, was sold, or nothing is new.</exception>
    Task<Product> ReceiveStockAsync(Reception reception, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Reception>> GetReceptionsAsync(
        string productId,
        int count = 50,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Reception>> GetRecentReceptionsAsync(
        int count = 20,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a reception, its tagged items and its quantity from stock in one transaction.
    /// Concurrent undo requests cannot apply the same adjustment twice.
    /// </summary>
    /// <exception cref="InvalidOperationException">Some of those units were already sold.</exception>
    Task<bool> DeleteReceptionAsync(string receptionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes sold units within the caller's active transaction. Returns false if the product
    /// does not exist or has insufficient stock. The caller must abort on failure.
    /// This does not mark any RFID item as sold; checkout should move to <see cref="SellAsync"/>.
    /// </summary>
    Task<bool> DecrementStockAsync(IClientSessionHandle session, string productId, int quantity,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sells <paramref name="quantity"/> units of a product within the caller's active transaction:
    /// the tagged items in <paramref name="epcs"/> plus untagged units for the rest. The caller must abort
    /// on failure.
    /// </summary>
    /// <exception cref="InvalidOperationException">A tag was already sold or stock changed.</exception>
    Task SellAsync(IClientSessionHandle session, string productId, int quantity, IReadOnlyCollection<string> epcs,
        string saleId, CancellationToken cancellationToken = default);
}

public sealed class DuplicateProductCodeException(string field, string value)
    : Exception($"Another product already uses the {field} '{value}'.")
{
    public string Field { get; } = field;
}

public sealed class ProductNotFoundException()
    : Exception("That product no longer exists. It may have been deleted.");
