using System.Security.Cryptography;
using System.Text;

namespace BrainSim.App;

/// <summary>Encrypts the API key for the current Windows user, so settings.json never holds it in clear.</summary>
internal static class Dpapi
{
    public static string Protect(string text) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(text), null, DataProtectionScope.CurrentUser));

    public static string Unprotect(string text)
    {
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(text), null, DataProtectionScope.CurrentUser));
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("The stored key is not valid base64.", ex);
        }
    }
}
