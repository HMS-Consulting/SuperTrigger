using System.Security.Cryptography;
using System.Text;

namespace SuperTrigger.Web.Services;

/// <summary>
/// DPAPI-based encryption for sensitive DB fields (passwords, tokens, secrets).
/// Uses LocalMachine scope — any process on the same machine can decrypt.
/// Backward-compatible: Decrypt() returns plain text unchanged if the value was stored before encryption was added.
/// </summary>
public static class FieldEncryption
{
    // Extra entropy — prevents decryption of data from other DPAPI-protected apps on the same machine.
    private static readonly byte[] Entropy = "SuperTrigger.HMS"u8.ToArray();

    public static string Encrypt(string plain)
    {
        var bytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.LocalMachine);
        return Convert.ToBase64String(bytes);
    }

    public static string Decrypt(string stored)
    {
        try
        {
            var bytes = Convert.FromBase64String(stored);
            return Encoding.UTF8.GetString(
                ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.LocalMachine));
        }
        catch
        {
            // Value was stored before encryption was introduced — return as-is.
            return stored;
        }
    }
}
