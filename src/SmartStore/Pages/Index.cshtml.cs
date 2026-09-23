using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using SmartStore.Data;
using SmartStore.Models;
using SmartStore.Options;
using SmartStore.Services;

namespace SmartStore.Pages;

public sealed class IndexModel : PageModel
{
    private readonly ICustomerRepository _customers;
    private readonly IStatusIndicator _statusIndicator;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        ICustomerRepository customers,
        IStatusIndicator statusIndicator,
        IOptions<GpioOptions> gpioOptions,
        ILogger<IndexModel> logger)
    {
        _customers = customers;
        _statusIndicator = statusIndicator;
        Gpio = gpioOptions.Value;
        _logger = logger;
    }

    [BindProperty]
    public CustomerInput Input { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? StatusKind { get; set; }

    public GpioOptions Gpio { get; }

    public IReadOnlyList<Customer> RecentCustomers { get; private set; } = [];

    public bool DatabaseAvailable { get; private set; } = true;

    public string? DatabaseWarning { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadRecentCustomersAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            StatusMessage = "The customer was not added. Correct the highlighted fields and try again.";
            StatusKind = "error";
            await _statusIndicator.ShowFailureAsync();
            await LoadRecentCustomersAsync(cancellationToken);
            return Page();
        }

        var customer = Input.ToCustomer();

        try
        {
            await _customers.AddAsync(customer, cancellationToken);
            await _statusIndicator.ShowSuccessAsync();

            StatusMessage = $"{customer.FullName} was added successfully.";
            StatusKind = "success";
            return RedirectToPage();
        }
        catch (CustomerAlreadyExistsException)
        {
            ModelState.AddModelError("Input.Email", "A customer with this email address already exists.");
            StatusMessage = "The customer was not added because the email address is already registered.";
            StatusKind = "error";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to add customer {Email}.", customer.Email);
            StatusMessage = "The customer could not be saved. Check the MongoDB connection and try again.";
            StatusKind = "error";
        }

        await _statusIndicator.ShowFailureAsync();
        await LoadRecentCustomersAsync(cancellationToken);
        return Page();
    }

    private async Task LoadRecentCustomersAsync(CancellationToken cancellationToken)
    {
        try
        {
            RecentCustomers = await _customers.GetRecentAsync(5, cancellationToken);
            DatabaseAvailable = true;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not load recent customers from MongoDB.");
            DatabaseAvailable = false;
            DatabaseWarning = "MongoDB is unavailable. Verify the Atlas network access list and connection settings.";
            RecentCustomers = [];
        }
    }
}
