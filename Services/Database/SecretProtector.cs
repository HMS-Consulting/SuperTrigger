using System.Security.Cryptography;
using System.Text;

namespace SuperTrigger.Web.Services.Database;

/// <summary>
/// DPAPI protection for secrets that live in configuration files rather than in the database
/// (currently the SQL Server password in appsettings.json). Separate from
/// <see cref="FieldEncryption"/> — different entropy, and the stored form carries a marker prefix
/// so a hand-written plaintext value stays usable.
/// </summary>
/// <remarks>
/// LocalMachine scope: the installer (running as SYSTEM) writes the value and the IIS application
/// pool identity reads it back, so a per-user key would be useless here. The same format is
/// produced by the MSI custom action (CustomActions.Web → ProtectSecret), which is why the prefix,
/// the entropy bytes and the base64 encoding must not change without changing both sides.
/// </remarks>
public static class SecretProtector
{
    public const string Prefix = "DPAPI:";

    private static readonly byte[] Entropy = "SuperTrigger.Web.DbConfig"u8.ToArray();

    public static bool IsProtected(string? value) =>
        value != null && value.StartsWith(Prefix, StringComparison.Ordinal);

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var bytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.LocalMachine);
        return Prefix + Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// Reverses <see cref="Protect"/>. A value without the marker prefix is returned unchanged, so
    /// an admin can drop a plaintext password into appsettings.json by hand and have it work.
    /// </summary>
    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (!IsProtected(stored)) return stored;

        try
        {
            var bytes = Convert.FromBase64String(stored.Substring(Prefix.Length));
            return Encoding.UTF8.GetString(
                ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.LocalMachine));
        }
        catch (Exception ex)
        {
            // Protected on a different machine (config copied between servers), or corrupted.
            throw new InvalidOperationException(
                "An encrypted database password could not be decrypted on this machine. Re-run the installer " +
                "(or replace Database:SqlServer:Password in appsettings.json with a plaintext value) to fix it.", ex);
        }
    }

    /// <summary>Protects <paramref name="value"/> unless it already carries the marker prefix.</summary>
    public static string EnsureProtected(string? value) =>
        string.IsNullOrEmpty(value) ? "" : IsProtected(value) ? value! : Protect(value!);
}
