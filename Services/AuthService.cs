using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using SuperTrigger.Web.Data;
using SuperTrigger.Web.Data.Entities;
using System.DirectoryServices.AccountManagement;

namespace SuperTrigger.Web.Services;

public class AuthService(IDbContextFactory<AppDbContext> dbFactory, ILogger<AuthService> logger, AppSettingsService settingsService)
{
    public async Task<bool> ValidateLocalAdminAsync(string username, string password)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var user = await db.LocalUsers.FirstOrDefaultAsync(u => u.Username == username);
            if (user == null) return false;
            return BCrypt.Net.BCrypt.Verify(password, user.PasswordHash);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error validating local admin");
            return false;
        }
    }

    public async Task<ClaimsPrincipal> CreateLocalAdminPrincipalAsync(string username)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, username),
            new(ClaimTypes.Role, AppRole.Admin.ToString()),
            new("AuthType", "Local")
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(identity);
    }

    // Validate AD credentials and return a principal if the user is in AdPrincipals
    public async Task<ClaimsPrincipal?> ValidateAdUserAsync(string username, string password)
    {
        try
        {
            var settings = await settingsService.GetAsync();
            var hasServiceAccount = !string.IsNullOrEmpty(settings.AdUsername) && !string.IsNullOrEmpty(settings.AdPassword);

            bool credentialsValid = await Task.Run(() =>
            {
                try
                {
                    using var ctx = hasServiceAccount
                        ? new PrincipalContext(ContextType.Domain, null, settings.AdUsername, settings.AdPassword)
                        : new PrincipalContext(ContextType.Domain);
                    return ctx.ValidateCredentials(username, password, ContextOptions.Negotiate);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "AD credential validation failed for {User}", username);
                    return false;
                }
            });

            if (!credentialsValid)
            {
                logger.LogInformation("AD login: invalid credentials for {User}", username);
                return null;
            }

            // Strip domain prefix if present (DOMAIN\user or user@domain)
            var samAccount = username.Contains('\\') ? username.Split('\\')[1]
                           : username.Contains('@') ? username.Split('@')[0]
                           : username;

            logger.LogInformation("AD login: credentials valid for {Sam}, checking AdPrincipals", samAccount);

            await using var db = await dbFactory.CreateDbContextAsync();

            var userEntry = await db.AdPrincipals
                .Where(p => p.Type == AdPrincipalType.User &&
                            p.Identifier.ToLower() == samAccount.ToLower())
                .FirstOrDefaultAsync();

            if (userEntry != null)
            {
                logger.LogInformation("AD login: direct match for {Sam} → role={Role}", samAccount, userEntry.Role);
                return BuildWindowsPrincipal(samAccount, samAccount, userEntry.Role);
            }

            // Check group membership
            var adSettings = await settingsService.GetAsync();
            var groups = await Task.Run(() => GetWindowsGroupMembership(samAccount, adSettings.AdUsername, adSettings.AdPassword, logger));
            logger.LogInformation("AD login: {Sam} groups: {Groups}", samAccount, string.Join(", ", groups.Take(10)));

            var groupEntries = await db.AdPrincipals.Where(p => p.Type == AdPrincipalType.Group).ToListAsync();
            var matchedGroup = groupEntries.FirstOrDefault(g =>
                groups.Any(ug => ug.Equals(g.Identifier, StringComparison.OrdinalIgnoreCase)));

            if (matchedGroup != null)
            {
                logger.LogInformation("AD login: group match '{Group}' for {Sam}", matchedGroup.Identifier, samAccount);
                return BuildWindowsPrincipal(samAccount, samAccount, matchedGroup.Role);
            }

            logger.LogWarning("AD login: credentials valid but {Sam} not in AdPrincipals", samAccount);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in ValidateAdUserAsync for {User}", username);
            return null;
        }
    }

    public async Task<ClaimsPrincipal?> CreateWindowsUserPrincipalAsync(string windowsIdentity)
    {
        try
        {
            // Extract sAMAccountName from DOMAIN\user or user@domain format
            var samAccount = windowsIdentity.Contains('\\')
                ? windowsIdentity.Split('\\')[1]
                : windowsIdentity.Split('@')[0];

            logger.LogInformation("Windows login attempt: identity={Identity} samAccount={Sam}", windowsIdentity, samAccount);

            await using var db = await dbFactory.CreateDbContextAsync();

            // Check direct user match
            var userEntry = await db.AdPrincipals
                .Where(p => p.Type == AdPrincipalType.User &&
                            p.Identifier.ToLower() == samAccount.ToLower())
                .FirstOrDefaultAsync();

            if (userEntry != null)
            {
                logger.LogInformation("Windows login: direct user match for {Sam} → role={Role}", samAccount, userEntry.Role);
                return BuildWindowsPrincipal(windowsIdentity, samAccount, userEntry.Role);
            }

            logger.LogInformation("Windows login: no direct match for {Sam}, checking group membership", samAccount);

            // Check group membership
            var adSettings = await settingsService.GetAsync();
            var groups = await Task.Run(() => GetWindowsGroupMembership(samAccount, adSettings.AdUsername, adSettings.AdPassword, logger));
            logger.LogInformation("Windows login: {Sam} is in {Count} groups: {Groups}", samAccount, groups.Count, string.Join(", ", groups.Take(10)));

            var groupEntries = await db.AdPrincipals
                .Where(p => p.Type == AdPrincipalType.Group)
                .ToListAsync();

            var matchedGroup = groupEntries.FirstOrDefault(g =>
                groups.Any(ug => ug.Equals(g.Identifier, StringComparison.OrdinalIgnoreCase)));

            if (matchedGroup != null)
            {
                logger.LogInformation("Windows login: group match '{Group}' for {Sam}", matchedGroup.Identifier, samAccount);
                return BuildWindowsPrincipal(windowsIdentity, samAccount, matchedGroup.Role);
            }

            logger.LogWarning("Windows login: no match found for {Sam} — not in AdPrincipals", samAccount);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating Windows user principal for {Identity}", windowsIdentity);
            return null;
        }
    }

    private static ClaimsPrincipal BuildWindowsPrincipal(string windowsIdentity, string samAccount, AppRole role)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, samAccount),
            new(ClaimTypes.WindowsAccountName, windowsIdentity),
            new(ClaimTypes.Role, role.ToString()),
            new("AuthType", "Windows")
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(identity);
    }

    private static List<string> GetWindowsGroupMembership(string samAccount, string adUsername, string adPassword, ILogger logger)
    {
        var groups = new List<string>();
        try
        {
            var hasCredentials = !string.IsNullOrEmpty(adUsername) && !string.IsNullOrEmpty(adPassword);
            using var ctx = hasCredentials
                ? new PrincipalContext(ContextType.Domain, null, adUsername, adPassword)
                : new PrincipalContext(ContextType.Domain);
            using var user = UserPrincipal.FindByIdentity(ctx, IdentityType.SamAccountName, samAccount);
            if (user == null) return groups;

            foreach (var group in user.GetAuthorizationGroups())
            {
                groups.Add(group.Name);
                group.Dispose();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "GetWindowsGroupMembership failed for {Sam}", samAccount);
        }
        return groups;
    }

    public async Task EnsureDefaultAdminExistsAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (!await db.LocalUsers.AnyAsync())
        {
            db.LocalUsers.Add(new LocalUser
            {
                Username = "admin",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("Admin123!")
            });
            await db.SaveChangesAsync();
            logger.LogInformation("Default admin user created (username=admin)");
        }
    }

    public async Task<bool> ChangePasswordAsync(string username, string newPassword)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var user = await db.LocalUsers.FirstOrDefaultAsync(u => u.Username == username);
            if (user == null) return false;
            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
            await db.SaveChangesAsync();
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error changing password for {Username}", username);
            return false;
        }
    }
}
