using Microsoft.EntityFrameworkCore;
using SuperTrigger.Web.Data.Entities;

namespace SuperTrigger.Web.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<FileTrigger> FileTriggers => Set<FileTrigger>();
    public DbSet<MailTrigger> MailTriggers => Set<MailTrigger>();
    public DbSet<LocalUser> LocalUsers => Set<LocalUser>();
    public DbSet<AdPrincipal> AdPrincipals => Set<AdPrincipal>();
    public DbSet<QueueItemLog> QueueItemLogs => Set<QueueItemLog>();
    public DbSet<OrchSettings> OrchSettings => Set<OrchSettings>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<OrchSettings>().HasData(new OrchSettings { Id = 1 });
        mb.Entity<MailTrigger>().HasIndex(e => e.TriggerName).IsUnique();
        mb.Entity<FileTrigger>().HasIndex(e => e.TriggerName).IsUnique();
    }
}
