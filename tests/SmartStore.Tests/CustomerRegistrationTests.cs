using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging.Abstractions;
using SmartStore.Data;
using SmartStore.Models;
using SmartStore.Options;
using SmartStore.Pages;
using SmartStore.Services;

namespace SmartStore.Tests;

public sealed class CustomerRegistrationTests
{
    [Fact]
    public void ToCustomer_NormalizesStoredValues()
    {
        var input = ValidInput();
        input.Email = "  Jordan.LEE@Example.COM ";
        input.PostalCode = "h3g1m8";
        input.Province = "qc";

        var customer = input.ToCustomer();

        Assert.Equal("jordan.lee@example.com", customer.Email);
        Assert.Equal("H3G 1M8", customer.Address.PostalCode);
        Assert.Equal("QC", customer.Address.Province);
        Assert.Equal(DateTimeKind.Utc, customer.CreatedAtUtc.Kind);
    }

    [Fact]
    public void Validation_RejectsAnInvalidCanadianPostalCode()
    {
        var input = ValidInput();
        input.PostalCode = "12345";
        var results = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(
            input,
            new ValidationContext(input),
            results,
            validateAllProperties: true);

        Assert.False(valid);
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(CustomerInput.PostalCode)));
    }

    [Fact]
    public async Task SuccessfulPost_SavesCustomerAndSignalsBlueOutput()
    {
        var repository = new FakeCustomerRepository();
        var indicator = new FakeStatusIndicator();
        var page = CreatePage(repository, indicator);

        var result = await page.OnPostAsync(CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(result);
        Assert.NotNull(repository.AddedCustomer);
        Assert.Equal(1, indicator.SuccessSignals);
        Assert.Equal(0, indicator.FailureSignals);
        Assert.Equal("success", page.StatusKind);
    }

    [Fact]
    public async Task DuplicateEmail_DoesNotSaveAndSignalsFailureOutputs()
    {
        var repository = new FakeCustomerRepository
        {
            AddException = new CustomerAlreadyExistsException("jordan@example.com")
        };
        var indicator = new FakeStatusIndicator();
        var page = CreatePage(repository, indicator);

        var result = await page.OnPostAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.False(page.ModelState.IsValid);
        Assert.Equal(0, indicator.SuccessSignals);
        Assert.Equal(1, indicator.FailureSignals);
        Assert.Equal("error", page.StatusKind);
    }

    [Fact]
    public async Task InvalidForm_SkipsDatabaseAndSignalsFailureOutputs()
    {
        var repository = new FakeCustomerRepository();
        var indicator = new FakeStatusIndicator();
        var page = CreatePage(repository, indicator);
        page.ModelState.AddModelError("Input.Email", "Invalid email");

        var result = await page.OnPostAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Null(repository.AddedCustomer);
        Assert.Equal(1, indicator.FailureSignals);
    }

    private static IndexModel CreatePage(
        ICustomerRepository repository,
        IStatusIndicator indicator) =>
        new(
            repository,
            indicator,
            Microsoft.Extensions.Options.Options.Create(new GpioOptions()),
            NullLogger<IndexModel>.Instance)
        {
            Input = ValidInput()
        };

    private static CustomerInput ValidInput() => new()
    {
        FirstName = "Jordan",
        LastName = "Lee",
        Email = "jordan@example.com",
        Phone = "514-555-0182",
        Street = "821 Sainte-Catherine Street W",
        City = "Montreal",
        Province = "QC",
        PostalCode = "H3G 1M8"
    };

    private sealed class FakeCustomerRepository : ICustomerRepository
    {
        public Customer? AddedCustomer { get; private set; }

        public Exception? AddException { get; init; }

        public Task AddAsync(Customer customer, CancellationToken cancellationToken = default)
        {
            if (AddException is not null)
            {
                return Task.FromException(AddException);
            }

            AddedCustomer = customer;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Customer>> GetRecentAsync(
            int count,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Customer>>([]);
    }

    private sealed class FakeStatusIndicator : IStatusIndicator
    {
        public int SuccessSignals { get; private set; }

        public int FailureSignals { get; private set; }

        public Task ShowSuccessAsync(CancellationToken cancellationToken = default)
        {
            SuccessSignals++;
            return Task.CompletedTask;
        }

        public Task ShowFailureAsync(CancellationToken cancellationToken = default)
        {
            FailureSignals++;
            return Task.CompletedTask;
        }
    }
}
