using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Client;
using Newtonsoft.Json.Linq;
using SuperTrigger.Web.Data;
using SuperTrigger.Web.Data.Entities;
using SuperTrigger.Web.Services;
using SuperTrigger.Web.Services.Auth;
using SuperTrigger.Web.Services.Graph;
using System.Net;
using System.Net.Mail;
using System.Security;

namespace SuperTrigger.Web.Services.Background;

public class MailPollingService(
    IDbContextFactory<AppDbContext> dbFactory,
    OrchestratorService orchestrator,
    AppSettingsService settingsService,
    TriggerReloadChannel reloadChannel,
    IServiceScopeFactory scopeFactory,
    GraphMailService graphMailService,
    ILogger<MailPollingService> logger) : BackgroundService
{
    private readonly List<(Task Task, CancellationTokenSource Cts)> _pollingTasks = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("MailPollingService starting");
        await StartPollingTasksAsync();

        await foreach (var _ in reloadChannel.Subscribe().ReadAllAsync(stoppingToken))
        {
            logger.LogInformation("Reloading mail polling tasks...");
            await StopPollingTasksAsync();
            await StartPollingTasksAsync();
        }
    }

    private async Task StartPollingTasksAsync()
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var triggers = await db.MailTriggers
                .Where(t => t.Active && t.MailAuthMode == "Interactive")
                .ToListAsync();
            logger.LogInformation("Starting {Count} mail polling tasks", triggers.Count);

            foreach (var trigger in triggers)
            {
                var cts = new CancellationTokenSource();
                var task = Task.Run(() => PollMailTriggerAsync(trigger, cts.Token));
                _pollingTasks.Add((task, cts));
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start mail polling tasks");
        }
    }

    private async Task StopPollingTasksAsync()
    {
        foreach (var (_, cts) in _pollingTasks)
            await cts.CancelAsync();

        await Task.WhenAll(_pollingTasks.Select(t => t.Task).Where(t => !t.IsCompleted));
        _pollingTasks.Clear();
    }

    private async Task PollMailTriggerAsync(MailTrigger trigger, CancellationToken ct)
    {
        logger.LogInformation("Mail polling started for trigger: {Name} [{Mode}]",
            trigger.TriggerName, trigger.MailAuthMode);
        var settings = await settingsService.GetAsync();

        (var username, var password, var sso, IConfidentialClientApplication? cca) =
            trigger.MailAuthMode == "Interactive"
                ? ResolveCredentials(trigger, settings)
                : (null, null, false, null);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (trigger.MailAuthMode == "OAuth2Interactive")
                    await PollOnceOAuthAsync(trigger, settings);
                else
                    await PollOnceAsync(trigger, settings, username, password, sso, cca);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error polling mail for trigger {Name}", trigger.TriggerName);
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(settings.MailTriggerIntervalInMinutes), ct);
            }
            catch (OperationCanceledException) { break; }
        }

        logger.LogInformation("Mail polling stopped for trigger: {Name}", trigger.TriggerName);
    }

    private async Task PollOnceOAuthAsync(MailTrigger trigger, OrchSettings settings)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var fresh = await db.MailTriggers.FindAsync(trigger.Id);
        if (fresh == null || !fresh.Active) return;

        using var scope = scopeFactory.CreateScope();
        var mailOAuthService = scope.ServiceProvider.GetRequiredService<MailOAuthService>();
        var accessToken = await mailOAuthService.GetValidAccessTokenAsync(fresh);
        if (accessToken == null)
        {
            logger.LogWarning("Trigger [{Name}] has no valid OAuth token — skipping poll (re-authenticate in UI)",
                trigger.TriggerName);
            return;
        }

        var mailboxUpn = !string.IsNullOrWhiteSpace(fresh.SharedMailBox)
            ? fresh.SharedMailBox
            : fresh.OAuthUserUpn;

        if (string.IsNullOrWhiteSpace(mailboxUpn))
        {
            logger.LogWarning("Trigger [{Name}] has no mailbox UPN — skipping", trigger.TriggerName);
            return;
        }

        logger.LogDebug("OAuth2 polling trigger [{Name}] mailbox={Mailbox}", trigger.TriggerName, mailboxUpn);

        var messages = await graphMailService.ListUnreadMessagesAsync(accessToken, mailboxUpn, fresh.MailFolder);
        foreach (var message in messages)
        {
            if (!TriggerHelpers.MatchesMailFilters(message, fresh)) continue;

            var payload = TriggerHelpers.BuildGraphMailPayload(message, fresh);
            bool success = false;
            try
            {
                var folderPath = TriggerHelpers.BuildFolderPath(settings, fresh);
                await orchestrator.AddQueueItemAsync(payload, fresh.QueueName, folderPath,
                    Enum.TryParse<QueueItemPriority>(fresh.Priority, out var p) ? p : QueueItemPriority.Normal);
                success = true;
                await graphMailService.MarkAsReadAsync(accessToken, mailboxUpn, message["id"]!.ToString());
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error adding queue item for OAuth mail");
            }

            await LogQueueItemAsync(fresh, payload, success, success ? null : "Failed to add queue item");
        }
    }

    private async Task PollOnceAsync(
        MailTrigger trigger,
        OrchSettings settings,
        string? username,
        SecureString? password,
        bool sso,
        IConfidentialClientApplication? cca)
    {
        var mailServerURL = !string.IsNullOrEmpty(trigger.CustomMailServer)
            ? trigger.CustomMailServer : settings.MailServerURL;
        var mailServerVersion = !string.IsNullOrEmpty(trigger.CustomMailServerVersion)
            ? trigger.CustomMailServerVersion : settings.MailServerVersion;

        logger.LogDebug("Polling mail for trigger: {Name}", trigger.TriggerName);

        (var mails, var service) = HMS.Mail.Exchange.Core.GetMessages(
            mailServerURL,
            HMS.Mail.Exchange.Core.StringToExchangeVersion(mailServerVersion),
            sso,
            trigger.MailFolder,
            true,
            ref Nlog_dummy,
            false,
            null, null,
            true,
            trigger.SharedMailBox,
            trigger.UserDomain,
            username,
            password,
            null,
            trigger.SubjectFilterContains,
            trigger.From.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            trigger.To,
            trigger.BodyFilterContains,
            false,
            Microsoft.Exchange.WebServices.Data.SortDirection.Ascending,
            false,
            trigger.AttachmentsType.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            cca);

        foreach (var mail in mails)
        {
            var payload = PrepareEwsMailPayload(mail, trigger);
            bool success = false;
            try
            {
                var folderPath = TriggerHelpers.BuildFolderPath(settings, trigger);
                await orchestrator.AddQueueItemAsync(payload, trigger.QueueName, folderPath,
                    Enum.TryParse<QueueItemPriority>(trigger.Priority, out var p) ? p : QueueItemPriority.Normal);
                success = true;
                HMS.Mail.Exchange.Core.MarkMessageAsReadOrUnRead(service, mail, true, ref Nlog_dummy, false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error adding queue item for mail");
            }

            await LogQueueItemAsync(trigger, payload, success, success ? null : "Failed to add queue item");
        }
    }

    private static (string? username, SecureString? password, bool sso, IConfidentialClientApplication? cca)
        ResolveCredentials(MailTrigger trigger, OrchSettings settings)
    {
        if (string.IsNullOrWhiteSpace(trigger.UserName))
            return (null, null, true, null);

        string? username = null;
        SecureString? password = null;

        if (!string.IsNullOrWhiteSpace(trigger.Password))
        {
            username = trigger.UserName;
            password = new NetworkCredential("", trigger.Password).SecurePassword;
        }
        else if (trigger.UserName.StartsWith("CyberArk:::"))
        {
            // CyberArk not yet implemented in web version
            return (null, null, false, null);
        }
        else
        {
            try
            {
                var cm = new CredentialManagement.Credential { Target = "MailCred_" + trigger.UserName };
                if (cm.Load())
                {
                    username = cm.Username;
                    password = cm.SecurePassword;
                }
            }
            catch
            {
                return (null, null, false, null);
            }
        }

        IConfidentialClientApplication? cca = null;
        if (!string.IsNullOrWhiteSpace(trigger.AzureTenantId) && username != null && password != null)
        {
            cca = ConfidentialClientApplicationBuilder
                .Create(username)
                .WithClientSecret(new NetworkCredential("", password).Password)
                .WithTenantId(trigger.AzureTenantId)
                .Build();
        }

        return (username, password, false, cca);
    }

    private static JObject PrepareEwsMailPayload(MailMessage mail, MailTrigger trigger)
    {
        var mailDetails = mail.Headers["Date"] + "_" + mail.Subject + "_" + mail.Sender.Address;
        var reference = mailDetails[..Math.Min(128, mailDetails.Length)];

        var content = new JObject
        {
            ["TriggerName"] = trigger.TriggerName,
            ["MailUID"] = mail.Headers["UID"],
            ["MailInternetUID"] = mail.Headers["InternetUID"],
            ["MailFolder"] = trigger.MailFolder,
            ["MailSharedBox"] = trigger.SharedMailBox,
            ["Sender"] = mail.Sender.Address,
            ["Subject"] = mail.Subject,
            ["DateTimeReceived"] = mail.Headers["Date"],
            ["IsMeetingRequest"] = mail.Headers["IsMeetingRequest"],
            ["IsMeetingResponse"] = mail.Headers["IsMeetingResponse"],
            ["IsMeetingCancellation"] = mail.Headers["IsMeetingCancellation"]
        };

        if (bool.TryParse(mail.Headers["IsMeetingRequest"], out var isMeetingReq) && isMeetingReq)
        {
            content["AppointmentId"] = mail.Headers["AppointmentId"];
            content["MeetingRequestType"] = mail.Headers["MeetingRequestType"];
            content["MeetingStart"] = mail.Headers["MeetingStart"];
            content["MeetingEnd"] = mail.Headers["MeetingEnd"];
            if (mail.Headers.AllKeys.Contains("MeetingLocation"))
                content["MeetingLocation"] = mail.Headers["MeetingLocation"];
            content["MeetingIsRecurring"] = mail.Headers["MeetingIsRecurring"];
            if (bool.TryParse(mail.Headers["MeetingIsRecurring"], out var isRecurring) && isRecurring)
                content["MeetingRecurrence"] = mail.Headers["MeetingRecurrence"];
        }
        else if (bool.TryParse(mail.Headers["IsMeetingResponse"], out var isMeetingResp) && isMeetingResp)
        {
            content["MeetingResponseType"] = mail.Headers["MeetingResponseType"];
            content["AppointmentId"] = mail.Headers["AppointmentId"];
        }
        else if (bool.TryParse(mail.Headers["IsMeetingCancellation"], out var isMeetingCancel) && isMeetingCancel)
        {
            content["AppointmentId"] = mail.Headers["AppointmentId"];
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

    private async Task LogQueueItemAsync(MailTrigger trigger, JObject payload, bool success, string? error)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
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
        catch { /* log errors should not crash the service */ }
    }

    // NLog logger reference adapter — HMS.Mail.Exchange.Core expects ref Logger
    private static NLog.Logger Nlog_dummy = NLog.LogManager.GetCurrentClassLogger();

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await StopPollingTasksAsync();
        await base.StopAsync(cancellationToken);
    }
}
