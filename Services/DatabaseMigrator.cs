using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SuperTrigger.Web.Data;
using SuperTrigger.Web.Services.Database;

namespace SuperTrigger.Web.Services;

// Owns everything needed to bring the application database — SQLite file or SQL Server database —
// up to the current schema, optionally carry the previous database's data over, record the choice
// in appsettings.json and seed the default admin user. Invoked via
// `SuperTrigger.Web.exe --setup-db <request.json>` (or the older `--migrate-db <sqlite path>`),
// either by a developer locally or by the MSI installer's SetupDatabase custom action — the app
// itself never does any of this on normal startup.
public static class DatabaseMigrator
{
    // SQLite only: this product has always upgraded its SQLite schema with idempotent DDL on top of
    // the InitialCreate migration, rather than with a migration per change. The SQL Server side
    // reconciles against the EF model instead — see SqlServerSchemaBuilder.
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
        "ALTER TABLE OrchSettings ADD COLUMN RetentionDays INTEGER NOT NULL DEFAULT 14",
        "ALTER TABLE MailTriggers ADD COLUMN GraphUseWebhook INTEGER NOT NULL DEFAULT 1",
        "ALTER TABLE OrchSettings ADD COLUMN GraphUsername TEXT NOT NULL DEFAULT ''",
        "ALTER TABLE OrchSettings ADD COLUMN GraphPassword TEXT NOT NULL DEFAULT ''",
        "ALTER TABLE OrchSettings ADD COLUMN GraphDetectionMode TEXT NOT NULL DEFAULT 'Webhook'",
        "ALTER TABLE OrchSettings ADD COLUMN GlobalSharedMailBox TEXT NOT NULL DEFAULT ''",
        "ALTER TABLE OrchSettings ADD COLUMN FileWatchMethod TEXT NOT NULL DEFAULT 'FileSystemWatcher'",
        "ALTER TABLE OrchSettings ADD COLUMN FilePollingIntervalInSeconds INTEGER NOT NULL DEFAULT 30",
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

    // Back-compat entry point for `--migrate-db <path>`: a SQLite-only setup with no data migration.
    public static Task RunAsync(string dbPath) =>
        SetupAsync(
            new DbSetupRequest
            {
                Target = new DbConfig { Provider = DbProviderKind.Sqlite, SqlitePath = dbPath },
                CopyExistingData = false
            },
            Console.Out);

