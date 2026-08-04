using Microsoft.EntityFrameworkCore;
using SuperTrigger.Web.Data;

namespace SuperTrigger.Web.Services.Background;

// Periodically deletes QueueItemLogs and AuditLogs older than the configured
// OrchSettings.RetentionDays. Reloads the setting every cycle so changes made
// on the Settings page take effect without an app restart.
public class RetentionCleanupService(
    IDbContextFactory<AppDbContext> dbFactory,
    AppSettingsService settingsService,
    ILogger<RetentionCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("RetentionCleanupService starting");

        await CleanupAsync();

        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await CleanupAsync();
        }
        catch (OperationCanceledException) { }
    }

    private async Task CleanupAsync()
    {
        try
        {
            var settings = await settingsService.GetAsync();
            var cutoff = DateTime.Now.AddDays(-settings.RetentionDays);

            await using var db = await dbFactory.CreateDbContextAsync();

            var deletedLogs = await db.QueueItemLogs
                .Where(l => l.CreatedAt < cutoff)
                .ExecuteDeleteAsync();

            var deletedAuditLogs = await db.AuditLogs
                .Where(a => a.CreatedAt < cutoff)
                .ExecuteDeleteAsync();

            if (deletedLogs > 0 || deletedAuditLogs > 0)
                logger.LogInformation(
                    "Retention cleanup removed {LogCount} log(s) and {AuditCount} audit log(s) older than {Days} day(s)",
                    deletedLogs, deletedAuditLogs, settings.RetentionDays);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error running retention cleanup");
        }
    }
}
