namespace Analytika.Models;

public class Facility
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;

    // Official DHPO/eClaimLink facility license identity, matched from the imported
    // facility list. Kept alongside the short display Name (non-destructive).
    public string? FullName { get; set; }      // official license name
    public string? LicenseCode { get; set; }   // DHA-F-xxxxx license code
}
