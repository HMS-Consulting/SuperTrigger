using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SuperTrigger.Web.Data;

namespace SuperTrigger.Web.Services.Database;

public enum DbProviderKind
{
    Sqlite,
    SqlServer
}

/// <summary>
/// Everything needed to reach the application database, in the shape it is persisted in
/// appsettings.json. Owns connection-string construction so the app, the <c>--setup-db</c> CLI
/// mode and the MSI-driven upgrade path all build the exact same connection.
/// </summary>
/// <remarks>
/// appsettings.json layout (only the keys that apply to the selected provider matter):
/// <code>
/// "Database": {
///   "Provider": "Sqlite" | "SqlServer",
///   "Path": "C:\\ProgramData\\HMS\\SuperTriggerWeb\\supertrigger.db",
///   "SqlServer": {
///     "ConnectionString": "",            // set this to bypass the fields below entirely
///     "Server": "SQL01",
///     "Port": "1433",                    // optional
///     "Instance": "SQLEXPRESS",          // optional
///     "Database": "SuperTriggerWeb",
///     "AuthMode": "Windows" | "Sql",
///     "Username": "",
///     "Password": "DPAPI:...",           // DPAPI-encrypted; plaintext also accepted
///     "Encrypt": true,
///     "TrustServerCertificate": true,
///     "ConnectTimeoutSeconds": 30
///   }
/// }
/// </code>
/// </remarks>
public sealed class DbConfig
{
    public const string WindowsAuth = "Windows";
    public const string SqlAuth = "Sql";

    public DbProviderKind Provider { get; set; } = DbProviderKind.Sqlite;

    // --- SQLite ---
    public string SqlitePath { get; set; } = DefaultSqlitePath;

    // --- SQL Server ---
    /// <summary>Verbatim connection string. When set, every other SQL Server field is ignored.</summary>
    public string ConnectionStringOverride { get; set; } = "";
    public string Server { get; set; } = "";
    public string Port { get; set; } = "";
    public string Instance { get; set; } = "";
    public string DatabaseName { get; set; } = "";
    public string AuthMode { get; set; } = WindowsAuth;
    public string Username { get; set; } = "";
    /// <summary>Plaintext in memory; always persisted through <see cref="SecretProtector"/>.</summary>
    public string Password { get; set; } = "";
    public bool Encrypt { get; set; } = true;
    public bool TrustServerCertificate { get; set; } = true;
    public int ConnectTimeoutSeconds { get; set; } = 30;

