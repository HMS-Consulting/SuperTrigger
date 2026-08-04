using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SuperTrigger.Web.Data;

namespace SuperTrigger.Web.Services;

// Owns everything needed to bring a SQLite DB file at an arbitrary path up to the current
// schema and seed the default admin user. Invoked via `SuperTrigger.Web.exe --migrate-db <path>`,
// either by a developer locally or by the MSI installer's MigrateDatabase custom action — the app
// itself no longer does any of this on normal startup.
public static class DatabaseMigrator
{
    private static readonly string[] SchemaBootstrapSql =
    [
        "ALTER TABLE MailTriggers ADD COLUMN MailAuthMode TEXT NOT NULL DEFAULT 'Interactive'",
        "ALTER TABLE MailTriggers ADD COLUMN GraphClientId TEXT NOT NULL DEFAULT ''",
        "ALTER TABLE MailTriggers ADD COLUMN GraphClientSecret TEXT NOT NULL DEFAULT ''",
        "ALTER TABLE MailTriggers ADD COLUMN GraphSubscriptionId TEXT",
        "ALTER TABLE MailTriggers ADD COLUMN GraphSubscriptionExpiry TEXT",
        "ALTER TABLE MailTriggers ADD COLUMN OAuthAccessToken TEXT",
        "ALTER TABLE MailTriggers ADD COLUMN OAuthRefreshToken TEXT",
        "ALTER TABLE MailTriggers ADD COLUMN OAuthTokenExpiry TEXT",
        "ALTER TABLE MailTriggers ADD COLUMN OAuthUserUpn TEXT",
        "ALTER TABLE OrchSettings ADD COLUMN PublicBaseUrl TEXT NOT NULL DEFAULT ''",
        "ALTER TABLE OrchSettings ADD COLUMN AdUsername TEXT NOT NULL DEFAULT ''",
        "ALTER TABLE OrchSettings ADD COLUMN AdPassword TEXT NOT NULL DEFAULT ''",
        "ALTER TABLE OrchSettings ADD COLUMN GraphTenantId TEXT NOT NULL DEFAULT ''",
        "ALTER TABLE OrchSettings ADD COLUMN GraphClientId TEXT NOT NULL DEFAULT ''",
        "ALTER TABLE OrchSettings ADD COLUMN GraphClientSecret TEXT NOT NULL DEFAULT ''",
        "ALTER TABLE OrchSettings ADD COLUMN OAuthGlobalAccessToken TEXT",
        "ALTER TABLE OrchSettings ADD COLUMN OAuthGlobalRefreshToken TEXT",
        "ALTER TABLE OrchSettings ADD COLUMN OAuthGlobalTokenExpiry TEXT",
        "ALTER TABLE OrchSettings ADD COLUMN OAuthGlobalUserUpn TEXT",
        "ALTER TABLE FileTriggers ADD COLUMN WatcherUsername TEXT NOT NULL DEFAULT ''",
        "ALTER TABLE FileTriggers ADD COLUMN WatcherPassword TEXT NOT NULL DEFAULT ''",
        "ALTER TABLE FileTriggers ADD COLUMN FileNameMatchMode TEXT NOT NULL DEFAULT 'Contains'",
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_MailTriggers_TriggerName ON MailTriggers(TriggerName)",
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_FileTriggers_TriggerName ON FileTriggers(TriggerName)",
        """
        CREATE TABLE IF NOT EXISTS AuditLogs (
            Id INTEGER NOT NULL CONSTRAINT PK_AuditLogs PRIMARY KEY AUTOINCREMENT,
            CreatedAt TEXT NOT NULL,
            Username TEXT NOT NULL,
            Action TEXT NOT NULL,
            EntityType TEXT NOT NULL,
            EntityName TEXT NOT NULL,
            Details TEXT NOT NULL
        )
        """
    ];

    public static async Task RunAsync(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        IDbContextFactory<AppDbContext> dbFactory = new SingleOptionsDbContextFactory(options);

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.MigrateAsync();

            foreach (var sql in SchemaBootstrapSql)
            {
                try { await db.Database.ExecuteSqlRawAsync(sql); }
                catch { /* column/table already exists — safe to ignore */ }
            }

            // Migrate legacy UseGraphApi=1 rows → MailAuthMode='AppIdPerTrigger'
            try
            {
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE MailTriggers SET MailAuthMode='AppIdPerTrigger' WHERE UseGraphApi=1 AND MailAuthMode='Interactive'");
            }
            catch { /* UseGraphApi column may not exist on fresh installs */ }
        }

        var appSettingsService = new AppSettingsService(dbFactory);
        var authService = new AuthService(dbFactory, NullLogger<AuthService>.Instance, appSettingsService);
        await authService.EnsureDefaultAdminExistsAsync();
    }

    // Records the path this run migrated as Database:Path in appsettings.json next to the exe, so a
    // later MSI upgrade can read it back and prefill the DB-location dialog with the same value.
    // Best-effort: a failure here shouldn't be treated as a migration failure, since the DB itself
    // is already up to date at this point.
    public static void RecordDbPathInAppSettings(string dbPath)
    {
        try
        {
            var appSettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(appSettingsPath)) return;

            var json = JsonNode.Parse(File.ReadAllText(appSettingsPath)) as JsonObject ?? new JsonObject();
            var database = json["Database"] as JsonObject ?? new JsonObject();
            database["Path"] = dbPath;
            json["Database"] = database;

            File.WriteAllText(appSettingsPath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: could not record Database:Path in appsettings.json — future upgrades will not prefill this DB path: {ex.Message}");
        }
    }

    // AuthService/AppSettingsService take IDbContextFactory<AppDbContext> so they can be reused
    // as-is here, outside of DI, against a single fixed connection string.
    private sealed class SingleOptionsDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
