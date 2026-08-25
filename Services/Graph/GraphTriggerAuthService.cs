using SuperTrigger.Web.Data.Entities;
using SuperTrigger.Web.Services.Auth;

namespace SuperTrigger.Web.Services.Graph;

// Resolves a Graph access token / mailbox UPN for a MailTrigger, regardless of which Graph
// MailAuthMode it uses (OAuth2Interactive, AppIdGlobal, AppIdPerTrigger, GraphUsernamePassword).
// Shared by the webhook subscription service and the polling service so both can reach the same
// mailbox the same way.
public class GraphTriggerAuthService(
    GraphMailService graphService,
    IServiceScopeFactory scopeFactory,
    ILogger<GraphTriggerAuthService> logger)
{
    public async Task<string?> ResolveAccessTokenAsync(MailTrigger trigger, OrchSettings settings)
    {
        switch (trigger.MailAuthMode)
        {
            case "OAuth2Interactive":
            {
                using var scope = scopeFactory.CreateScope();
                var oauthService = scope.ServiceProvider.GetRequiredService<MailOAuthService>();
                return await oauthService.GetValidAccessTokenAsync(trigger);
            }

            case "GraphUsernamePassword":
                return await ResolveUsernamePasswordTokenAsync(trigger, settings);

            case "AppIdGlobal":
                return await ResolveGlobalTokenAsync(trigger, settings);

            case "AppIdPerTrigger":
                if (string.IsNullOrWhiteSpace(trigger.AzureTenantId) || string.IsNullOrWhiteSpace(trigger.GraphClientId) ||
                    string.IsNullOrWhiteSpace(trigger.GraphClientSecret))
                {
                    logger.LogWarning("Trigger [{Name}] ({Mode}) is missing Graph credentials", trigger.TriggerName, trigger.MailAuthMode);
                    return null;
                }
                return await graphService.GetAccessTokenAsync(trigger.AzureTenantId, trigger.GraphClientId, trigger.GraphClientSecret);

            default:
                return null;
        }
    }

    private async Task<string?> ResolveUsernamePasswordTokenAsync(MailTrigger trigger, OrchSettings settings)
    {
        var tenantId = !string.IsNullOrWhiteSpace(trigger.AzureTenantId) ? trigger.AzureTenantId : settings.GraphTenantId;
        var clientId = !string.IsNullOrWhiteSpace(trigger.GraphClientId) ? trigger.GraphClientId : settings.GraphClientId;
        var clientSecret = !string.IsNullOrWhiteSpace(trigger.GraphClientSecret) ? trigger.GraphClientSecret : settings.GraphClientSecret;

        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(trigger.UserName) || string.IsNullOrWhiteSpace(trigger.Password))
        {
            logger.LogWarning(
                "Trigger [{Name}] (GraphUsernamePassword) is missing Tenant/Client ID (per-trigger or global) or mailbox Username/Password",
                trigger.TriggerName);
            return null;
        }

        return await graphService.GetAccessTokenByPasswordAsync(tenantId, clientId, clientSecret, trigger.UserName, trigger.Password);
    }

    private async Task<string?> ResolveGlobalTokenAsync(MailTrigger trigger, OrchSettings settings)
    {
        switch (settings.GlobalMailAuthMode)
        {
            case "OAuth2Interactive":
            {
                using var scope = scopeFactory.CreateScope();
                var oauthService = scope.ServiceProvider.GetRequiredService<MailOAuthService>();
                var token = await oauthService.GetValidGlobalAccessTokenAsync();
                if (token == null)
                    logger.LogWarning(
                        "Trigger [{Name}] (AppIdGlobal) — no valid global Interactive Sign-in token. Sign in via Settings → Mail.",
                        trigger.TriggerName);
                return token;
            }

            case "GraphUsernamePassword":
                if (string.IsNullOrWhiteSpace(settings.GraphTenantId) || string.IsNullOrWhiteSpace(settings.GraphClientId) ||
                    string.IsNullOrWhiteSpace(settings.GraphUsername) || string.IsNullOrWhiteSpace(settings.GraphPassword))
                {
                    logger.LogWarning(
                        "Trigger [{Name}] (AppIdGlobal) — global Username & Password mode is missing Tenant ID, Client ID, Username or Password in Settings.",
                        trigger.TriggerName);
                    return null;
                }
                return await graphService.GetAccessTokenByPasswordAsync(
                    settings.GraphTenantId, settings.GraphClientId, settings.GraphClientSecret,
                    settings.GraphUsername, settings.GraphPassword);

            case "AppIdGlobal":
                if (string.IsNullOrWhiteSpace(settings.GraphTenantId) || string.IsNullOrWhiteSpace(settings.GraphClientId) ||
                    string.IsNullOrWhiteSpace(settings.GraphClientSecret))
                {
                    logger.LogWarning(
                        "Trigger [{Name}] (AppIdGlobal) — global App ID + Secret mode is missing Tenant ID, Client ID or Client Secret in Settings.",
                        trigger.TriggerName);
                    return null;
                }
                return await graphService.GetAccessTokenAsync(settings.GraphTenantId, settings.GraphClientId, settings.GraphClientSecret);

            default: // "Interactive" (EWS) or "AppIdPerTrigger" — no Graph credentials configured globally
                logger.LogWarning(
                    "Trigger [{Name}] (AppIdGlobal) — the Global Mail Authentication Mode in Settings is set to \"{Mode}\", which has no Microsoft Graph credentials. " +
                    "Choose a Graph-based global mode in Settings → Mail, or change this trigger's Authentication Mode.",
                    trigger.TriggerName, settings.GlobalMailAuthMode);
                return null;
        }
    }

    public static string ResolveMailboxUpnForTrigger(MailTrigger trigger, OrchSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(trigger.SharedMailBox))
            return trigger.SharedMailBox;

        if (trigger.MailAuthMode == "AppIdGlobal" && !string.IsNullOrWhiteSpace(settings.GlobalSharedMailBox))
            return settings.GlobalSharedMailBox;

        if (trigger.MailAuthMode == "OAuth2Interactive" && !string.IsNullOrWhiteSpace(trigger.OAuthUserUpn))
            return trigger.OAuthUserUpn;

        if (trigger.MailAuthMode == "GraphUsernamePassword" && !string.IsNullOrWhiteSpace(trigger.UserName))
            return trigger.UserName;

        // EWS treats an empty mailbox as "use the authenticated/SSO mailbox" — matches a plain
        // Interactive trigger's existing behavior with a blank SharedMailBox.
        if (trigger.EffectiveMailAuthMode(settings) == "Interactive")
            return "";

        throw new InvalidOperationException(
            $"Trigger [{trigger.TriggerName}]: Shared Mailbox (UPN) is required for Graph API mode");
    }
}
