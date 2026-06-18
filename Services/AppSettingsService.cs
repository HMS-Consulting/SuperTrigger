using Microsoft.EntityFrameworkCore;
using SuperTrigger.Web.Data;
using SuperTrigger.Web.Data.Entities;

namespace SuperTrigger.Web.Services;

public class AppSettingsService(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<OrchSettings> GetAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.OrchSettings.FirstAsync();
    }

    public async Task SaveAsync(OrchSettings settings)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.OrchSettings.Update(settings);
        await db.SaveChangesAsync();
    }
}
