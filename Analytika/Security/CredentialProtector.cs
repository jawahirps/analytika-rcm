using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace Analytika.Security;

/// <summary>
/// Encrypts portal credentials at rest using ASP.NET Data Protection.
/// Values are stored with a version prefix so legacy Base64-encoded values
/// can still be read and are transparently upgraded on startup.
/// </summary>
public interface ICredentialProtector
{
    string Protect(string plaintext);
    string Unprotect(string stored);
    bool IsProtected(string stored);
}

public class CredentialProtector : ICredentialProtector
{
    private const string Prefix = "dpv1:";
    private readonly IDataProtector _protector;

    public CredentialProtector(IDataProtectionProvider provider)
        => _protector = provider.CreateProtector("Analytika.PortalCredentials.v1");

    public string Protect(string plaintext)
        => Prefix + _protector.Protect(plaintext ?? string.Empty);

    public bool IsProtected(string stored)
        => stored != null && stored.StartsWith(Prefix, StringComparison.Ordinal);

    public string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return string.Empty;
        if (IsProtected(stored))
            return _protector.Unprotect(stored[Prefix.Length..]);
        // Legacy values were stored as Base64-encoded UTF-8
        return Encoding.UTF8.GetString(Convert.FromBase64String(stored));
    }
}
