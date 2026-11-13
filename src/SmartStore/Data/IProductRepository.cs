using MongoDB.Driver;
using SmartStore.Models;

namespace SmartStore.Data;

// Product lookups can join checkout's session. Stock decrements must join its active transaction.
public interface IProductRepository
{
    Task<(Product Product, bool IsTag)?> ResolveCodeAsync(string code, CancellationToken token, IClientSessionHandle? session = null);
    Task<List<ProductCode>> ListCodesAsync(CancellationToken token);
    Task AssignCodeAsync(string code, string upc, CancellationToken token);
    Task BackfillCodesAsync(CancellationToken token);

    Task<IReadOnlyList<Product>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<Product?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    Task<Product?> FindByUpcAsync(string upc, CancellationToken cancellationToken = default,
        IClientSessionHandle? session = null);

    Task<Product?> FindByEpcAsync(string epc, CancellationToken cancellationToken = default,
        IClientSessionHandle? session = null);

    /// <exception cref="DuplicateProductCodeException">UPC or EPC already used.</exception>
    Task AddAsync(Product product, CancellationToken cancellationToken = default);

    /// <exception cref="DuplicateProductCodeException">UPC or EPC already used by another product.</exception>
    /// <exception cref="ProductNotFoundException">The product no longer exists.</exception>
    Task UpdateAsync(Product product, CancellationToken cancellationToken = default);

    /// <summary>Deletes the product and its reception history. Returns false if it did not exist.</summary>
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a delivery and adds its quantity to stock in one transaction.
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
    /// Removes a reception and its quantity from stock in one transaction.
    /// Concurrent undo requests cannot apply the same adjustment twice.
    /// </summary>
    /// <exception cref="InvalidOperationException">Some of those units were already sold.</exception>
    Task<bool> DeleteReceptionAsync(string receptionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes sold units within the caller's active transaction. Returns false if the product
    /// does not exist or has insufficient stock. The caller must abort on failure.
    /// </summary>
    Task<bool> DecrementStockAsync(IClientSessionHandle session, string productId, int quantity,
        CancellationToken cancellationToken = default);
}

public sealed class DuplicateProductCodeException(string field, string value)
    : Exception($"Another product already uses the {field} '{value}'.")
{
    public string Field { get; } = field;
}

public sealed class ProductNotFoundException()
    : Exception("That product no longer exists. It may have been deleted.");
