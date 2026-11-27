using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SmartStore.Data;
using SmartStore.Models;
using SmartStore.Options;
using SmartStore.Pages;
using SmartStore.Services;

namespace SmartStore.Tests;

public sealed class RegistrationPrivacyTests
{
    [Fact]
    public async Task SaveFailure_DoesNotWriteCustomerAddressOrExceptionPayloadToDiagnostics()
    {
        var logger = new CaptureLogger();
        var page = new IndexModel(new FailingRepository(), new Indicator(),
            Microsoft.Extensions.Options.Options.Create(new GpioOptions()), logger);
        page.PageContext.HttpContext = new DefaultHttpContext();
        page.Input = new CustomerInput
        {
            FirstName = "Synthetic", LastName = "Customer", Email = "private-customer@example.test",
            Phone = "5145550101", Street = "1 Test Street", City = "Montreal", Province = "QC", PostalCode = "H2X1Y4"
        };
        await page.OnPostAsync(CancellationToken.None);
        Assert.Contains(logger.Messages, text => text.Contains("Customer registration failed"));
        Assert.DoesNotContain(logger.Messages, text => text.Contains(page.Input.Email));
        Assert.DoesNotContain(logger.Messages, text => text.Contains("sensitive database payload"));
    }

    private sealed class FailingRepository : ICustomerRepository
    {
        public Task AddAsync(Customer customer, CancellationToken token = default) =>
            Task.FromException(new InvalidOperationException($"sensitive database payload {customer.Email}"));
        public Task<IReadOnlyList<Customer>> GetRecentAsync(int count, CancellationToken token = default) =>
            Task.FromResult<IReadOnlyList<Customer>>([]);
    }
    private sealed class Indicator : IStatusIndicator
    {
        public Task ShowSuccessAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task ShowFailureAsync(CancellationToken token = default) => Task.CompletedTask;
    }
    private sealed class CaptureLogger : ILogger<IndexModel>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception) + exception);
    }
}
