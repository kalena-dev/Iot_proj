using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SmartStore.Data;
using SmartStore.Models;

namespace SmartStore.Pages.Products;

public sealed class CreateModel(IProductRepository repository, ILogger<CreateModel> logger) : PageModel
{
    [BindProperty] public ProductInput Input { get; set; } = new();
    public string? ErrorMessage { get; private set; }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public string? StatusKind { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        await LoadCategoriesAsync(cancellationToken);

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            await LoadCategoriesAsync(cancellationToken);
            return Page();
        }

        try
        {
            var product = Input.ToNewProduct();
            await repository.AddAsync(product, cancellationToken);
            StatusMessage = $"Product \"{product.Name}\" added successfully. Receive stock to make it available.";
            StatusKind = "success";
            return RedirectToPage("Receive", new { id = product.Id });
        }
        catch (DuplicateProductCodeException exception)
        {
            ModelState.AddModelError(exception.Field == "EPC" ? "Input.Epc" : "Input.Upc", exception.Message);
        }
        catch (Exception exception) when (exception is TimeoutException or MongoDB.Driver.MongoException)
        {
            logger.LogWarning(exception, "Cannot add product.");
            ErrorMessage = "The product could not be saved. Check the MongoDB connection.";
        }

        await LoadCategoriesAsync(cancellationToken);
        return Page();
    }

    private async Task LoadCategoriesAsync(CancellationToken cancellationToken)
    {
        try
        {
            ViewData["Categories"] = (await repository.GetAllAsync(cancellationToken))
                .Select(p => p.Category).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();
        }
        catch (Exception exception) when (exception is TimeoutException or MongoDB.Driver.MongoException)
        {
            logger.LogWarning(exception, "Cannot load categories.");
        }
    }
}
