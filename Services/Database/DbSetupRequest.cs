using System.IO;
using System.Text.Json.Nodes;

namespace SuperTrigger.Web.Services.Database;

/// <summary>
/// The instruction file consumed by <c>SuperTrigger.Web.exe --setup-db &lt;file&gt;</c>: which
/// database to use from now on, and what to do about the data in the previous one.
/// </summary>
/// <remarks>
/// Written by the MSI's PrepareDbSetupRequest custom action (which runs immediately, so it can
/// read the installer properties) and read by the deferred SetupDatabase action's child process.
/// A file rather than a command line because the SQL password would otherwise be visible in the
/// process arguments — it is DPAPI-protected inside the file
/// (<see cref="SecretProtector"/>) and the installer deletes the file afterwards.
///
/// Also usable by hand:
/// <code>
/// SuperTrigger.Web.exe --setup-db C:\Temp\dbsetup.json
/// </code>
/// </remarks>
public sealed class DbSetupRequest
{
    public DbConfig Target { get; set; } = new();

    /// <summary>
    /// When the previous install pointed at a different database, copy its contents into the new
    /// one instead of starting from an empty database.
    /// </summary>
    public bool CopyExistingData { get; set; } = true;

    /// <summary>
    /// Path to the previous install's appsettings.json (the installer keeps a copy before the old
    /// files are removed). Empty on a fresh install.
    /// </summary>
    public string PreviousConfigFile { get; set; } = "";

    public static DbSetupRequest FromJsonFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Database setup request file not found: {path}");

        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                   ?? throw new InvalidOperationException($"'{path}' is not a JSON object.");

        var target = new DbConfig
        {
            Provider = DbConfig.ParseProvider(Str(root, "Provider")),
            SqlitePath = Str(root, "SqlitePath") is { Length: > 0 } p ? p : DbConfig.DefaultSqlitePath,
            ConnectionStringOverride = Str(root, "ConnectionString"),
            Server = Str(root, "Server"),
            Port = Str(root, "Port"),
            Instance = Str(root, "Instance"),
            DatabaseName = Str(root, "Database"),
            AuthMode = DbConfig.NormalizeAuthMode(Str(root, "AuthMode")),
            Username = Str(root, "Username"),
            Password = SecretProtector.Unprotect(Str(root, "Password")),
            Encrypt = Bool(root, "Encrypt", true),
            TrustServerCertificate = Bool(root, "TrustServerCertificate", true)
        };

        if (int.TryParse(Str(root, "ConnectTimeoutSeconds"), out var timeout) && timeout > 0)
            target.ConnectTimeoutSeconds = timeout;

        return new DbSetupRequest
        {
            Target = target,
            // "Copy" (the installer's wording) or a plain boolean.
            CopyExistingData = Str(root, "DataAction") is { Length: > 0 } action
                ? action.Equals("Copy", StringComparison.OrdinalIgnoreCase)
                : Bool(root, "CopyExistingData", true),
            PreviousConfigFile = Str(root, "PreviousConfigFile")
        };
    }

    // JsonNode.ToString() yields the unquoted scalar value for strings, numbers and booleans alike.
    private static string Str(JsonObject root, string name) => root[name]?.ToString() ?? "";

    private static bool Bool(JsonObject root, string name, bool fallback) =>
        Str(root, name) switch
        {
            "" => fallback,
            "1" => true,
            "0" => false,
            var s => bool.TryParse(s, out var b) ? b : fallback
        };
}
