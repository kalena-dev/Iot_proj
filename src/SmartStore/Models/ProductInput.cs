using System.ComponentModel.DataAnnotations;

namespace SmartStore.Models;

public sealed class ProductInput
{
    [Required(ErrorMessage = "Enter the product name.")]
    [StringLength(100, MinimumLength = 2, ErrorMessage = "Name must be 2–100 characters.")]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter a category.")]
    [StringLength(50, MinimumLength = 2, ErrorMessage = "Category must be 2–50 characters.")]
    public string Category { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter a price.")]
    [Range(typeof(decimal), "0.01", "99999.99", ErrorMessage = "Price must be between $0.01 and $99,999.99.")]
    [Display(Name = "Price (CAD)")]
    public decimal? Price { get; set; }

    [Required(ErrorMessage = "Scan or type the UPC barcode.")]
    [RegularExpression(@"^\s*\d{1,13}\s*$", ErrorMessage = "UPC must be digits only, at most 13.")]
    [Display(Name = "UPC (barcode)")]
    public string Upc { get; set; } = string.Empty;

    [Required(ErrorMessage = "Scan or type the RFID EPC.")]
    [RegularExpression(@"^\s*[0-9A-Fa-f]{1,24}\s*$", ErrorMessage = "EPC must be hexadecimal (0–9, A–F), at most 24 characters.")]
    [Display(Name = "EPC (RFID tag)")]
    public string Epc { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter the producer company.")]
    [StringLength(100, MinimumLength = 2, ErrorMessage = "Producer must be 2–100 characters.")]
    [Display(Name = "Producer company")]
    public string Producer { get; set; } = string.Empty;

    [StringLength(500)]
    public string? Description { get; set; }

    [Url(ErrorMessage = "Enter a full image link starting with http:// or https://.")]
    [StringLength(500)]
    [Display(Name = "Image URL")]
    public string? ImageUrl { get; set; }

    public static ProductInput FromProduct(Product product) => new()
    {
        Name = product.Name,
        Category = product.Category,
        Price = product.Price,
        Upc = product.Upc,
        Epc = product.Epc,
        Producer = product.Producer,
        Description = product.Description,
        ImageUrl = product.ImageUrl
    };

    // Copies the form values onto a product (new or existing). Stock is not touched.
    public void ApplyTo(Product product)
    {
        product.Name = Name.Trim();
        product.Category = Category.Trim();
        product.Price = decimal.Round(Price ?? 0, 2);
        product.Upc = Upc.Trim();
        product.Epc = Epc.Trim().ToUpperInvariant();
        product.Producer = Producer.Trim();
        product.Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim();
        product.ImageUrl = string.IsNullOrWhiteSpace(ImageUrl) ? null : ImageUrl.Trim();
        product.UpdatedAtUtc = DateTime.UtcNow;
    }

    public Product ToNewProduct()
    {
        var product = new Product
        {
            Name = "", Category = "", Upc = "", Epc = "", Producer = "",
            CreatedAtUtc = DateTime.UtcNow
        };
        ApplyTo(product);
        return product;
    }
}
