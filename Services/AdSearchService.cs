using System.DirectoryServices.AccountManagement;

namespace SuperTrigger.Web.Services;

public record AdEntry(string Identifier, string DisplayName, bool IsGroup);

public class AdSearchService(ILogger<AdSearchService> logger, AppSettingsService settingsService)
{
    public string? LastError { get; private set; }

    public List<AdEntry> Search(string query)
    {
        LastError = null;
        var results = new List<AdEntry>();
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2) return results;

        try
        {
            var settings = settingsService.GetAsync().GetAwaiter().GetResult();
            var hasCredentials = !string.IsNullOrEmpty(settings.AdUsername) && !string.IsNullOrEmpty(settings.AdPassword);
            using var ctx = hasCredentials
                ? new PrincipalContext(ContextType.Domain, null, settings.AdUsername, settings.AdPassword)
                : new PrincipalContext(ContextType.Domain);

            // Search users by SamAccountName prefix (indexed) and DisplayName prefix
            using var userSearcher = new PrincipalSearcher(
                new UserPrincipal(ctx) { SamAccountName = $"{query}*" });
            foreach (Principal p in userSearcher.FindAll())
            {
                if (p is UserPrincipal u)
                    results.Add(new AdEntry(u.SamAccountName ?? u.Name ?? "", u.DisplayName ?? u.SamAccountName ?? "", false));
                p.Dispose();
            }

            // Also search users by DisplayName prefix if we have room
            if (results.Count < 20)
            {
                using var displaySearcher = new PrincipalSearcher(
                    new UserPrincipal(ctx) { DisplayName = $"{query}*" });
                foreach (Principal p in displaySearcher.FindAll())
                {
                    if (p is UserPrincipal u && !results.Any(r => r.Identifier == (u.SamAccountName ?? "")))
                        results.Add(new AdEntry(u.SamAccountName ?? u.Name ?? "", u.DisplayName ?? u.SamAccountName ?? "", false));
                    p.Dispose();
                }
            }

            // Search groups
            using var groupSearcher = new PrincipalSearcher(
                new GroupPrincipal(ctx) { Name = $"{query}*" });
            foreach (Principal p in groupSearcher.FindAll())
            {
                results.Add(new AdEntry(p.Name ?? "", p.DisplayName ?? p.Name ?? "", true));
                p.Dispose();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AD search failed for query '{Query}'", query);
            LastError = ex.Message;
        }

        return results.Take(20).ToList();
    }
}
