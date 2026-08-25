using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using SuperTrigger.Web.Data;
using SuperTrigger.Web.Data.Entities;

namespace SuperTrigger.Web.Services.Database;

/// <summary>
/// Creates and upgrades the SQL Server schema straight from the EF Core model, instead of from a
/// second set of generated migrations.
/// </summary>
/// <remarks>
/// The SQLite side of this product has always been upgraded with idempotent DDL
/// (<see cref="DatabaseMigrator"/>'s bootstrap statements) rather than with pure EF migrations, and
/// its migration history is SQLite-specific. Rather than maintain a parallel, hand-kept T-SQL
/// script that has to be extended for every new entity property, this reconciles the live schema
/// against the model on every install/upgrade:
/// <list type="bullet">
///   <item>missing table → CREATE TABLE built from the model (types from the SQL Server type mappings)</item>
///   <item>existing table, missing column → ALTER TABLE ADD, with a DEFAULT so existing rows stay valid</item>
///   <item>missing unique index → CREATE UNIQUE INDEX</item>
/// </list>
/// Column DEFAULTs come from a default-constructed entity instance, so a column added later gets
/// the same value a new row would have had (e.g. MailServerVersion → 'Exchange2013_SP1'), which is
/// also what makes the single-column OrchSettings seed insert below produce a correct row.
/// Nothing here ever drops or retypes anything — a property removed from the model just leaves a
/// now-unused column behind.
/// </remarks>
internal static class SqlServerSchemaBuilder
{
    /// <summary>
    /// Creates the target database if it doesn't exist yet. Requires a login with CREATE DATABASE
    /// permission (or dbcreator); if the database already exists no permission beyond connecting
    /// to master is needed, and a failure to even reach master is reported as a clear error.
    /// </summary>
    public static async Task EnsureDatabaseExistsAsync(DbConfig config, TextWriter log)
    {
        var builder = new SqlConnectionStringBuilder(config.BuildConnectionString());
        var databaseName = builder.InitialCatalog;
        if (string.IsNullOrWhiteSpace(databaseName))
            throw new InvalidOperationException("The SQL Server connection string does not name a database (Initial Catalog).");

        builder.InitialCatalog = "master";

        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();

        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT CASE WHEN DB_ID(@name) IS NULL THEN 0 ELSE 1 END";
        exists.Parameters.AddWithValue("@name", databaseName);
        if ((int)(await exists.ExecuteScalarAsync())! == 1) return;

        log.WriteLine($"Database '{databaseName}' does not exist yet — creating it.");
        await using var create = connection.CreateCommand();
        // QUOTENAME + sp_executesql: CREATE DATABASE can't take the name as a parameter.
        create.CommandText = """
            DECLARE @sql nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@name);
            EXEC sp_executesql @sql;
            """;
        create.Parameters.AddWithValue("@name", databaseName);
        create.CommandTimeout = 120;
        await create.ExecuteNonQueryAsync();
        log.WriteLine($"Database '{databaseName}' created.");
    }

    public static async Task EnsureSchemaAsync(AppDbContext db, TextWriter log)
    {
        var existingTables = await GetExistingTablesAsync(db);

        foreach (var entityType in db.Model.GetEntityTypes())
        {
            var table = entityType.GetTableName();
            if (table == null) continue;
            var schema = entityType.GetSchema() ?? "dbo";

            if (!existingTables.Contains(Key(schema, table)))
            {
                log.WriteLine($"Creating table [{schema}].[{table}]");
                await db.Database.ExecuteSqlRawAsync(BuildCreateTable(entityType, schema, table));
            }
            else
            {
                var existingColumns = await GetExistingColumnsAsync(db, schema, table);
                foreach (var property in entityType.GetProperties())
                {
                    var column = property.GetColumnName();
                    if (column == null || existingColumns.Contains(column)) continue;

                    log.WriteLine($"Adding column [{schema}].[{table}].[{column}]");
                    // EF1002: this is DDL generated from the EF model, not from user input.
#pragma warning disable EF1002
                    await db.Database.ExecuteSqlRawAsync(
                        $"ALTER TABLE {Qualify(schema, table)} ADD {BuildColumnDefinition(entityType, property, forAlter: true)}");
#pragma warning restore EF1002
                }
            }

            foreach (var index in entityType.GetIndexes())
                await db.Database.ExecuteSqlRawAsync(BuildCreateIndex(index, schema, table));
        }

        await SeedOrchSettingsAsync(db, log);
    }