    public static string DefaultSqlitePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "HMS", "SuperTriggerWeb", "supertrigger.db");

    public bool IsSqlServer => Provider == DbProviderKind.SqlServer;

    // ---------------------------------------------------------------- reading

    public static DbConfig FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("Database");
        var sql = section.GetSection("SqlServer");

        var cfg = new DbConfig
        {
            Provider = ParseProvider(section["Provider"]),
            SqlitePath = section["Path"] is { Length: > 0 } p ? p : DefaultSqlitePath,
            ConnectionStringOverride = sql["ConnectionString"] ?? "",
            Server = sql["Server"] ?? "",
            Port = sql["Port"] ?? "",
            Instance = sql["Instance"] ?? "",
            DatabaseName = sql["Database"] ?? "",
            AuthMode = NormalizeAuthMode(sql["AuthMode"]),
            Username = sql["Username"] ?? "",
            Password = SecretProtector.Unprotect(sql["Password"]),
            Encrypt = ParseBool(sql["Encrypt"], true),
            TrustServerCertificate = ParseBool(sql["TrustServerCertificate"], true),
            ConnectTimeoutSeconds = int.TryParse(sql["ConnectTimeoutSeconds"], out var t) && t > 0 ? t : 30
        };

        return cfg;
    }

    /// <summary>
    /// Reads a specific appsettings.json file (rather than the running app's configuration).
    /// Used to load the *previous* install's database settings during an MSI upgrade, from the
    /// copy the installer takes before the old files are replaced. Returns null when the file
    /// doesn't exist or carries no Database section.
    /// </summary>
    public static DbConfig? FromAppSettingsFile(string appSettingsPath)
    {
        if (!File.Exists(appSettingsPath)) return null;

        var root = JsonNode.Parse(File.ReadAllText(appSettingsPath)) as JsonObject;
        if (root?["Database"] is not JsonObject db) return null;

        var sql = db["SqlServer"] as JsonObject ?? new JsonObject();

        return new DbConfig
        {
            Provider = ParseProvider(Str(db["Provider"])),
            SqlitePath = Str(db["Path"]) is { Length: > 0 } p ? p : DefaultSqlitePath,
            ConnectionStringOverride = Str(sql["ConnectionString"]),
            Server = Str(sql["Server"]),
            Port = Str(sql["Port"]),
            Instance = Str(sql["Instance"]),
            DatabaseName = Str(sql["Database"]),
            AuthMode = NormalizeAuthMode(Str(sql["AuthMode"])),
            Username = Str(sql["Username"]),
            Password = SecretProtector.Unprotect(Str(sql["Password"])),
            Encrypt = ParseBool(Str(sql["Encrypt"]), true),
            TrustServerCertificate = ParseBool(Str(sql["TrustServerCertificate"]), true),
            ConnectTimeoutSeconds = int.TryParse(Str(sql["ConnectTimeoutSeconds"]), out var t) && t > 0 ? t : 30
        };

        // ToString() on a JsonNode yields the unquoted scalar value (string, number or boolean).
        static string Str(JsonNode? n) => n?.ToString() ?? "";
    }

    // ---------------------------------------------------------------- writing

    /// <summary>
    /// Persists this configuration into the Database section of an appsettings.json file, leaving
    /// every other key in the file untouched. The SQL password is DPAPI-encrypted on the way out.
    /// </summary>
    public void WriteToAppSettings(string appSettingsPath)
    {
        var root = File.Exists(appSettingsPath)
            ? JsonNode.Parse(File.ReadAllText(appSettingsPath)) as JsonObject ?? new JsonObject()
            : new JsonObject();

        var db = root["Database"] as JsonObject ?? new JsonObject();
        db["Provider"] = Provider.ToString();
        db["Path"] = SqlitePath;

        var sql = db["SqlServer"] as JsonObject ?? new JsonObject();
        sql["ConnectionString"] = ConnectionStringOverride;
        sql["Server"] = Server;
        sql["Port"] = Port;
        sql["Instance"] = Instance;
        sql["Database"] = DatabaseName;
        sql["AuthMode"] = AuthMode;
        sql["Username"] = Username;
        // Windows auth never needs the password; don't keep a stale one on disk.
        sql["Password"] = AuthMode == SqlAuth ? SecretProtector.Protect(Password) : "";
        sql["Encrypt"] = Encrypt;
        sql["TrustServerCertificate"] = TrustServerCertificate;
        sql["ConnectTimeoutSeconds"] = ConnectTimeoutSeconds;

        db["SqlServer"] = sql;
        root["Database"] = db;

        File.WriteAllText(appSettingsPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    // ------------------------------------------------------- connection setup

    public string BuildConnectionString()
    {
        if (Provider == DbProviderKind.Sqlite)
            return $"Data Source={SqlitePath}";

        if (ConnectionStringOverride is { Length: > 0 })
            return ConnectionStringOverride;

        if (string.IsNullOrWhiteSpace(Server))
            throw new InvalidOperationException(
                "Database:SqlServer:Server is not configured (and no Database:SqlServer:ConnectionString was supplied).");
        if (string.IsNullOrWhiteSpace(DatabaseName))
            throw new InvalidOperationException("Database:SqlServer:Database is not configured.");

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = BuildDataSource(),
            InitialCatalog = DatabaseName,
            Encrypt = Encrypt,
            TrustServerCertificate = TrustServerCertificate,
            ConnectTimeout = ConnectTimeoutSeconds,
            ApplicationName = "SuperTrigger.Web",
            MultipleActiveResultSets = true
        };

        if (AuthMode == SqlAuth)
        {
            builder.IntegratedSecurity = false;
            builder.UserID = Username;
            builder.Password = Password;
        }
        else
        {
            // Connects as the IIS application pool identity (or as SYSTEM, during installation).
            builder.IntegratedSecurity = true;
        }

        return builder.ConnectionString;
    }

    /// <summary>
    /// "host", "host\instance", "host,port" or "host\instance,port" — a named instance and an
    /// explicit port can both be supplied; SQL Server then connects on the port.
    /// </summary>
    private string BuildDataSource()
    {
        var source = Server.Trim();
        if (Instance is { Length: > 0 }) source += "\\" + Instance.Trim();
        if (Port is { Length: > 0 }) source += "," + Port.Trim();
        return source;
    }

    /// <param name="enableRetryOnFailure">
    /// Only for the long-running web app: retries transient SQL Server/network faults. Left off
    /// for the installer's own contexts, whose explicit transactions are incompatible with it.
    /// </param>
    public DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, bool enableRetryOnFailure = false)
    {
        var connectionString = BuildConnectionString();

        if (Provider == DbProviderKind.SqlServer)
        {
            options.UseSqlServer(connectionString, sql =>
            {
                sql.CommandTimeout(Math.Max(ConnectTimeoutSeconds, 60));
                if (enableRetryOnFailure) sql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null);
            });
        }
        else
        {
            options.UseSqlite(connectionString);
        }

        return options;
    }

    public DbContextOptions<AppDbContext> BuildOptions(bool enableRetryOnFailure = false)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        Configure(builder, enableRetryOnFailure);
        return builder.Options;
    }

    // ------------------------------------------------------------------ misc

    /// <summary>
    /// True when both configurations point at the same physical database — the test that decides
    /// whether an upgrade is a plain in-place upgrade or a move to a different database.
    /// </summary>
    public bool SameTargetAs(DbConfig? other)
    {
        if (other == null || other.Provider != Provider) return false;

        if (Provider == DbProviderKind.Sqlite)
            return PathsEqual(SqlitePath, other.SqlitePath);

        if (ConnectionStringOverride is { Length: > 0 } || other.ConnectionStringOverride is { Length: > 0 })
            return string.Equals(ConnectionStringOverride, other.ConnectionStringOverride, StringComparison.OrdinalIgnoreCase);

        return string.Equals(BuildDataSource(), other.BuildDataSource(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(DatabaseName, other.DatabaseName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Log-safe one-liner — never includes the password.</summary>
    public string Describe() => Provider == DbProviderKind.Sqlite
        ? $"SQLite file '{SqlitePath}'"
        : ConnectionStringOverride is { Length: > 0 }
            ? "SQL Server (explicit connection string from appsettings.json)"
            : $"SQL Server '{BuildDataSource()}' database '{DatabaseName}' " +
              (AuthMode == SqlAuth ? $"(SQL login '{Username}')" : "(Windows authentication)");

    public static DbProviderKind ParseProvider(string? value) =>
        value != null && value.Trim().Equals("SqlServer", StringComparison.OrdinalIgnoreCase)
            ? DbProviderKind.SqlServer
            : DbProviderKind.Sqlite;

    public static string NormalizeAuthMode(string? value) =>
        value != null && value.Trim().Equals(SqlAuth, StringComparison.OrdinalIgnoreCase)
            ? SqlAuth
            : WindowsAuth;

    private static bool ParseBool(string? value, bool fallback) =>
        value switch
        {
            null or "" => fallback,
            "1" => true,
            "0" => false,
            _ => bool.TryParse(value, out var b) ? b : fallback
        };
}