    /// <summary>
    /// Brings the requested database up to the current schema, optionally copies the previous
    /// database's data into it, records the configuration in appsettings.json and makes sure the
    /// default admin user exists. Throws on any failure that leaves the app unable to start.
    /// </summary>
    public static async Task SetupAsync(DbSetupRequest request, TextWriter log)
    {
        var target = request.Target;
        log.WriteLine($"Target database: {target.Describe()}");

        // 1. Make sure the database itself exists (file directory / SQL Server database).
        if (target.IsSqlServer)
            await SqlServerSchemaBuilder.EnsureDatabaseExistsAsync(target, log);
        else
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target.SqlitePath))!);

        // 2. Schema.
        await using (var db = new AppDbContext(target.BuildOptions()))
            await EnsureSchemaAsync(db, target, log);

        // 3. Optionally carry the previous install's data over to a different database.
        await MigrateExistingDataAsync(request, log);

        // 4. Record the choice so the app (and the next upgrade) know where the database lives.
        RecordConfigInAppSettings(target, log);

        // 5. Default admin user — no-op when the database already has one (including a copied one).
        var factory = new SingleOptionsDbContextFactory(target.BuildOptions());
        var appSettingsService = new AppSettingsService(factory);
        var authService = new AuthService(factory, NullLogger<AuthService>.Instance, appSettingsService);
        await authService.EnsureDefaultAdminExistsAsync();

        log.WriteLine("Database setup completed.");
    }

    private static async Task EnsureSchemaAsync(AppDbContext db, DbConfig config, TextWriter log)
    {
        if (config.IsSqlServer)
        {
            await SqlServerSchemaBuilder.EnsureSchemaAsync(db, log);
            return;
        }

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

        // Poll interval moved from minutes to seconds. Only runs the one time the new column is
        // added (guarded by the ADD COLUMN above throwing on repeat runs), so it carries over each
        // install's/upgrade's existing minutes-based value (fresh installs default to 0.5 min = 30s
        // anyway) without ever overwriting a value the user later configures in seconds.
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE OrchSettings ADD COLUMN MailTriggerIntervalInSeconds INTEGER NOT NULL DEFAULT 30");
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE OrchSettings SET MailTriggerIntervalInSeconds = CAST(MailTriggerIntervalInMinutes * 60 AS INTEGER)");
        }
        catch { /* column already exists — leave the user's configured value alone */ }

        // "AppIdGlobal" trigger auth used to pick its method implicitly (Client Secret present ->
        // client credentials, else -> fall back to the delegated global sign-in). That's now an
        // explicit GlobalMailAuthMode setting, using the same values as MailTrigger.MailAuthMode.
        // Only runs the one time the column is added, so it carries over each existing install's
        // effective behavior instead of resetting it — installs with a Client Secret configured
        // become "AppIdGlobal"; installs with a working global sign-in and no secret become
        // "OAuth2Interactive"; everyone else (nothing configured -- e.g. EWS-only installs) keeps
        // the column default of "Interactive".
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE OrchSettings ADD COLUMN GlobalMailAuthMode TEXT NOT NULL DEFAULT 'Interactive'");
            await db.Database.ExecuteSqlRawAsync("""
                UPDATE OrchSettings SET GlobalMailAuthMode = 'AppIdGlobal'
                WHERE GraphClientSecret IS NOT NULL AND GraphClientSecret <> ''
                """);
            await db.Database.ExecuteSqlRawAsync("""
                UPDATE OrchSettings SET GlobalMailAuthMode = 'OAuth2Interactive'
                WHERE (GraphClientSecret IS NULL OR GraphClientSecret = '')
                  AND OAuthGlobalAccessToken IS NOT NULL AND OAuthGlobalAccessToken <> ''
                """);
        }
        catch { /* column already exists — leave the user's configured value alone */ }

        // "Custom App ID (per trigger)" was removed as a global Authentication Mode choice —
        // it has no meaning globally (each trigger configures its own credentials). Any
        // install that had it selected falls back to the safe no-credentials-needed default.
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE OrchSettings SET GlobalMailAuthMode = 'Interactive' WHERE GlobalMailAuthMode = 'AppIdPerTrigger'");
    }

    /// <summary>
    /// Copies the previous install's data into the new database when the upgrade moves to a
    /// different one (SQLite → SQL Server, SQL Server → SQLite, or a different server/file) and the
    /// user chose to keep their data. A plain in-place upgrade never reaches the copy — the old and
    /// new configuration resolve to the same database, so there is nothing to move.
    /// </summary>
    private static async Task MigrateExistingDataAsync(DbSetupRequest request, TextWriter log)
    {
        if (request.PreviousConfigFile is not { Length: > 0 })
            return;

        DbConfig? previous;
        try
        {
            previous = DbConfig.FromAppSettingsFile(request.PreviousConfigFile);
        }
        catch (Exception ex)
        {
            log.WriteLine($"Could not read the previous database configuration from '{request.PreviousConfigFile}': {ex.Message}");
            return;
        }

        if (previous == null)
        {
            log.WriteLine("No previous database configuration found — nothing to migrate.");
            return;
        }

        if (previous.SameTargetAs(request.Target))
        {
            log.WriteLine("The database location is unchanged — upgrading it in place, no data migration needed.");
            return;
        }

        if (!request.CopyExistingData)
        {
            log.WriteLine($"Previous database ({previous.Describe()}) left untouched — starting with a new, empty database as requested.");
            return;
        }

        if (previous.Provider == DbProviderKind.Sqlite && !File.Exists(previous.SqlitePath))
        {
            log.WriteLine($"Previous SQLite database '{previous.SqlitePath}' no longer exists — nothing to copy.");
            return;
        }

        log.WriteLine($"Copying data from the previous database ({previous.Describe()}) into the new one.");

        await using var source = new AppDbContext(previous.BuildOptions());
        await using var target = new AppDbContext(request.Target.BuildOptions());

        // The old database may be several versions behind; bring it up to the current schema first
        // so it can be read through the current model. It is only read from after this point.
        await EnsureSchemaAsync(source, previous, log);

        await DataCopier.CopyAllAsync(source, target, request.Target.IsSqlServer, log);
        log.WriteLine("Data migration completed. The previous database was left in place untouched.");
    }

    // Records the configuration this run set up in appsettings.json next to the exe, so the app
    // knows which database to use and a later MSI upgrade can read it back and prefill its dialogs.
    // Best-effort: a failure here shouldn't be treated as a setup failure, since the database itself
    // is already up to date at this point.
    private static void RecordConfigInAppSettings(DbConfig config, TextWriter log)
    {
        try
        {
            var appSettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(appSettingsPath))
            {
                log.WriteLine($"Warning: no appsettings.json at '{appSettingsPath}' — the database configuration was not recorded.");
                return;
            }

            config.WriteToAppSettings(appSettingsPath);
            log.WriteLine($"Recorded the database configuration in '{appSettingsPath}'.");
        }
        catch (Exception ex)
        {
            log.WriteLine($"Warning: could not record the database configuration in appsettings.json — " +
                          $"the app may not find this database, and future upgrades will not prefill it: {ex.Message}");
        }
    }

    // AuthService/AppSettingsService take IDbContextFactory<AppDbContext> so they can be reused
    // as-is here, outside of DI, against a single fixed connection string.
    private sealed class SingleOptionsDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
