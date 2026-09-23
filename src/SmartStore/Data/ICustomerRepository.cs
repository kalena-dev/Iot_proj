using SmartStore.Models;

namespace SmartStore.Data;

public interface ICustomerRepository
{
    Task AddAsync(Customer customer, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Customer>> GetRecentAsync(
        int count,
        CancellationToken cancellationToken = default);
}

public sealed class CustomerAlreadyExistsException(string email)
    : Exception($"A customer with the email address '{email}' already exists.");
