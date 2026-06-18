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

            // If a subscription already exists, try to renew it instead of creating a duplicate
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

            if (!MatchesFilters(message, trigger))
            {
                logger.LogDebug("Message {MessageId} filtered out for trigger [{Name}]", messageId, trigger.TriggerName);
                return;
            }

            var payload = BuildPayload(message, trigger);
            var folderPath = BuildFolderPath(settings, trigger);

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

    private static bool MatchesFilters(JObject message, MailTrigger trigger)
    {
        var subject = message["subject"]?.ToString() ?? "";
        var fromEmail = message["from"]?["emailAddress"]?["address"]?.ToString() ?? "";
        var bodyContent = message["body"]?["content"]?.ToString() ?? "";

        if (!string.IsNullOrEmpty(trigger.SubjectFilterContains) &&
            !subject.Contains(trigger.SubjectFilterContains, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrEmpty(trigger.BodyFilterContains) &&
            !bodyContent.Contains(trigger.BodyFilterContains, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrEmpty(trigger.From))
        {
            var fromFilters = trigger.From.Split(';',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fromFilters.Length > 0 &&
                !fromFilters.Any(f => fromEmail.Contains(f, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        return true;
    }

    private static string? ToLocal(string? graphDateTime)
    {
        if (string.IsNullOrEmpty(graphDateTime)) return graphDateTime;
        try
        {
            var dt = DateTime.Parse(graphDateTime, null,
                System.Globalization.DateTimeStyles.RoundtripKind);
            if (dt.Kind == DateTimeKind.Utc)
                dt = dt.ToLocalTime();
            return dt.ToString("dd/MM/yyyy HH:mm:ss");
        }
        catch { return graphDateTime; }
    }

    private static JObject BuildPayload(JObject message, MailTrigger trigger)
    {
        var subject = message["subject"]?.ToString() ?? "";
        var from = message["from"]?["emailAddress"]?["address"]?.ToString() ?? "";
        var received = ToLocal(message["receivedDateTime"]?.ToString()) ?? "";

        var raw = $"{received}_{subject}_{from}";
        var reference = raw[..Math.Min(128, raw.Length)];

        var meetingType = message["meetingMessageType"]?.ToString() ?? "none";
        var isMeetingRequest = meetingType == "meetingRequest";
        var isMeetingResponse = meetingType is "meetingAccepted" or "meetingTentativelyAccepted" or "meetingDeclined";
        var isMeetingCancellation = meetingType == "meetingCancelled";

        var content = new JObject
        {
            ["TriggerName"] = trigger.TriggerName,
            ["MailId"] = message["id"]?.ToString(),
            ["MailInternetUID"] = message["internetMessageId"]?.ToString(),
            ["MailFolder"] = trigger.MailFolder,
            ["MailSharedBox"] = trigger.SharedMailBox,
            ["Sender"] = from,
            ["Subject"] = subject,
            ["DateTimeReceived"] = received,
            ["HasAttachments"] = message["hasAttachments"]?.ToString(),
            ["IsMeetingRequest"] = isMeetingRequest.ToString(),
            ["IsMeetingResponse"] = isMeetingResponse.ToString(),
            ["IsMeetingCancellation"] = isMeetingCancellation.ToString()
        };

        if (isMeetingRequest || isMeetingResponse || isMeetingCancellation)
        {
            var evt = message["event"] as JObject;
            var appointmentId = evt?["iCalUId"]?.ToString() ?? evt?["id"]?.ToString();
            if (!string.IsNullOrEmpty(appointmentId))
                content["AppointmentId"] = appointmentId;

            if (isMeetingRequest)
            {
                content["MeetingRequestType"] = meetingType;
                content["MeetingStart"] = ToLocal(evt?["start"]?["dateTime"]?.ToString());
                content["MeetingEnd"] = ToLocal(evt?["end"]?["dateTime"]?.ToString());
                var location = evt?["location"]?["displayName"]?.ToString();
                if (!string.IsNullOrEmpty(location))
                    content["MeetingLocation"] = location;
                var evtType = evt?["type"]?.ToString() ?? "";
                var isRecurring = evtType is "seriesMaster" or "occurrence" or "exception";
                content["MeetingIsRecurring"] = isRecurring.ToString();
                if (isRecurring && evt?["recurrence"] != null)
                    content["MeetingRecurrence"] = evt["recurrence"]!.ToString(Newtonsoft.Json.Formatting.None);
            }
            else if (isMeetingResponse)
            {
                content["MeetingResponseType"] = meetingType;
            }
        }

        return new JObject
        {
            ["itemData"] = new JObject
            {
                ["Reference"] = reference,
                ["SpecificContent"] = content
            }
        };
    }

    private static string BuildFolderPath(OrchSettings settings, MailTrigger trigger)
    {
        var parts = new List<string> { settings.OrchestratorMainFolderName };
        if (!string.IsNullOrEmpty(trigger.DivisionName))
        {
            parts.Add(trigger.DivisionName);
            parts.Add(trigger.CompanyName);
        }
        parts.Add(trigger.BusinessDepartmentName);
        parts.Add(trigger.BusinessProcessName);
        return string.Join("/", parts);
    }

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
