using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace Analytika.Services;

/// <summary>
/// Encrypts/decrypts portal credentials with ASP.NET Core Data Protection.
/// New values are stored as "v1:&lt;protected&gt;"; values without the prefix are
/// treated as legacy Base64 (the original storage format) so existing rows
/// keep working until the startup migration re-encrypts them.
/// </summary>
public interface ICredentialProtector
{
    string Protect(string plaintext);
    bool TryUnprotect(string stored, out string plaintext);
    bool IsLegacy(string stored);
}

public class CredentialProtector : ICredentialProtector
{
    private const string Prefix = "v1:";
    private readonly IDataProtector _protector;

    public CredentialProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("Analytika.PortalCredentials");
    }

    public string Protect(string plaintext) => Prefix + _protector.Protect(plaintext);

    public bool IsLegacy(string stored) =>
        !string.IsNullOrEmpty(stored) && !stored.StartsWith(Prefix, StringComparison.Ordinal);

    public bool TryUnprotect(string stored, out string plaintext)
    {
        plaintext = string.Empty;
        if (string.IsNullOrEmpty(stored)) return false;

        try
        {
            plaintext = stored.StartsWith(Prefix, StringComparison.Ordinal)
                ? _protector.Unprotect(stored[Prefix.Length..])
                : Encoding.UTF8.GetString(Convert.FromBase64String(stored));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
