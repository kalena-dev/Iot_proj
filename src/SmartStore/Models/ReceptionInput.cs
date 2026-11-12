using System.ComponentModel.DataAnnotations;

namespace SmartStore.Models;

public sealed class ReceptionInput
{
    [Required(ErrorMessage = "Choose a product.")]
    [Display(Name = "Product")]
    public string ProductId { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter the quantity received.")]
    [Range(1, 9999, ErrorMessage = "Quantity must be between 1 and 9999.")]
    [Display(Name = "Quantity received")]
    public int? Quantity { get; set; }

    [Required(ErrorMessage = "Enter the date received.")]
    [DataType(DataType.Date)]
    [Display(Name = "Date received")]
    public DateTime? ReceivedOn { get; set; } = DateTime.Today;

    [DataType(DataType.Date)]
    [Display(Name = "Expiry date (optional)")]
    public DateTime? ExpiresOn { get; set; }

    [StringLength(200)]
    [Display(Name = "Supplier / note (optional)")]
    public string? Note { get; set; }

    public Reception ToReception() => new()
    {
        ProductId = ProductId,
        Quantity = Quantity ?? 0,
        ReceivedOn = DateTime.SpecifyKind(ReceivedOn!.Value.Date, DateTimeKind.Utc),
        ExpiresOn = ExpiresOn is { } expires ? DateTime.SpecifyKind(expires.Date, DateTimeKind.Utc) : null,
        Note = string.IsNullOrWhiteSpace(Note) ? null : Note.Trim(),
        CreatedAtUtc = DateTime.UtcNow
    };
}
