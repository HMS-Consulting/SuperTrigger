using Microsoft.EntityFrameworkCore;
using SuperTrigger.Web.Data;
using SuperTrigger.Web.Data.Entities;

namespace SuperTrigger.Web.Services;

public class AuditLogService(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task LogAsync(string? username, string action, string entityType, string entityName, string details = "")
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.AuditLogs.Add(new AuditLog
        {
            Username = string.IsNullOrWhiteSpace(username) ? "unknown" : username,
            Action = action,
            EntityType = entityType,
            EntityName = entityName,
            Details = details
        });
        await db.SaveChangesAsync();
    }
}
