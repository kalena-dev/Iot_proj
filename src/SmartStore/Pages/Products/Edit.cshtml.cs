using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SmartStore.Data;
using SmartStore.Models;

namespace SmartStore.Pages.Products;

public sealed class EditModel(IProductRepository repository, ILogger<EditModel> logger) : PageModel
{
    [BindProperty] public ProductInput Input { get; set; } = new();
    public Product? Product { get; private set; }
    public string? ErrorMessage { get; private set; }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public string? StatusKind { get; set; }

    public async Task<IActionResult> OnGetAsync(string id, CancellationToken cancellationToken)
    {
        if (await LoadProductAsync(id, cancellationToken) is { } redirect) return redirect;

        Input = ProductInput.FromProduct(Product!);
        await LoadCategoriesAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string id, CancellationToken cancellationToken)
    {
        if (await LoadProductAsync(id, cancellationToken) is { } redirect) return redirect;

        if (ModelState.IsValid)
        {
            try
            {
                var product = Product!;
                Input.ApplyTo(product);
                await repository.UpdateAsync(product, cancellationToken);
                StatusMessage = $"Product \"{product.Name}\" updated successfully.";
                StatusKind = "success";
                return RedirectToPage("Index");
            }
            catch (DuplicateProductCodeException exception)
            {
                ModelState.AddModelError(exception.Field == "EPC" ? "Input.Epc" : "Input.Upc", exception.Message);
            }
            catch (ProductNotFoundException)
            {
                return NotFoundRedirect();
            }
            catch (Exception exception) when (exception is TimeoutException or MongoDB.Driver.MongoException)
            {
                logger.LogWarning(exception, "Cannot update product {ProductId}.", id);
                ErrorMessage = "The changes could not be saved. Check the MongoDB connection.";
            }
        }

        await LoadCategoriesAsync(cancellationToken);
        return Page();
    }

    // Returns a redirect if the product can't be shown, or null once Product is loaded.
    private async Task<IActionResult?> LoadProductAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            Product = await repository.GetByIdAsync(id, cancellationToken);
            return Product is null ? NotFoundRedirect() : null;
        }
        catch (Exception exception) when (exception is TimeoutException or MongoDB.Driver.MongoException)
        {
            logger.LogWarning(exception, "Cannot load product {ProductId}.", id);
            StatusMessage = "The product could not be loaded. Check the MongoDB connection.";
            StatusKind = "error";
            return RedirectToPage("Index");
        }
    }

    private IActionResult NotFoundRedirect()
    {
        StatusMessage = "That product no longer exists. It may have been deleted.";
        StatusKind = "error";
        return RedirectToPage("Index");
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
