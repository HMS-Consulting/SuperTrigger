using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;
using SuperTrigger.Web.Data;
using SuperTrigger.Web.Data.Entities;
using SuperTrigger.Web.Services.Auth;
using SuperTrigger.Web.Services.Graph;

namespace SuperTrigger.Web.Services.Background;

public class GraphSubscriptionService(
    IDbContextFactory<AppDbContext> dbFactory,
    GraphMailService graphService,
    AppSettingsService settingsService,
    TriggerReloadChannel reloadChannel,
    OrchestratorService orchestrator,
    IServiceScopeFactory scopeFactory,
    ILogger<GraphSubscriptionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("GraphSubscriptionService starting");

        await SetupAllSubscriptionsAsync();

        _ = Task.Run(() => RenewalLoopAsync(stoppingToken), stoppingToken);

        await foreach (var _ in reloadChannel.Subscribe().ReadAllAsync(stoppingToken))
        {
            logger.LogInformation("GraphSubscriptionService: reloading subscriptions");
            await DeleteAllSubscriptionsAsync();
            await SetupAllSubscriptionsAsync();
        }
    }

    // ─── URL validation ──────────────────────────────────────────────────────

    private static bool IsWebhookCapableUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) &&
        !url.Contains("localhost", StringComparison.OrdinalIgnoreCase) &&
        !url.Contains("127.0.0.1");

    // ─── Subscription setup ──────────────────────────────────────────────────

    private async Task SetupAllSubscriptionsAsync()
    {
        try
        {
            var settings = await settingsService.GetAsync();

            await using var db = await dbFactory.CreateDbContextAsync();
            var triggers = await db.MailTriggers
                .Where(t => t.Active && t.MailAuthMode != "Interactive")
                .ToListAsync();

            if (!triggers.Any())
            {
                logger.LogInformation("No active Graph mail triggers found");
                return;
            }

            if (!IsWebhookCapableUrl(settings.PublicBaseUrl))
            {
                logger.LogWarning(
                    "PublicBaseUrl is not set or is localhost — cannot create Graph webhooks for {Count} trigger(s). " +
                    "Configure a public HTTPS URL (e.g. ngrok) in Settings → Microsoft Graph.",
                    triggers.Count);
                return;
            }

            var notificationUrl = $"{settings.PublicBaseUrl.TrimEnd('/')}/api/graph-notifications";
            logger.LogInformation("Setting up {Count} Graph subscriptions", triggers.Count);

            foreach (var trigger in triggers)
                await CreateSubscriptionForTriggerAsync(trigger, notificationUrl, settings, db);

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to set up Graph subscriptions");
        }
    }

    private async Task CreateSubscriptionForTriggerAsync(
        MailTrigger trigger, string notificationUrl, OrchSettings settings, AppDbContext db)
    {
        try
        {
            var token = await ResolveAccessTokenAsync(trigger, settings);
            if (token == null)
            {
                if (trigger.MailAuthMode == "OAuth2Interactive")
                    logger.LogWarning(
                        "Trigger [{Name}] has no valid OAuth token — cannot create subscription. Re-authenticate in the UI.",
                        trigger.TriggerName);
                else
                    logger.LogError(
                        "Trigger [{Name}] ({Mode}) is missing Graph credentials — skipping subscription",
                        trigger.TriggerName, trigger.MailAuthMode);
                return;
            }

            if (!string.IsNullOrEmpty(trigger.GraphSubscriptionId))
            {
                try
                {
                    var renewedExpiry = await graphService.RenewSubscriptionAsync(token, trigger.GraphSubscriptionId);
                    trigger.GraphSubscriptionExpiry = renewedExpiry;
                    db.MailTriggers.Update(trigger);
                    logger.LogInformation("Graph subscription renewed on startup for trigger [{Name}] → {Id}",
                        trigger.TriggerName, trigger.GraphSubscriptionId);
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Could not renew existing subscription {Id} for trigger [{Name}] — will create a new one",
                        trigger.GraphSubscriptionId, trigger.TriggerName);
                    trigger.GraphSubscriptionId = null;
                    trigger.GraphSubscriptionExpiry = null;
                }
            }

            var mailboxUpn = ResolveMailboxUpnForTrigger(trigger);
            var (subscriptionId, expiry) = await graphService.CreateSubscriptionAsync(
                token, mailboxUpn, trigger.MailFolder, notificationUrl, trigger.Id.ToString());

            trigger.GraphSubscriptionId = subscriptionId;
            trigger.GraphSubscriptionExpiry = expiry;
            db.MailTriggers.Update(trigger);

            logger.LogInformation("Graph subscription created for trigger [{Name}] → {Id}", trigger.TriggerName, subscriptionId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create Graph subscription for trigger [{Name}]", trigger.TriggerName);
        }
    }

    private async Task DeleteAllSubscriptionsAsync()
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var settings = await settingsService.GetAsync();
            var triggers = await db.MailTriggers
                .Where(t => t.GraphSubscriptionId != null)
                .ToListAsync();

            foreach (var trigger in triggers)
            {
                if (trigger.GraphSubscriptionId != null)
                {
                    try
                    {
                        var token = await ResolveAccessTokenAsync(trigger, settings);
                        if (token != null)
                            await graphService.DeleteSubscriptionAsync(token, trigger.GraphSubscriptionId);
                    }
                    catch { /* subscription may already be expired — clear DB regardless */ }
                }

                trigger.GraphSubscriptionId = null;
                trigger.GraphSubscriptionExpiry = null;
                db.MailTriggers.Update(trigger);
            }

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting Graph subscriptions on reload");
        }
    }

    private async Task RenewalLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                await RenewExpiringSubscriptionsAsync();
        }
        catch (OperationCanceledException) { }
    }

    private async Task RenewExpiringSubscriptionsAsync()
    {
        try
        {
            var settings = await settingsService.GetAsync();
            if (!IsWebhookCapableUrl(settings.PublicBaseUrl)) return;

            var threshold = DateTime.UtcNow.AddHours(24);

            await using var db = await dbFactory.CreateDbContextAsync();
            var triggers = await db.MailTriggers
                .Where(t => t.Active
                    && t.MailAuthMode != "Interactive"
                    && t.GraphSubscriptionId != null
                    && t.GraphSubscriptionExpiry < threshold)
                .ToListAsync();

            var failedIds = new List<int>();

            foreach (var trigger in triggers)
            {
                try
                {
                    var token = await ResolveAccessTokenAsync(trigger, settings);
                    if (token == null)
                    {
                        logger.LogWarning("Cannot renew subscription for trigger [{Name}] — no valid token", trigger.TriggerName);
                        continue;
                    }

                    var newExpiry = await graphService.RenewSubscriptionAsync(token, trigger.GraphSubscriptionId!);
                    trigger.GraphSubscriptionExpiry = newExpiry;
                    db.MailTriggers.Update(trigger);
                    logger.LogInformation("Renewed Graph subscription for trigger [{Name}]", trigger.TriggerName);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Renewal failed for trigger [{Name}] — will recreate", trigger.TriggerName);
                    trigger.GraphSubscriptionId = null;
                    trigger.GraphSubscriptionExpiry = null;
                    db.MailTriggers.Update(trigger);
                    failedIds.Add(trigger.Id);
                }
            }

            await db.SaveChangesAsync();

            if (failedIds.Any())
            {
                var notificationUrl = $"{settings.PublicBaseUrl.TrimEnd('/')}/api/graph-notifications";
                await using var db2 = await dbFactory.CreateDbContextAsync();
                foreach (var id in failedIds)
                {
                    var fresh = await db2.MailTriggers.FindAsync(id);
                    if (fresh != null)
                        await CreateSubscriptionForTriggerAsync(fresh, notificationUrl, settings, db2);
                }
                await db2.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in Graph subscription renewal loop");
        }
    }

    // ─── Notification processing (called from webhook endpoint) ─────────────

    public async Task ProcessNotificationBatchAsync(JArray notifications)
    {
        foreach (var item in notifications)
        {
            if (item is JObject n)
                await ProcessSingleNotificationAsync(n);
        }
    }

    private async Task ProcessSingleNotificationAsync(JObject notification)
    {
        try
        {
            var clientState = notification["clientState"]?.ToString();
            var resourceData = notification["resourceData"] as JObject;
            var messageId = resourceData?["id"]?.ToString();

            if (clientState == null || messageId == null)
            {
                logger.LogWarning("Graph notification missing clientState or messageId");
                return;
            }

            if (!int.TryParse(clientState, out var triggerId))
            {
                logger.LogWarning("Graph notification clientState is not an int: {State}", clientState);
                return;
            }

            await using var db = await dbFactory.CreateDbContextAsync();
            var trigger = await db.MailTriggers.FindAsync(triggerId);
            if (trigger == null || !trigger.Active || !trigger.UsesWebhook) return;

            var settings = await settingsService.GetAsync();
            var token = await ResolveAccessTokenAsync(trigger, settings);
            if (token == null)
            {
                logger.LogWarning("Cannot process notification for trigger [{Name}] — no valid token", trigger.TriggerName);
                return;
            }

            var mailboxUpn = ResolveMailboxUpnForTrigger(trigger);
            var message = await graphService.GetMessageAsync(token, mailboxUpn, messageId);

            if (message == null)
            {
                logger.LogWarning("Could not fetch message {MessageId} for trigger [{Name}]", messageId, trigger.TriggerName);
                return;
            }

            if (!TriggerHelpers.MatchesMailFilters(message, trigger))
            {
                logger.LogDebug("Message {MessageId} filtered out for trigger [{Name}]", messageId, trigger.TriggerName);
                return;
            }

            var payload = TriggerHelpers.BuildGraphMailPayload(message, trigger);
            var folderPath = TriggerHelpers.BuildFolderPath(settings, trigger);

            await orchestrator.AddQueueItemAsync(payload, trigger.QueueName, folderPath,
                Enum.TryParse<QueueItemPriority>(trigger.Priority, out var p) ? p : QueueItemPriority.Normal);

            await LogAsync(trigger, payload, true, null, db);
            logger.LogInformation("Queue item added from Graph webhook | trigger=[{Name}] queue={Queue}",
                trigger.TriggerName, trigger.QueueName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing Graph notification");
        }
    }

    // ─── Token / credential resolution ──────────────────────────────────────

    private async Task<string?> ResolveAccessTokenAsync(MailTrigger trigger, OrchSettings settings)
    {
        if (trigger.MailAuthMode == "OAuth2Interactive")
        {
            using var scope = scopeFactory.CreateScope();
            var oauthService = scope.ServiceProvider.GetRequiredService<MailOAuthService>();
            return await oauthService.GetValidAccessTokenAsync(trigger);
        }

        if (trigger.MailAuthMode == "AppIdGlobal" && string.IsNullOrWhiteSpace(settings.GraphClientSecret))
        {
            // No app-only (client_credentials) secret configured for the Global App ID -- fall back
            // to the delegated global sign-in ("Sign in with Microsoft" in Settings). Only reaches
            // mailboxes that signed-in user has delegated access to, not arbitrary tenant mailboxes.
            using var scope = scopeFactory.CreateScope();
            var oauthService = scope.ServiceProvider.GetRequiredService<MailOAuthService>();
            var token = await oauthService.GetValidGlobalAccessTokenAsync();
            if (token == null)
                logger.LogWarning("Trigger [{Name}] (AppIdGlobal) has no Client Secret and no valid global sign-in — configure one in Settings.", trigger.TriggerName);
            return token;
        }

        var (tenantId, clientId, clientSecret) = ResolveAppCredentials(trigger, settings);
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            logger.LogWarning("Trigger [{Name}] ({Mode}) is missing Graph credentials", trigger.TriggerName, trigger.MailAuthMode);
            return null;
        }

        return await graphService.GetAccessTokenAsync(tenantId, clientId, clientSecret);
    }

    private static (string tenantId, string clientId, string clientSecret) ResolveAppCredentials(
        MailTrigger trigger, OrchSettings settings) =>
        trigger.MailAuthMode switch
        {
            "AppIdGlobal" => (settings.GraphTenantId, settings.GraphClientId, settings.GraphClientSecret),
            "AppIdPerTrigger" => (trigger.AzureTenantId, trigger.GraphClientId, trigger.GraphClientSecret),
            _ => ("", "", "")
        };

    private static string ResolveMailboxUpnForTrigger(MailTrigger trigger)
    {
        if (!string.IsNullOrWhiteSpace(trigger.SharedMailBox))
            return trigger.SharedMailBox;

        if (trigger.MailAuthMode == "OAuth2Interactive" && !string.IsNullOrWhiteSpace(trigger.OAuthUserUpn))
            return trigger.OAuthUserUpn;

        throw new InvalidOperationException(
            $"Trigger [{trigger.TriggerName}]: Shared Mailbox (UPN) is required for Graph API mode");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private async Task LogAsync(MailTrigger trigger, JObject payload, bool success, string? error, AppDbContext db)
    {
        try
        {
            db.QueueItemLogs.Add(new QueueItemLog
            {
                TriggerName = trigger.TriggerName,
                TriggerType = TriggerType.Mail,
                QueueName = trigger.QueueName,
                Reference = payload["itemData"]?["Reference"]?.ToString() ?? "",
                Payload = payload.ToString(),
                Success = success,
                ErrorMessage = error
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write queue item log");
        }
    }
}