    /// <summary>
    /// The single OrchSettings row the app expects to always exist. Seeded by
    /// <c>HasData</c> on the SQLite side (through the initial migration), which never runs here
    /// because this path doesn't use migrations — every other column relies on the DEFAULT
    /// constraints emitted by <see cref="BuildCreateTable"/>.
    /// </summary>
    private static async Task SeedOrchSettingsAsync(AppDbContext db, TextWriter log)
    {
        var entityType = db.Model.FindEntityType(typeof(OrchSettings));
        var table = entityType?.GetTableName();
        if (entityType == null || table == null) return;
        var qualified = Qualify(entityType.GetSchema() ?? "dbo", table);

        var count = await db.OrchSettings.CountAsync();
        if (count > 0) return;

        log.WriteLine("Seeding the default settings row.");
        // EF1002: the table name comes from the EF model, not from user input.
#pragma warning disable EF1002
        await db.Database.ExecuteSqlRawAsync($"""
            SET IDENTITY_INSERT {qualified} ON;
            INSERT INTO {qualified} ([Id]) VALUES (1);
            SET IDENTITY_INSERT {qualified} OFF;
            """);
#pragma warning restore EF1002
    }

    // ------------------------------------------------------------------ DDL

    private static string BuildCreateTable(IEntityType entityType, string schema, string table)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"CREATE TABLE {Qualify(schema, table)} (");

        var lines = entityType.GetProperties()
            .Where(p => p.GetColumnName() != null)
            .Select(p => "    " + BuildColumnDefinition(entityType, p, forAlter: false))
            .ToList();

        var primaryKey = entityType.FindPrimaryKey();
        if (primaryKey != null)
        {
            var columns = string.Join(", ", primaryKey.Properties.Select(p => Bracket(p.GetColumnName()!)));
            lines.Add($"    CONSTRAINT {Bracket(primaryKey.GetName() ?? "PK_" + table)} PRIMARY KEY ({columns})");
        }

        sb.AppendLine(string.Join(",\r\n", lines));
        sb.AppendLine(")");
        return sb.ToString();
    }

    private static string BuildColumnDefinition(IEntityType entityType, IProperty property, bool forAlter)
    {
        var column = property.GetColumnName()!;
        var storeType = property.GetColumnType();
        if (string.IsNullOrEmpty(storeType))
            throw new InvalidOperationException(
                $"Could not determine a SQL Server column type for {entityType.ClrType.Name}.{property.Name}.");

        var sb = new StringBuilder($"{Bracket(column)} {storeType}");

        var isIdentity = property.IsPrimaryKey()
                         && property.ValueGenerated == ValueGenerated.OnAdd
                         && IsIntegerType(property.ClrType);
        if (isIdentity) sb.Append(" IDENTITY(1,1)");

        sb.Append(property.IsNullable ? " NULL" : " NOT NULL");

        // A NOT NULL column added to a table that already has rows must carry a DEFAULT, and giving
        // new tables the same defaults keeps the two paths (and the OrchSettings seed) consistent.
        if (!isIdentity)
        {
            var literal = DefaultLiteral(entityType, property);
            if (literal != null)
            {
                // Named on ALTER so a failed/retried install can't collide with an auto-generated name.
                if (forAlter) sb.Append($" CONSTRAINT {Bracket($"DF_{entityType.GetTableName()}_{column}")}");
                sb.Append($" DEFAULT ({literal})");
            }
        }

        return sb.ToString();
    }

    private static string BuildCreateIndex(IIndex index, string schema, string table)
    {
        var name = index.GetDatabaseName() ?? "IX_" + table;
        var columns = string.Join(", ", index.Properties.Select(p => Bracket(p.GetColumnName()!)));
        var unique = index.IsUnique ? "UNIQUE " : "";
        return $"""
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{name}' AND object_id = OBJECT_ID(N'{Qualify(schema, table)}'))
                CREATE {unique}INDEX {Bracket(name)} ON {Qualify(schema, table)} ({columns})
            """;
    }

    /// <summary>
    /// The T-SQL literal for this column's DEFAULT, taken from a default-constructed entity, or
    /// null when the column shouldn't get one.
    /// </summary>
    private static string? DefaultLiteral(IEntityType entityType, IProperty property)
    {
        // Encrypted columns go through a value converter: the CLR default ("") would have to be run
        // through DPAPI to be a valid stored value, which is pointless for a default. An empty
        // string is what the SQLite schema uses for these too, and FieldEncryption.Decrypt returns
        // unrecognised values unchanged, so it reads back as "".
        var hasConverter = property.GetValueConverter() != null;

        object? value = null;
        if (!hasConverter && property.PropertyInfo != null)
        {
            var instance = DefaultInstance(entityType.ClrType);
            if (instance != null)
            {
                try { value = property.PropertyInfo.GetValue(instance); }
                catch { /* fall through to the type default below */ }
            }
        }

        var clrType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;

        if (value == null)
        {
            if (property.IsNullable) return null;
            // Converter-backed or unreadable: fall back to the plain type default.
            value = clrType == typeof(string) ? "" : Activator.CreateInstance(clrType);
            if (value == null) return null;
        }

        if (clrType.IsEnum)
            return Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);

        return value switch
        {
            string s => "N'" + s.Replace("'", "''") + "'",
            bool b => b ? "1" : "0",
            byte or sbyte or short or ushort or int or uint or long or ulong =>
                Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            float or double or decimal =>
                Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            // DateTime.Now-style defaults become "now on the server"; DateTime.MinValue stays literal.
            DateTime dt => dt == default ? "'0001-01-01T00:00:00'" : "SYSDATETIME()",
            Guid g => $"'{g}'",
            _ => null
        };
    }

    private static readonly Dictionary<Type, object?> DefaultInstances = new();

    private static object? DefaultInstance(Type clrType)
    {
        if (DefaultInstances.TryGetValue(clrType, out var cached)) return cached;
        object? instance = null;
        try { instance = Activator.CreateInstance(clrType); }
        catch { /* no parameterless constructor — defaults fall back to type defaults */ }
        DefaultInstances[clrType] = instance;
        return instance;
    }

    // -------------------------------------------------------------- metadata

    private static async Task<HashSet<string>> GetExistingTablesAsync(AppDbContext db)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var connection = db.Database.GetDbConnection();
        var wasClosed = connection.State != System.Data.ConnectionState.Open;
        if (wasClosed) await db.Database.OpenConnectionAsync();
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT TABLE_SCHEMA, TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE'";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                result.Add(Key(reader.GetString(0), reader.GetString(1)));
        }
        finally
        {
            if (wasClosed) await db.Database.CloseConnectionAsync();
        }
        return result;
    }

    private static async Task<HashSet<string>> GetExistingColumnsAsync(AppDbContext db, string schema, string table)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var connection = db.Database.GetDbConnection();
        var wasClosed = connection.State != System.Data.ConnectionState.Open;
        if (wasClosed) await db.Database.OpenConnectionAsync();
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = @s AND TABLE_NAME = @t";
            var s = cmd.CreateParameter(); s.ParameterName = "@s"; s.Value = schema; cmd.Parameters.Add(s);
            var t = cmd.CreateParameter(); t.ParameterName = "@t"; t.Value = table; cmd.Parameters.Add(t);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                result.Add(reader.GetString(0));
        }
        finally
        {
            if (wasClosed) await db.Database.CloseConnectionAsync();
        }
        return result;
    }

    private static bool IsIntegerType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte);
    }

    private static string Key(string schema, string table) => schema + "." + table;
    private static string Bracket(string identifier) => "[" + identifier.Replace("]", "]]") + "]";
    private static string Qualify(string schema, string table) => Bracket(schema) + "." + Bracket(table);
}
