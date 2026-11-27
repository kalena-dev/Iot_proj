using System.ComponentModel.DataAnnotations;

namespace SmartStore.Models;

public sealed class ReceptionInput
{
    public const int MaxTags = 500;

    [Required(ErrorMessage = "Choose a product.")]
    [Display(Name = "Product")]
    public string ProductId { get; set; } = string.Empty;

    // One EPC per scanned unit, separated by new lines, spaces or commas.
    [StringLength(MaxTags * 26, ErrorMessage = "Too many tags for one delivery.")]
    [Display(Name = "Scanned RFID tags")]
    public string? Tags { get; set; }

    [Range(0, 9999, ErrorMessage = "Untagged units must be between 0 and 9999.")]
    [Display(Name = "Untagged units (no RFID)")]
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

    public static IReadOnlyList<string> SplitTags(string? tags) =>
        (tags ?? "").Split([' ', '\t', '\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries);

    // Distinct, normalized EPCs, plus the entries that are not valid EPCs.
    public (List<string> Epcs, List<string> Invalid) ParseTags()
    {
        var epcs = new List<string>();
        var invalid = new List<string>();
        foreach (var entry in SplitTags(Tags))
        {
            if (Item.NormalizeEpc(entry) is { } epc) { if (!epcs.Contains(epc)) epcs.Add(epc); }
            else invalid.Add(entry);
        }
        return (epcs, invalid);
    }

    // Quantity on the returned reception is the untagged count; the repository adds the new tags.
    public Reception ToReception() => new()
    {
        ProductId = ProductId,
        Quantity = Quantity ?? 0,
        Epcs = ParseTags().Epcs,
        ReceivedOn = DateTime.SpecifyKind(ReceivedOn!.Value.Date, DateTimeKind.Utc),
        ExpiresOn = ExpiresOn is { } expires ? DateTime.SpecifyKind(expires.Date, DateTimeKind.Utc) : null,
        Note = string.IsNullOrWhiteSpace(Note) ? null : Note.Trim(),
        CreatedAtUtc = DateTime.UtcNow
    };
}
