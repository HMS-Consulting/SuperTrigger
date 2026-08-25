using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SuperTrigger.Web.Data.Entities;
using SuperTrigger.Web.Services;

namespace SuperTrigger.Web.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<FileTrigger> FileTriggers => Set<FileTrigger>();
    public DbSet<MailTrigger> MailTriggers => Set<MailTrigger>();
    public DbSet<LocalUser> LocalUsers => Set<LocalUser>();
    public DbSet<AdPrincipal> AdPrincipals => Set<AdPrincipal>();
    public DbSet<QueueItemLog> QueueItemLogs => Set<QueueItemLog>();
    public DbSet<OrchSettings> OrchSettings => Set<OrchSettings>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<OrchSettings>().HasData(new OrchSettings { Id = 1 });

        // The length matters on SQL Server only: an unbounded string maps to nvarchar(max), which
        // cannot be an index key column. SQLite ignores it (its TEXT columns are unbounded either
        // way), so existing SQLite databases are unaffected.
        mb.Entity<MailTrigger>().Property(e => e.TriggerName).HasMaxLength(256);
        mb.Entity<FileTrigger>().Property(e => e.TriggerName).HasMaxLength(256);
        mb.Entity<MailTrigger>().HasIndex(e => e.TriggerName).IsUnique();
        mb.Entity<FileTrigger>().HasIndex(e => e.TriggerName).IsUnique();

        // DPAPI-backed converters — transparent encrypt on write, decrypt on read.
        var enc = new ValueConverter<string, string>(
            v => FieldEncryption.Encrypt(v),
            v => FieldEncryption.Decrypt(v));

        // Nullable variant for string? fields — EF Core requires matching nullability.
        var encNull = new ValueConverter<string?, string?>(
            v => v == null ? null : FieldEncryption.Encrypt(v),
            v => v == null ? null : FieldEncryption.Decrypt(v));

        mb.Entity<OrchSettings>(e =>
        {
            e.Property(x => x.Password).HasConversion(enc);
            e.Property(x => x.AdPassword).HasConversion(enc);
            e.Property(x => x.GraphClientSecret).HasConversion(enc);
            e.Property(x => x.OAuthGlobalAccessToken).HasConversion(encNull);
            e.Property(x => x.OAuthGlobalRefreshToken).HasConversion(encNull);
        });

        mb.Entity<MailTrigger>(e =>
        {
            e.Property(x => x.Password).HasConversion(enc);
            e.Property(x => x.GraphClientSecret).HasConversion(enc);
            e.Property(x => x.OAuthAccessToken).HasConversion(encNull);
            e.Property(x => x.OAuthRefreshToken).HasConversion(encNull);
        });

        mb.Entity<FileTrigger>(e =>
        {
            e.Property(x => x.WatcherPassword).HasConversion(enc);
        });
    }
}
