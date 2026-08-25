using System.IO;
using Microsoft.EntityFrameworkCore;
using SuperTrigger.Web.Data;
using SuperTrigger.Web.Data.Entities;

namespace SuperTrigger.Web.Services.Database;

/// <summary>
/// Copies every row of the application database from one provider/location to another — the
/// "keep my existing data" half of switching between SQLite and SQL Server during an upgrade.
/// </summary>
/// <remarks>
/// The copy runs through EF Core rather than at the storage level, which is what makes it work
/// across providers at all, and also what keeps the encrypted columns correct: the value
/// converters in <see cref="AppDbContext"/> decrypt on read from the source and re-encrypt on
/// write to the target (see <see cref="FieldEncryption"/> — DPAPI LocalMachine, so this must run
/// on the machine that owns the data).
///
/// Primary keys are preserved, which on SQL Server needs IDENTITY_INSERT around the inserts.
/// A table that already has rows in the target is left alone rather than merged — except the
/// single OrchSettings row, which is replaced, since a freshly created target always has the
/// seeded default one and the user asked for the old settings.
/// </remarks>
internal static class DataCopier
{
    public static async Task CopyAllAsync(AppDbContext source, AppDbContext target, bool targetIsSqlServer, TextWriter log)
    {
        // No foreign keys between these tables, so the order is cosmetic.
        await CopySettingsAsync(source, target, log);
        await CopyTableAsync(source, target, c => c.LocalUsers, targetIsSqlServer, log);
        await CopyTableAsync(source, target, c => c.AdPrincipals, targetIsSqlServer, log);
        await CopyTableAsync(source, target, c => c.FileTriggers, targetIsSqlServer, log);
        await CopyTableAsync(source, target, c => c.MailTriggers, targetIsSqlServer, log);
        await CopyTableAsync(source, target, c => c.QueueItemLogs, targetIsSqlServer, log);
        await CopyTableAsync(source, target, c => c.AuditLogs, targetIsSqlServer, log);
    }

    /// <summary>
    /// OrchSettings is a single row that every freshly set-up database already has (seeded by the
    /// SQLite migration / by <see cref="SqlServerSchemaBuilder"/>), so its values are written onto
    /// that row instead of being deleted and re-inserted. Updating rather than inserting also
    /// sidesteps the legacy MailTriggerIntervalInMinutes column, which the original SQLite
    /// migration created NOT NULL without a default and which the model no longer knows about — so
    /// any EF INSERT into a SQLite OrchSettings table fails, while an UPDATE is unaffected.
    /// </summary>
    private static async Task CopySettingsAsync(AppDbContext source, AppDbContext target, TextWriter log)
    {
        var sourceSettings = await source.OrchSettings.AsNoTracking().FirstOrDefaultAsync();
        if (sourceSettings == null)
        {
            log.WriteLine("  OrchSettings: the old database has no settings row, keeping the defaults");
            return;
        }

        var targetSettings = await target.OrchSettings.FirstOrDefaultAsync();
        if (targetSettings == null)
        {
            target.OrchSettings.Add(sourceSettings);
            await target.SaveChangesAsync();
        }
        else
        {
            // Same row, so the key isn't being changed — SetValues then copies every mapped column.
            sourceSettings.Id = targetSettings.Id;
            target.Entry(targetSettings).CurrentValues.SetValues(sourceSettings);
            await target.SaveChangesAsync();
        }

        foreach (var entry in target.ChangeTracker.Entries<OrchSettings>().ToList())
            entry.State = EntityState.Detached;

        log.WriteLine("  OrchSettings: settings carried over");
    }

    private static async Task CopyTableAsync<T>(
        AppDbContext source,
        AppDbContext target,
        Func<AppDbContext, DbSet<T>> set,
        bool targetIsSqlServer,
        TextWriter log) where T : class
    {
        var tableName = target.Model.FindEntityType(typeof(T))?.GetTableName() ?? typeof(T).Name;

        List<T> rows;
        try
        {
            rows = await set(source).AsNoTracking().ToListAsync();
        }
        catch (Exception ex)
        {
            // A table the old database never had (upgrading from a build that predates it).
            log.WriteLine($"  {tableName}: could not be read from the old database, skipped ({ex.Message})");
            return;
        }

        if (rows.Count == 0)
        {
            log.WriteLine($"  {tableName}: nothing to copy");
            return;
        }

        var targetSet = set(target);
        if (await targetSet.AnyAsync())
        {
            log.WriteLine($"  {tableName}: already has rows in the new database, left untouched");
            return;
        }

        targetSet.AddRange(rows);

        if (targetIsSqlServer)
        {
            // IDENTITY_INSERT is per-connection, so the connection has to stay open across the
            // SET / INSERT / SET sequence instead of EF opening and closing it per command.
            await target.Database.OpenConnectionAsync();
            try
            {
                // EF1002: the table name comes from the EF model, not from user input.
#pragma warning disable EF1002
                await target.Database.ExecuteSqlRawAsync($"SET IDENTITY_INSERT [{tableName}] ON");
                await target.SaveChangesAsync();
                await target.Database.ExecuteSqlRawAsync($"SET IDENTITY_INSERT [{tableName}] OFF");
                // Explicit inserts do advance the identity value, but a RESEED makes that certain
                // even if the rows arrived out of order or the table was partially populated.
                await target.Database.ExecuteSqlRawAsync($"DBCC CHECKIDENT ('[{tableName}]', RESEED)");
#pragma warning restore EF1002
            }
            finally
            {
                await target.Database.CloseConnectionAsync();
            }
        }
        else
        {
            await target.SaveChangesAsync();
        }

        // Detach so a later table's SaveChanges doesn't re-send these.
        foreach (var entry in target.ChangeTracker.Entries<T>().ToList())
            entry.State = EntityState.Detached;

        log.WriteLine($"  {tableName}: copied {rows.Count} row(s)");
    }
}
