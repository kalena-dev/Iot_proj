using System.ComponentModel.DataAnnotations;

namespace SmartStore.Models;

public sealed class CustomerInput
{
    [Required(ErrorMessage = "Enter the customer's first name.")]
    [StringLength(60, MinimumLength = 2)]
    [Display(Name = "First name")]
    public string FirstName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter the customer's last name.")]
    [StringLength(60, MinimumLength = 2)]
    [Display(Name = "Last name")]
    public string LastName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter an email address.")]
    [EmailAddress(ErrorMessage = "Enter a valid email address.")]
    [StringLength(254)]
    [Display(Name = "Email address")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter a telephone number.")]
    [Phone(ErrorMessage = "Enter a valid telephone number.")]
    [StringLength(30)]
    [Display(Name = "Telephone number")]
    public string Phone { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter a street address.")]
    [StringLength(120, MinimumLength = 4)]
    [Display(Name = "Street address")]
    public string Street { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter a city.")]
    [StringLength(80)]
    public string City { get; set; } = string.Empty;

    [Required(ErrorMessage = "Select a province or territory.")]
    [RegularExpression(@"^(AB|BC|MB|NB|NL|NS|NT|NU|ON|PE|QC|SK|YT)$",
        ErrorMessage = "Select a valid province or territory.")]
    [Display(Name = "Province / territory")]
    public string Province { get; set; } = "QC";

    [Required(ErrorMessage = "Enter a postal code.")]
    [RegularExpression(
        @"^[A-Za-z]\d[A-Za-z][ -]?\d[A-Za-z]\d$",
        ErrorMessage = "Use a Canadian postal code such as H3G 1M8.")]
    [Display(Name = "Postal code")]
    public string PostalCode { get; set; } = string.Empty;

    public Customer ToCustomer() => new()
    {
        FirstName = FirstName.Trim(),
        LastName = LastName.Trim(),
        Email = Email.Trim().ToLowerInvariant(),
        Phone = Phone.Trim(),
        Address = new CustomerAddress
        {
            Street = Street.Trim(),
            City = City.Trim(),
            Province = Province.Trim().ToUpperInvariant(),
            PostalCode = NormalizePostalCode(PostalCode),
            Country = "Canada"
        },
        CreatedAtUtc = DateTime.UtcNow
    };

    private static string NormalizePostalCode(string value)
    {
        var compact = value.Replace(" ", string.Empty).Replace("-", string.Empty).ToUpperInvariant();
        return compact.Length == 6 ? $"{compact[..3]} {compact[3..]}" : value.Trim().ToUpperInvariant();
    }
}
