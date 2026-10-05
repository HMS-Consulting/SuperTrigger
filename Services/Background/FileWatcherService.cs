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

public enum TriggerHealthState { Healthy, Down, Unknown }

public sealed record TriggerStatus(TriggerHealthState State, DateTime? LastSeenAt, string DetectionMode);

public class FileWatcherService(
    IDbContextFactory<AppDbContext> dbFactory,
    OrchestratorService orchestrator,
    AppSettingsService settingsService,
    TriggerReloadChannel reloadChannel,
    ILogger<FileWatcherService> logger) : BackgroundService
{
    private readonly List<WatcherEntry> _watchers = [];
    private readonly object _watchersLock = new();
    private readonly Dictionary<string, DateTime> _fileHistory = [];
    private readonly object _historyLock = new();
    private int _sameFileInterval = 20;
    private int _pollIntervalSeconds = 30;

    // Bounds how many folders can have a poll scan (SMB directory listing) in flight at once,
    // regardless of trigger count. This directly targets the SMB credit-exhaustion root cause
    // that FileSystemWatcher hits at ~100 concurrent persistent watches. Not admin-configurable
    // on purpose - see docs/file-watch-polling-plan.md.
    private const int MaxConcurrentPollScans = 10;
    private const int MaxFilesPerPollScan = MaxRescanFiles;
    private readonly SemaphoreSlim _pollSemaphore = new(MaxConcurrentPollScans);

    private const int HealthCheckIntervalSeconds = 15;
    private const int BaseRetrySeconds = 15;
    private const int MaxRetrySeconds = 300;
    private const int MaxRescanFiles = 500;

    private sealed class WatcherEntry(FileTrigger trigger, string filter)
    {
        public FileTrigger Trigger { get; } = trigger;
        public string Filter { get; } = filter;
        public FileSystemWatcher? Watcher { get; set; }
        public bool Healthy { get; set; }
        public bool Disposed { get; set; }
        public int FailureCount { get; set; }
        public DateTime NextRetry { get; set; } = DateTime.MinValue;
        public DateTime? DownSince { get; set; }
        public DateTime? LastEventAt { get; set; }

        // Polling-mode state. DetectionMode is set when the entry is registered and drives which
        // of these fields (vs. Watcher/FailureCount/DownSince above) are meaningful.
        public string DetectionMode { get; set; } = "FileSystemWatcher";
        public DateTime? LastScanAt { get; set; }
        public bool LastScanOk { get; set; } = true;
        public int ConsecutiveFailures { get; set; }
        public CancellationTokenSource? PollCts { get; set; }

        // File paths present after the previous successful scan. A file absent from this set is
        // treated as new even if its timestamps are old - moving a file into the folder preserves
        // both LastWriteTime and CreationTime, so a timestamp check alone would miss it. Null until
        // the first scan establishes a baseline.
        public HashSet<string>? KnownFiles { get; set; }
    }

    private CancellationToken _stoppingToken;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        logger.LogInformation("FileWatcherService starting");
        await LoadWatchersAsync();

        _ = Task.Run(() => CleanFileHistoryLoop(stoppingToken), stoppingToken);
        _ = Task.Run(() => WatcherHealthLoop(stoppingToken), stoppingToken);

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
            _pollIntervalSeconds = settings.FilePollingIntervalInSeconds;
            var usePolling = settings.FileWatchMethod == "Polling";

            await using var db = await dbFactory.CreateDbContextAsync();
            var triggers = await db.FileTriggers.Where(t => t.Active).ToListAsync();
            logger.LogInformation("Loading {Count} active file triggers ({Method})", triggers.Count, settings.FileWatchMethod);

            foreach (var trigger in triggers)
            {
                var extensions = trigger.FileTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var ext in extensions)
                {
                    var namePattern = trigger.FileNameMatchMode == "Equals"
                        ? trigger.FileNameContains
                        : "*" + trigger.FileNameContains + "*";
                    var filter = namePattern + "." + ext.TrimStart('.');

                    var entry = new WatcherEntry(trigger, filter);
                    lock (_watchersLock) _watchers.Add(entry);

                    if (usePolling)
                        StartPolling(entry, _stoppingToken);
                    else
                        // A failure here is not fatal: the entry stays registered and the
                        // health loop keeps retrying until the folder becomes reachable.
                        TryStartWatcher(entry);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load file watchers");
        }
    }

    /// <summary>
    /// Per-trigger-id status, aggregated across a trigger's extension-watchers (worst status
    /// wins - one unhealthy extension-watcher marks the whole trigger Down). A trigger id absent
    /// from the result has not been loaded yet (Unknown).
    /// </summary>
    public IReadOnlyDictionary<int, TriggerStatus> GetStatuses()
    {
        WatcherEntry[] snapshot;
        lock (_watchersLock) snapshot = [.. _watchers];

        var result = new Dictionary<int, TriggerStatus>();
        foreach (var group in snapshot.Where(e => !e.Disposed).GroupBy(e => e.Trigger.Id))
        {
            var entries = group.ToList();
            var healthy = entries.All(e => e.Healthy);
            var lastSeenTimes = entries
                .Select(e => e.DetectionMode == "Polling" ? e.LastScanAt : e.LastEventAt)
                .Where(t => t.HasValue)
                .Select(t => t!.Value)
                .ToList();

            result[group.Key] = new TriggerStatus(
                healthy ? TriggerHealthState.Healthy : TriggerHealthState.Down,
                lastSeenTimes.Count > 0 ? lastSeenTimes.Max() : null,
                entries[0].DetectionMode);
        }
        return result;
    }

    private bool TryStartWatcher(WatcherEntry entry)
    {
        var trigger = entry.Trigger;
        try
        {
            var watcher = CreateWatcher(trigger.FolderPath, entry.Filter, trigger.WatcherUsername, trigger.WatcherPassword);

            watcher.Created += (s, e) => { entry.LastEventAt = DateTime.Now; OnFileEvent(e.FullPath, trigger); };
            watcher.Renamed += (s, e) =>
            {
                if (Regex.IsMatch(e.Name ?? "", WildCardToRegular(entry.Filter)))
                {
                    entry.LastEventAt = DateTime.Now;
                    OnFileEvent(e.FullPath, trigger);
                }
            };
            watcher.Error += (s, e) =>
                MarkUnhealthy(entry, e.GetException().Message);

            entry.Watcher = watcher;
            entry.Healthy = true;
            entry.NextRetry = DateTime.MinValue;

            var userInfo = string.IsNullOrEmpty(trigger.WatcherUsername) ? "app account" : trigger.WatcherUsername;
            if (entry.FailureCount > 0)
                logger.LogInformation("Watcher recovered after {Attempts} failed attempt(s): {Path} | Filter: {Filter}",
                    entry.FailureCount, trigger.FolderPath, entry.Filter);
            else
                logger.LogInformation("Watching: {Path} | Filter: {Filter} | User: {User}", trigger.FolderPath, entry.Filter, userInfo);

            entry.FailureCount = 0;

            // Files dropped while the watcher was down never raised an event - pick them up now.
            if (entry.DownSince is { } downSince)
            {
                var since = downSince;
                entry.DownSince = null;
                _ = Task.Run(() => RescanMissedFiles(entry, since));
            }

            return true;
        }
        catch (Exception ex)
        {
            entry.Healthy = false;
            entry.Watcher = null;
            entry.DownSince ??= DateTime.Now;
            entry.FailureCount++;
            ScheduleRetry(entry);

            // Log the first failures loudly, then throttle to avoid flooding the log
            // while a share stays offline for hours.
            if (entry.FailureCount <= 3 || entry.FailureCount % 20 == 0)
                logger.LogError(ex, "Failed to create watcher for trigger {Name} on {Path} (attempt {Attempt}); retrying in {Delay}s",
                    trigger.TriggerName, trigger.FolderPath, entry.FailureCount, (int)(entry.NextRetry - DateTime.Now).TotalSeconds);
            else
                logger.LogDebug("Watcher for trigger {Name} still unavailable (attempt {Attempt})", trigger.TriggerName, entry.FailureCount);

            return false;
        }
    }

    private void MarkUnhealthy(WatcherEntry entry, string reason)
    {
        if (entry.Disposed) return;

        var wasHealthy = entry.Healthy;
        entry.Healthy = false;
        entry.DownSince ??= DateTime.Now;
        entry.FailureCount++;
        ScheduleRetry(entry);

        try { entry.Watcher?.Dispose(); } catch { /* already gone */ }
        entry.Watcher = null;

        if (wasHealthy)
            logger.LogError("Watcher for trigger {Name} on {Path} stopped: {Reason}. Retrying in {Delay}s",
                entry.Trigger.TriggerName, entry.Trigger.FolderPath, reason, (int)(entry.NextRetry - DateTime.Now).TotalSeconds);
    }

    private static void ScheduleRetry(WatcherEntry entry)
    {
        var delay = Math.Min(MaxRetrySeconds, BaseRetrySeconds * Math.Pow(2, Math.Min(entry.FailureCount - 1, 10)));
        entry.NextRetry = DateTime.Now.AddSeconds(delay);
    }

    /// <summary>
    /// Periodically verifies every watcher is alive and the folder is still reachable,
    /// and restarts the ones that are down. Covers both creation failures and watchers
    /// that die later (share disconnected, permissions revoked, buffer overflow).
    /// </summary>
    private async Task WatcherHealthLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(HealthCheckIntervalSeconds), ct); }
            catch (OperationCanceledException) { return; }

            WatcherEntry[] snapshot;
            lock (_watchersLock) snapshot = [.. _watchers];

            foreach (var entry in snapshot)
            {
                if (ct.IsCancellationRequested) return;
                if (entry.Disposed) continue;
                // Polling entries self-heal inside PollLoopAsync/ScanFolder - this loop only
                // covers the FileSystemWatcher path (create/error/retry).
                if (entry.DetectionMode == "Polling") continue;

                try
                {
                    if (entry.Healthy)
                    {
                        // A dropped network share often does not raise Error - probe the folder.
                        if (entry.Watcher is { EnableRaisingEvents: true } && IsFolderAccessible(entry))
                            continue;

                        MarkUnhealthy(entry, "folder is no longer accessible or the watcher stopped raising events");
                    }

                    if (DateTime.Now >= entry.NextRetry)
                        TryStartWatcher(entry);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Health check failed for trigger {Name}", entry.Trigger.TriggerName);
                }
            }
        }
    }

    private bool IsFolderAccessible(WatcherEntry entry)
    {
        try
        {
            return RunAs(entry.Trigger.WatcherUsername, entry.Trigger.WatcherPassword,
                () => Directory.Exists(entry.Trigger.FolderPath));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Folder probe failed for {Path}", entry.Trigger.FolderPath);
            return false;
        }
    }

    private void RescanMissedFiles(WatcherEntry entry, DateTime downSince)
    {
        try
        {
            var files = RunAs(entry.Trigger.WatcherUsername, entry.Trigger.WatcherPassword, () =>
                Directory.EnumerateFiles(entry.Trigger.FolderPath, entry.Filter, SearchOption.TopDirectoryOnly)
                    .Where(f => File.GetLastWriteTime(f) >= downSince || File.GetCreationTime(f) >= downSince)
                    .Take(MaxRescanFiles + 1)
                    .ToList());

            if (files.Count == 0) return;

            if (files.Count > MaxRescanFiles)
            {
                files = files.Take(MaxRescanFiles).ToList();
                logger.LogWarning("More than {Max} files were added to {Path} while the watcher was down; only the first {Max} are processed",
                    MaxRescanFiles, entry.Trigger.FolderPath, MaxRescanFiles);
            }

            logger.LogInformation("Recovering {Count} file(s) created in {Path} while the watcher was down (since {Since})",
                files.Count, entry.Trigger.FolderPath, downSince);

            foreach (var file in files)
                OnFileEvent(file, entry.Trigger);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to rescan {Path} after watcher recovery", entry.Trigger.FolderPath);
        }
    }

    /// <summary>
    /// Registers a polling-mode watcher entry and starts its scan loop. Called from
    /// LoadWatchersAsync when settings.FileWatchMethod == "Polling".
    /// </summary>
    private void StartPolling(WatcherEntry entry, CancellationToken stoppingToken)
    {
        entry.DetectionMode = "Polling";
        entry.LastScanAt = DateTime.Now;
        entry.Healthy = true;
        entry.LastScanOk = true;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        entry.PollCts = cts;

        var userInfo = string.IsNullOrEmpty(entry.Trigger.WatcherUsername) ? "app account" : entry.Trigger.WatcherUsername;
        logger.LogInformation("Polling: {Path} | Filter: {Filter} | Interval: {Interval}s | User: {User}",
            entry.Trigger.FolderPath, entry.Filter, _pollIntervalSeconds, userInfo);

        _ = Task.Run(() => PollLoopAsync(entry, cts.Token), cts.Token);
    }

    private async Task PollLoopAsync(WatcherEntry entry, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _pollIntervalSeconds));

        try
        {
            // Stagger initial scans across the interval window so many folders don't all hit
            // the file server in the same instant - after this, each folder's loop free-runs
            // on its own cadence and natural drift between folders is fine.
            await Task.Delay(Random.Shared.Next(0, (int)interval.TotalMilliseconds), ct);

            while (!ct.IsCancellationRequested)
            {
                await _pollSemaphore.WaitAsync(ct);
                try { ScanFolder(entry); }
                finally { _pollSemaphore.Release(); }

                await Task.Delay(interval, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on reload (method switch / trigger change) or service shutdown.
        }
    }

    private void ScanFolder(WatcherEntry entry)
    {
        var trigger = entry.Trigger;
        var since = entry.LastScanAt ?? DateTime.MinValue;
        var scanStarted = DateTime.Now;

        try
        {
            var files = RunAs(trigger.WatcherUsername, trigger.WatcherPassword, () =>
                Directory.EnumerateFiles(trigger.FolderPath, entry.Filter, SearchOption.TopDirectoryOnly)
                    .Select(f => (Path: f, Time: MaxTime(File.GetLastWriteTime(f), File.GetCreationTime(f))))
                    .ToList());

            entry.LastScanOk = true;
            entry.ConsecutiveFailures = 0;
            entry.Healthy = true;

            // New = not seen in the previous scan (e.g. moved in with old timestamps), or
            // created/modified since the previous scan. On the first scan there is no baseline,
            // so only the timestamp check applies - pre-existing files are not triggered.
            var known = entry.KnownFiles;
            var candidates = files
                .Where(f => f.Time >= since || (known != null && !known.Contains(f.Path)))
                .OrderBy(f => f.Time)
                .ToList();

            var current = new HashSet<string>(files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);

            if (candidates.Count > MaxFilesPerPollScan)
            {
                // Leave the files beyond the cap out of the known set so the next cycle treats
                // them as new again instead of silently skipping them.
                foreach (var skipped in candidates.Skip(MaxFilesPerPollScan))
                    current.Remove(skipped.Path);
                candidates = candidates.Take(MaxFilesPerPollScan).ToList();
                logger.LogWarning(
                    "Poll scan of {Path} found more than {Max} new file(s) in one cycle; only the oldest {Max} are processed this cycle - the rest will be picked up next cycle",
                    trigger.FolderPath, MaxFilesPerPollScan, MaxFilesPerPollScan);
            }

            entry.KnownFiles = current;
            entry.LastScanAt = scanStarted;

            foreach (var file in candidates)
                OnFileEvent(file.Path, trigger);
        }
        catch (Exception ex)
        {
            entry.LastScanOk = false;
            entry.Healthy = false;
            entry.ConsecutiveFailures++;

            // Log the first failures loudly, then throttle to avoid flooding the log while a
            // share stays offline for a long time.
            if (entry.ConsecutiveFailures <= 3 || entry.ConsecutiveFailures % 20 == 0)
                logger.LogError(ex, "Poll scan failed for trigger {Name} on {Path} (attempt {Attempt})",
                    trigger.TriggerName, trigger.FolderPath, entry.ConsecutiveFailures);
            else
                logger.LogDebug("Poll scan for trigger {Name} still failing (attempt {Attempt})",
                    trigger.TriggerName, entry.ConsecutiveFailures);
        }
    }

    private static DateTime MaxTime(DateTime a, DateTime b) => a > b ? a : b;

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
            try { await Task.Delay(TimeSpan.FromSeconds(_sameFileInterval), ct); }
            catch (OperationCanceledException) { return; }

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
        lock (_watchersLock)
        {
            foreach (var entry in _watchers)
            {
                entry.Disposed = true;
                entry.Healthy = false;
                try { entry.Watcher?.Dispose(); } catch { /* ignore */ }
                entry.Watcher = null;
                try { entry.PollCts?.Cancel(); entry.PollCts?.Dispose(); } catch { /* ignore */ }
                entry.PollCts = null;
            }
            _watchers.Clear();
        }
    }

    public override void Dispose()
    {
        DisposeWatchers();
        base.Dispose();
    }

    private FileSystemWatcher CreateWatcher(string path, string filter, string? username, string? password) =>
        RunAs(username, password, () => BuildWatcher(path, filter));

    /// <summary>Runs <paramref name="action"/> impersonating the trigger credentials, if any.</summary>
    private static T RunAs<T>(string? username, string? password, Func<T> action)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            return action();

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
            T result = default!;
            WindowsIdentity.RunImpersonated(identity.AccessToken, () => result = action());
            return result;
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
