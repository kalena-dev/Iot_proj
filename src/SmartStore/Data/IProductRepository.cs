using SmartStore.Models;

namespace SmartStore.Data;

// Products and stock receptions. Checkout should use FindByUpcAsync / FindByEpcAsync
// to look up scanned items and DecrementStockAsync to take sold items out of stock.
public interface IProductRepository
{
    Task<IReadOnlyList<Product>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<Product?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    Task<Product?> FindByUpcAsync(string upc, CancellationToken cancellationToken = default);

    Task<Product?> FindByEpcAsync(string epc, CancellationToken cancellationToken = default);

    /// <exception cref="DuplicateProductCodeException">UPC or EPC already used.</exception>
    Task AddAsync(Product product, CancellationToken cancellationToken = default);

    /// <exception cref="DuplicateProductCodeException">UPC or EPC already used by another product.</exception>
    /// <exception cref="ProductNotFoundException">The product no longer exists.</exception>
    Task UpdateAsync(Product product, CancellationToken cancellationToken = default);

    /// <summary>Deletes the product and its reception history. Returns false if it did not exist.</summary>
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a delivery and adds its quantity to stock. A failed stock update removes the history entry.
    /// These are compensated writes, not a cross-document transaction.
    /// </summary>
    /// <exception cref="ProductNotFoundException">The product no longer exists.</exception>
    Task<Product> ReceiveStockAsync(Reception reception, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Reception>> GetReceptionsAsync(
        string productId,
        int count = 50,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Reception>> GetRecentReceptionsAsync(
        int count = 20,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically claims a reception before removing its quantity from stock, so concurrent undo cannot apply twice.
    /// Restores its history if the stock adjustment fails; this is not a cross-document transaction.
    /// </summary>
    /// <exception cref="InvalidOperationException">Some of those units were already sold.</exception>
    Task<bool> DeleteReceptionAsync(string receptionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes sold units from stock in one atomic step. Returns false (and changes nothing)
    /// if the product does not exist or has fewer than <paramref name="quantity"/> in stock.
    /// </summary>
    Task<bool> DecrementStockAsync(string productId, int quantity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a whole cart from stock: all items or none. Keys are product ids, values are quantities.
    /// If any item is short or missing, the items already removed are put back and the result says
    /// which product failed. Call this before saving the sale; if saving then fails, call RestoreStockAsync.
    /// </summary>
    Task<StockDecrementResult> DecrementStockManyAsync(
        IReadOnlyDictionary<string, int> quantitiesByProductId,
        CancellationToken cancellationToken = default);

    /// <summary>Puts units back in stock (for example, a sale could not be saved after DecrementStockManyAsync).</summary>
    Task RestoreStockAsync(
        IReadOnlyDictionary<string, int> quantitiesByProductId,
        CancellationToken cancellationToken = default);
}

public sealed record StockDecrementResult(bool Succeeded, string? FailedProductId = null, string? Message = null)
{
    public static StockDecrementResult Success { get; } = new(true);
}

public sealed class DuplicateProductCodeException(string field, string value)
    : Exception($"Another product already uses the {field} '{value}'.")
{
    public string Field { get; } = field;
}

public sealed class ProductNotFoundException()
    : Exception("That product no longer exists. It may have been deleted.");
