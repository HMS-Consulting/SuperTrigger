using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Client;
using Newtonsoft.Json.Linq;
using SuperTrigger.Web.Data;
using SuperTrigger.Web.Data.Entities;
using SuperTrigger.Web.Services;
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
    GraphMailService graphMailService,
    GraphTriggerAuthService graphAuthService,
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
            var settings = await settingsService.GetAsync();
            await using var db = await dbFactory.CreateDbContextAsync();
            var triggers = (await db.MailTriggers.Where(t => t.Active).ToListAsync())
                .Where(t => !t.UsesWebhook(settings))
                .ToList();
            logger.LogInformation("Starting {Count} mail polling tasks", triggers.Count);

            foreach (var trigger in triggers)
            {
                var cts = new CancellationTokenSource();
                var task = Task.Run(() => PollMailTriggerAsync(trigger, cts.Token));

                // Nothing ever observes these tasks: StopPollingTasksAsync filters on !IsCompleted,
                // and a faulted task *is* completed, so it is excluded from that WhenAll and its
                // exception is never surfaced. Without this continuation a fault escaping the retry
                // loop in PollMailTriggerAsync would be swallowed by the runtime -- no log, no
                // restart, the trigger just silently stops polling. Reading t.Exception here also
                // marks it observed, so it can never reach TaskScheduler.UnobservedTaskException.
                _ = task.ContinueWith(
                    t => logger.LogError(t.Exception,
                        "Mail polling task for trigger {Name} terminated unexpectedly and will not restart "
                        + "until the next reload or application restart", trigger.TriggerName),
                    TaskContinuationOptions.OnlyOnFaulted);

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

    // Retry envelope around the poll loop. The loop itself already swallows per-cycle failures, but
    // everything before it -- loading settings, resolving credentials -- used to throw straight out of
    // the Task.Run that started it, killing that trigger permanently. A locked database or a
    // temporarily unreachable credential store at the wrong moment was enough. Backs off so a
    // genuinely broken trigger settles into one attempt every 10 minutes instead of spinning.
    private async Task PollMailTriggerAsync(MailTrigger trigger, CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(30);
        var maxBackoff = TimeSpan.FromMinutes(10);

        while (!ct.IsCancellationRequested)
        {
            long startedAt = Environment.TickCount64;
            Exception? failure = null;
            try
            {
                await RunPollLoopAsync(trigger, ct);
            }
            // Only *our* cancellation means "stop for good". Any other OperationCanceledException --
            // an HttpClient timeout surfacing as TaskCanceledException, most often -- is a fault to
            // retry, not a shutdown signal. Reading it as shutdown is what used to end a trigger
            // permanently after a single slow Graph call.
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { failure = ex; }

            if (ct.IsCancellationRequested) break;

            // A loop that ran a long time before failing is a fresh problem, not the same one
            // repeating -- don't punish it with the backoff a rapid-fail cycle has built up.
            if (Environment.TickCount64 - startedAt > (long)TimeSpan.FromMinutes(5).TotalMilliseconds)
                backoff = TimeSpan.FromSeconds(30);

            if (failure != null)
                logger.LogError(failure, "Mail polling for trigger {Name} failed outside the poll cycle; "
                    + "retrying in {Backoff}", trigger.TriggerName, backoff);
            else
                // The poll loop returned while nobody asked it to stop. Restart it rather than
                // treating a silent exit as completion.
                logger.LogWarning("Mail poll loop for trigger {Name} exited without cancellation; "
                    + "restarting in {Backoff}", trigger.TriggerName, backoff);

            try { await Task.Delay(backoff, ct); }
            catch (OperationCanceledException) { break; }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, maxBackoff.Ticks));
        }

        logger.LogInformation("Mail polling stopped for trigger: {Name}", trigger.TriggerName);
    }

    private async Task RunPollLoopAsync(MailTrigger trigger, CancellationToken ct)
    {
        logger.LogInformation("Mail polling started for trigger: {Name} [{Mode}]",
            trigger.TriggerName, trigger.MailAuthMode);
        var settings = await settingsService.GetAsync();
        var effectiveMode = trigger.EffectiveMailAuthMode(settings);

        (var username, var password, var sso, IConfidentialClientApplication? cca) =
            effectiveMode == "Interactive"
                ? ResolveCredentials(trigger, settings)
                : (null, null, false, null);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (effectiveMode == "Interactive")
                    await PollOnceAsync(trigger, settings, username, password, sso, cca);
                else
                    await PollOnceGraphAsync(trigger, settings);
            }
            // Same distinction as in the retry envelope: a Graph/EWS call that times out throws
            // TaskCanceledException, which is a failed cycle -- log it and poll again next tick.
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error polling mail for trigger {Name}", trigger.TriggerName);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(settings.MailTriggerIntervalInSeconds), ct);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PollOnceGraphAsync(MailTrigger trigger, OrchSettings settings)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var fresh = await db.MailTriggers.FindAsync(trigger.Id);
        if (fresh == null || !fresh.Active) return;

        var accessToken = await graphAuthService.ResolveAccessTokenAsync(fresh, settings);
        if (accessToken == null)
        {
            logger.LogWarning("Trigger [{Name}] ({Mode}) has no valid Graph token — skipping poll",
                trigger.TriggerName, trigger.MailAuthMode);
            return;
        }

        string mailboxUpn;
        try
        {
            mailboxUpn = GraphTriggerAuthService.ResolveMailboxUpnForTrigger(fresh, settings);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("{Message}", ex.Message);
            return;
        }

        logger.LogDebug("Graph polling trigger [{Name}] mailbox={Mailbox}", trigger.TriggerName, mailboxUpn);

        var messages = await graphMailService.ListUnreadMessagesAsync(accessToken, mailboxUpn, fresh.MailFolder);
        if (messages.Count > 0)
            logger.LogInformation("Trigger [{Name}] fetched {Count} unread message(s) from mailbox {Mailbox}/{Folder}",
                trigger.TriggerName, messages.Count, mailboxUpn, fresh.MailFolder);

        foreach (var message in messages)
        {
            var messageId = message["id"]?.ToString() ?? "";
            var subject = message["subject"]?.ToString() ?? "";
            var sender = message["from"]?["emailAddress"]?["address"]?.ToString() ?? "";

            if (!TriggerHelpers.MatchesMailFilters(message, fresh))
            {
                logger.LogInformation(
                    "Trigger [{Name}] skipped mail (filters not matched) — Subject='{Subject}' From='{Sender}' Id={Id}",
                    trigger.TriggerName, subject, sender, messageId);
                continue;
            }

            logger.LogInformation(
                "Trigger [{Name}] detected relevant mail — Subject='{Subject}' From='{Sender}' Id={Id}",
                trigger.TriggerName, subject, sender, messageId);

            var payload = TriggerHelpers.BuildGraphMailPayload(message, fresh);
            bool success = false;
            string? error = null;
            try
            {
                var folderPath = TriggerHelpers.BuildFolderPath(settings, fresh);
                await orchestrator.AddQueueItemAsync(payload, fresh.QueueName, folderPath,
                    Enum.TryParse<QueueItemPriority>(fresh.Priority, out var p) ? p : QueueItemPriority.Normal);
                success = true;
                logger.LogInformation(
                    "Trigger [{Name}] added queue item to '{Queue}' — Subject='{Subject}' Id={Id}",
                    trigger.TriggerName, fresh.QueueName, subject, messageId);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                logger.LogError(ex, "Trigger [{Name}] failed to add queue item for mail Subject='{Subject}' Id={Id}",
                    trigger.TriggerName, subject, messageId);
            }

            if (success)
            {
                try
                {
                    await graphMailService.MarkAsReadAsync(accessToken, mailboxUpn, messageId);
                    logger.LogInformation("Trigger [{Name}] marked mail as READ — Subject='{Subject}' Id={Id}",
                        trigger.TriggerName, subject, messageId);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Trigger [{Name}] failed to mark mail as read Subject='{Subject}' Id={Id}",
                        trigger.TriggerName, subject, messageId);
                }
            }

            await LogQueueItemAsync(fresh, payload, success, success ? null : error ?? "Failed to add queue item");
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
            GraphTriggerAuthService.ResolveMailboxUpnForTrigger(trigger, settings),
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

        var mailList = mails?.ToList() ?? [];
        if (mailList.Count > 0)
            logger.LogInformation("Trigger [{Name}] detected {Count} relevant mail(s) in folder '{Folder}'",
                trigger.TriggerName, mailList.Count, trigger.MailFolder);

        foreach (var mail in mailList)
        {
            var subject = mail.Subject ?? "";
            var sender = mail.Sender?.Address ?? "";
            var mailId = mail.Headers["UID"] ?? "";

            logger.LogInformation(
                "Trigger [{Name}] detected relevant mail — Subject='{Subject}' From='{Sender}' Id={Id}",
                trigger.TriggerName, subject, sender, mailId);

            var payload = PrepareEwsMailPayload(mail, trigger);
            bool success = false;
            string? error = null;
            try
            {
                var folderPath = TriggerHelpers.BuildFolderPath(settings, trigger);
                await orchestrator.AddQueueItemAsync(payload, trigger.QueueName, folderPath,
                    Enum.TryParse<QueueItemPriority>(trigger.Priority, out var p) ? p : QueueItemPriority.Normal);
                success = true;
                logger.LogInformation(
                    "Trigger [{Name}] added queue item to '{Queue}' — Subject='{Subject}' Id={Id}",
                    trigger.TriggerName, trigger.QueueName, subject, mailId);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                logger.LogError(ex, "Trigger [{Name}] failed to add queue item for mail Subject='{Subject}' Id={Id}",
                    trigger.TriggerName, subject, mailId);
            }

            if (success)
            {
                try
                {
                    HMS.Mail.Exchange.Core.MarkMessageAsReadOrUnRead(service, mail, true, ref Nlog_dummy, false);
                    logger.LogInformation("Trigger [{Name}] marked mail as READ — Subject='{Subject}' Id={Id}",
                        trigger.TriggerName, subject, mailId);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Trigger [{Name}] failed to mark mail as read Subject='{Subject}' Id={Id}",
                        trigger.TriggerName, subject, mailId);
                }
            }

            await LogQueueItemAsync(trigger, payload, success, success ? null : error ?? "Failed to add queue item");
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
            ["MailId"] = mail.Headers["UID"],
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
        catch (Exception ex)
        {
            // log errors should not crash the service, but must be visible
            logger.LogError(ex, "Failed to write QueueItemLog for trigger [{Name}]", trigger.TriggerName);
        }
    }

    // NLog logger reference adapter — HMS.Mail.Exchange.Core expects ref Logger
    private static NLog.Logger Nlog_dummy = NLog.LogManager.GetCurrentClassLogger();

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await StopPollingTasksAsync();
        await base.StopAsync(cancellationToken);
    }
}
