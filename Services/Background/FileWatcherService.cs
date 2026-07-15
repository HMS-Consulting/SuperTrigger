using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;
using SuperTrigger.Web.Data;
using SuperTrigger.Web.Data.Entities;
using SuperTrigger.Web.Services;

namespace SuperTrigger.Web.Services.Background;

public class FileWatcherService(
    IDbContextFactory<AppDbContext> dbFactory,
    OrchestratorService orchestrator,
    AppSettingsService settingsService,
    TriggerReloadChannel reloadChannel,
    ILogger<FileWatcherService> logger) : BackgroundService
{
    private readonly List<WatcherEntry> _watchers = [];
    private readonly Dictionary<string, DateTime> _fileHistory = [];
    private readonly object _historyLock = new();
    private int _sameFileInterval = 20;

    private record WatcherEntry(FileSystemWatcher Watcher, FileTrigger Trigger);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("FileWatcherService starting");
        await LoadWatchersAsync();

        _ = Task.Run(() => CleanFileHistoryLoop(stoppingToken), stoppingToken);

        await foreach (var _ in reloadChannel.Subscribe().ReadAllAsync(stoppingToken))
        {
            logger.LogInformation("Reloading file watchers...");
            DisposeWatchers();
            await LoadWatchersAsync();
        }
    }

    private async Task LoadWatchersAsync()
    {
        try
        {
            var settings = await settingsService.GetAsync();
            _sameFileInterval = settings.SameFileIntervalInSeconds;

            await using var db = await dbFactory.CreateDbContextAsync();
            var triggers = await db.FileTriggers.Where(t => t.Active).ToListAsync();
            logger.LogInformation("Loading {Count} active file triggers", triggers.Count);

            foreach (var trigger in triggers)
            {
                var extensions = trigger.FileTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var ext in extensions)
                {
                    try
                    {
                        var filter = trigger.FileNameContains + "*." + ext.TrimStart('.');
                        var watcher = CreateWatcher(trigger.FolderPath, filter, trigger.WatcherUsername, trigger.WatcherPassword);

                        watcher.Created += (s, e) => OnFileEvent(e.FullPath, trigger);
                        watcher.Renamed += (s, e) =>
                        {
                            if (Regex.IsMatch(e.Name ?? "", WildCardToRegular(filter)))
                                OnFileEvent(e.FullPath, trigger);
                        };
                        watcher.Error += (s, e) =>
                        {
                            logger.LogError("Watcher error for trigger {Name}: {Error}", trigger.TriggerName, e.GetException().Message);
                            watcher.EnableRaisingEvents = false;
                        };

                        _watchers.Add(new WatcherEntry(watcher, trigger));
                        var userInfo = string.IsNullOrEmpty(trigger.WatcherUsername) ? "app account" : trigger.WatcherUsername;
                        logger.LogInformation("Watching: {Path} | Filter: {Filter} | User: {User}", trigger.FolderPath, filter, userInfo);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to create watcher for trigger {Name}", trigger.TriggerName);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load file watchers");
        }
    }

    private void OnFileEvent(string fullPath, FileTrigger trigger)
    {
        if (Path.GetFileName(fullPath).StartsWith("~$")) return;

        lock (_historyLock)
        {
            if (_fileHistory.TryGetValue(fullPath, out var lastSeen) &&
                Math.Abs((DateTime.Now - lastSeen).TotalSeconds) <= _sameFileInterval)
                return;

            _fileHistory[fullPath] = DateTime.Now;
        }

        logger.LogInformation("File detected: {Path} | Trigger: {Trigger}", fullPath, trigger.TriggerName);
        _ = Task.Run(() => ProcessFileAsync(fullPath, trigger));
    }

    private async Task ProcessFileAsync(string fullPath, FileTrigger trigger)
    {
        try
        {
            var settings = await settingsService.GetAsync();
            var payload = PrepareFilePayload(fullPath);
            var folderPath = TriggerHelpers.BuildFolderPath(settings, trigger);

            await orchestrator.AddQueueItemAsync(payload, trigger.QueueName, folderPath,
                Enum.TryParse<QueueItemPriority>(trigger.Priority, out var p) ? p : QueueItemPriority.Normal);

            await LogQueueItemAsync(trigger.TriggerName, trigger.QueueName,
                payload["itemData"]?["Reference"]?.ToString() ?? "", payload.ToString(), true, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Error processing file\n  Trigger: {Trigger}\n  File: {Path}\n  Queue: {Queue}",
                trigger.TriggerName, fullPath, trigger.QueueName);
            await LogQueueItemAsync(trigger.TriggerName, trigger.QueueName, fullPath, "", false, ex.Message);
        }
    }

    private static JObject PrepareFilePayload(string filePath)
    {
        var temp = "_" + DateTime.Now.ToString("dd_MM_yyyy_HH_mm_ss_fff");
        var fileName = Path.GetFileName(filePath);
        var reference = fileName[..Math.Min(128 - temp.Length, fileName.Length)] + temp;

        return new JObject
        {
            ["itemData"] = new JObject
            {
                ["Reference"] = reference,
                ["SpecificContent"] = new JObject { ["filePath"] = filePath }
            }
        };
    }

    private async Task LogQueueItemAsync(string triggerName, string queueName, string reference, string payload, bool success, string? error)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            db.QueueItemLogs.Add(new QueueItemLog
            {
                TriggerName = triggerName,
                TriggerType = TriggerType.File,
                QueueName = queueName,
                Reference = reference,
                Payload = payload,
                Success = success,
                ErrorMessage = error
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to log queue item");
        }
    }

    private async Task CleanFileHistoryLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(_sameFileInterval), ct);
            lock (_historyLock)
            {
                var cutoff = DateTime.Now.AddSeconds(-_sameFileInterval);
                var toRemove = _fileHistory.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList();
                toRemove.ForEach(k => _fileHistory.Remove(k));
            }
        }
    }

    private void DisposeWatchers()
    {
        foreach (var entry in _watchers)
            entry.Watcher.Dispose();
        _watchers.Clear();
    }

    public override void Dispose()
    {
        DisposeWatchers();
        base.Dispose();
    }

    private FileSystemWatcher CreateWatcher(string path, string filter, string? username, string? password)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            return BuildWatcher(path, filter);

        string domain, user;
        if (username.Contains('\\'))
        {
            var parts = username.Split('\\', 2);
            domain = parts[0];
            user = parts[1];
        }
        else if (username.Contains('@'))
        {
            var parts = username.Split('@', 2);
            user = parts[0];
            domain = parts[1];
        }
        else
        {
            domain = ".";
            user = username;
        }

        if (!LogonUser(user, domain, password, LOGON32_LOGON_NEW_CREDENTIALS, LOGON32_PROVIDER_WINNT50, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"LogonUser failed for {username}");

        try
        {
            using var identity = new WindowsIdentity(token);
            FileSystemWatcher? watcher = null;
            WindowsIdentity.RunImpersonated(identity.AccessToken, () => watcher = BuildWatcher(path, filter));
            return watcher!;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static FileSystemWatcher BuildWatcher(string path, string filter) =>
        new FileSystemWatcher(path, filter)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite,
            IncludeSubdirectories = false,
            InternalBufferSize = 64000,
            EnableRaisingEvents = true
        };

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LogonUser(string lpszUsername, string lpszDomain, string lpszPassword,
        int dwLogonType, int dwLogonProvider, out IntPtr phToken);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private const int LOGON32_LOGON_NEW_CREDENTIALS = 9;
    private const int LOGON32_PROVIDER_WINNT50 = 3;

    private static string WildCardToRegular(string pattern) =>
        "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
}
