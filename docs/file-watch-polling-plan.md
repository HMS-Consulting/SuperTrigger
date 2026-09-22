# File Trigger Detection: Dual-Mode (FileSystemWatcher + Polling) — Implementation Plan

## Context

On a customer deployment with 100+ active File Triggers pointing at folders on a NetApp
SMB3.1.1 share, the ~100th `FileSystemWatcher` silently stops receiving file events. Root
cause: SMB3 credit-based flow control — the server grants each session a limited number of
outstanding request "credits," and the persistent `ReadDirectoryChangesW` (Change Notify)
request behind `FileSystemWatcher` gets starved once ~100 concurrent notify requests are
outstanding. This is invisible to the app: `TryStartWatcher` succeeds, the watcher reports
`Healthy = true`, and even our own health probe (`Directory.Exists`) succeeds independently,
because it's a short one-shot request rather than a persistent one. There is no exception, no
log entry, and no existing health-check signal — the watcher just never fires again.

The fix that's actually available to us (we don't control the NetApp filer) is to give the
admin a second detection method that doesn't hold a persistent handle per folder: periodic
**polling** (directory listing scan). This plan adds Polling as a selectable alternative to
the existing FileSystemWatcher method, as a **global** setting (Settings page) — not
per-trigger — with FileSystemWatcher remaining the default so existing deployments are
unaffected until an admin explicitly opts in.

Decisions already confirmed with the user:
- Default polling interval: **30 seconds**.
- Concurrency cap / batch size for polling: **hardcoded constants**, not exposed in Settings
  (keeps the admin-facing surface minimal and prevents misconfiguration from recreating the
  original SMB overload problem).
- Scope now also includes a **status/health column** for File Triggers (Healthy/Down/Last
  seen), covering both detection methods — this did not exist before this plan.

## Data Model

### `Data/Entities/OrchSettings.cs` — add two fields

```csharp
// Global file-trigger detection method ("FileSystemWatcher" | "Polling") — same idiom as
// GlobalMailAuthMode/GraphDetectionMode (plain string, not a C# enum). No per-trigger override.
public string FileWatchMethod { get; set; } = "FileSystemWatcher";

// Poll interval in seconds, used only when FileWatchMethod = "Polling". Distinct from
// SameFileIntervalInSeconds, which is a same-file dedup window used by both methods, not a
// scan cadence.
public int FilePollingIntervalInSeconds { get; set; } = 30;
```

`SameFileIntervalInSeconds` stays untouched — it's a different concept (dedup window against
duplicate events for the same file) and applies to both detection methods equally.

### `Services/DatabaseMigrator.cs` — append to `SchemaBootstrapSql` (SQLite)

```
"ALTER TABLE OrchSettings ADD COLUMN FileWatchMethod TEXT NOT NULL DEFAULT 'FileSystemWatcher'",
"ALTER TABLE OrchSettings ADD COLUMN FilePollingIntervalInSeconds INTEGER NOT NULL DEFAULT 30",
```

Each statement is idempotent (existing pattern: wrapped in try/catch, ignores "column already
exists"). No data backfill needed — brand-new columns with sensible defaults.

**SQL Server**: no manual step — `SqlServerSchemaBuilder.EnsureSchemaAsync` diffs the live EF
model and adds missing columns automatically once the two C# properties exist on `OrchSettings`.

## Backend Architecture

Extend `Services/Background/FileWatcherService.cs` in place rather than adding a sibling
service — both modes share the dedup (`_fileHistory`), `OnFileEvent`, `ProcessFileAsync`,
`RunAs` impersonation, per-trigger×extension `WatcherEntry` enumeration, and
`TriggerReloadChannel` wiring. Splitting into two services would duplicate all of that.

### `WatcherEntry` changes
- Add `DetectionMode` (`FileSystemWatcher` | `Polling`).
- Add `LastScanAt` / `LastScanOk` / `ConsecutiveFailures` (used by both the polling loop and
  the new status UI — see below).
- Add a `CancellationTokenSource?` for polling entries (their loop's lifetime), alongside the
  existing `FileSystemWatcher?` (unused when in Polling mode).

### `LoadWatchersAsync` — branch on the global setting
Reads `settings.FileWatchMethod` once per load (same pattern as the existing
`_sameFileInterval` read). Builds the same per-trigger×extension `WatcherEntry` list either way
(no change to trigger/extension enumeration), then:
- `"FileSystemWatcher"` → existing `TryStartWatcher` path, unchanged.
- `"Polling"` → registers the entry with the new polling engine instead of a live
  `FileSystemWatcher`.

### Polling engine
- **Bounded concurrency**: `SemaphoreSlim(MaxConcurrentPollScans)` — new to the codebase (no
  general-purpose throttling utility currently exists; the only other `SemaphoreSlim` is
  `OrchestratorService`'s single-slot token-refresh lock, unrelated). This directly targets the
  root cause: caps how many concurrent SMB directory listings are in flight, regardless of
  trigger count.
- **Staggered start**: each folder's first scan is offset by a jittered delay within
  `[0, FilePollingIntervalInSeconds)` at load time, so scans spread across the interval window
  instead of bursting all at once every tick. After the first scan, each folder's loop runs
  independently (`scan → Task.Delay(interval) → scan → ...`); natural drift between folders is
  fine.
- **Scan logic** (reuses the existing `RescanMissedFiles` pattern): `Directory.EnumerateFiles`
  via `RunAs` impersonation, filtered by extension, capped at `MaxFilesPerPollScan` entries,
  compared against the entry's own `LastScanAt` (the polling equivalent of `downSince`) so only
  files new/modified since that folder's last successful scan are candidates. Matches are
  routed through the existing `OnFileEvent`, so dedup/logging/`ProcessFileAsync` behave
  identically to FileSystemWatcher-detected files.
- **Overflow handling**: if a scan hits the `MaxFilesPerPollScan` cap, advance `LastScanAt`
  only to the timestamp of the oldest file actually processed in that batch (not "now") so the
  remaining files are picked up on the next cycle rather than silently dropped, and log a
  warning so an admin can tell if their interval is too long for the folder's file volume.
- **Self-healing**: scan failures (folder inaccessible, SMB timeout) are caught per folder,
  logged, and increment `ConsecutiveFailures` — the loop keeps retrying every interval, no
  explicit backoff needed (unlike FSW, polling has no persistent connection to exhaust).
- **Teardown**: `DisposeWatchers` cancels polling `CancellationTokenSource`s alongside disposing
  `FileSystemWatcher`s, so a reload (method switch or trigger change) cleanly stops whichever
  mode was active.

### Hardcoded constants (not admin-configurable, per confirmed decision)
```csharp
private const int MaxConcurrentPollScans = 10;
private const int MaxFilesPerPollScan = MaxRescanFiles; // reuse existing 500 cap
```
With 100+ folders and a cap of 10, worst case is 10 concurrent SMB listings at any moment,
independent of trigger count.

## Status/Health Surface (new scope)

Currently no UI reflects watcher health at all — `Healthy`/`FailureCount`/`DownSince` exist
only inside `FileWatcherService`'s in-memory state. This plan adds:

- A query method on `FileWatcherService`, e.g. `IReadOnlyDictionary<int, TriggerStatus>
  GetStatuses()`, returning per-trigger-id status (aggregated across a trigger's
  extension-watchers: worst status wins) — `Healthy`, `Down`, `LastEventAt`/`LastScanAt`,
  `DetectionMode`. Thread-safe read of `_watchers` under the existing `_watchersLock`.
- A status column in `Components/Pages/FileTriggers.razor` (currently shows only DB-backed
  fields: Active, TriggerName, FolderPath, FileNameContains, FileTypes, QueueName, Priority,
  Business fields — no live state). Add a small badge/chip (green "Healthy" / red "Down" /
  gray "Unknown") plus a tooltip with last-seen timestamp and detection mode. Poll the status
  method periodically from the page (e.g. every 10-15s via a `PeriodicTimer` or MudBlazor
  `Timer`, consistent with a lightweight read-only refresh — no new backend push mechanism
  needed).

## Frontend (Settings.razor)

Extend the existing "File Watching" section (~lines 250-260):
- `MudSelect T="string" @bind-Value="_settings.FileWatchMethod"` with two items:
  `"FileSystemWatcher"` ("Real-time (FileSystemWatcher)") and `"Polling"` ("Polling (periodic
  folder scan)") — same idiom as the existing `GraphDetectionMode` select.
- Conditionally show `FilePollingIntervalInSeconds` as a `MudNumericField` only when
  `FileWatchMethod == "Polling"` (mirrors the existing `GraphDetectionMode`/
  `MailTriggerIntervalInSeconds` conditional block).
- Caption text: this is a global setting affecting all File Triggers; Polling trades
  near-instant detection for reliability at scale (useful when FileSystemWatcher stops
  detecting files on some network shares with 100+ triggers).

### Missing reload wiring — fix as part of this feature
`Settings.razor`'s `SaveAsync()` currently never calls `TriggerReloadChannel.RequestReload()`.
Inject `TriggerReloadChannel` and call `RequestReload()` after a successful save, so a method
or interval change takes effect live (consistent with how `FileWatcherService` already reacts
to reload signals for trigger CRUD changes).

## Staged Implementation

Each stage: branch off latest `main` → implement → `dotnet build` clean → manual smoke test
(via the `run` skill) → commit → merge to `main` → next stage's branch opens from the updated
`main`.

### Stage 1 — Data model + Settings UI (no behavior change)
**Branch:** `feature/file-watch-method-settings`
**Files:** `Data/Entities/OrchSettings.cs`, `Services/DatabaseMigrator.cs`,
`Components/Pages/Settings.razor`
**Commit theme:** "Add global file-watch method setting (FileSystemWatcher/Polling) — data
model and Settings UI only"
**Verify:**
- `dotnet build` clean.
- Run app; Settings shows the new dropdown + conditional interval field; save persists;
  Operator role sees it read-only; Admin can change it.
- Since `FileWatcherService` doesn't read the new field yet, this is additive/no-op for
  runtime behavior — confirm existing File Triggers still fire via FSW unchanged (regression
  check).
- Run `--migrate-db` against a copy of an existing SQLite DB; confirm new columns appear with
  correct defaults; confirm re-running is a no-op (idempotency).
**Merge:** to `main`.

### Stage 2 — Polling engine implementation (dormant until wired)
**Branch:** `feature/file-trigger-polling-engine`
**Files:** `Services/Background/FileWatcherService.cs` (`WatcherEntry.DetectionMode`, polling
loop, `SemaphoreSlim` gate, jitter, scan-and-compare logic reusing `OnFileEvent`)
**Commit theme:** "Implement folder-polling detection engine with bounded concurrency (not yet
wired to global setting)"
**Verify:**
- `dotnet build` clean.
- Exercise the polling loop directly against several local test folders (temporarily flip the
  branch condition locally during testing, revert before commit): files detected within the
  interval, dedup works (drop the same file twice, confirm debounce), semaphore caps
  concurrent scans (20+ dummy folders, log timestamps show staggered/bounded starts).
- Confirm the FSW path is untouched — no regression in default behavior.
**Merge:** to `main` (polling code present but not reachable via Settings yet).

### Stage 3 — Wiring, live reload, and final integration
**Branch:** `feature/file-trigger-polling-switch`
**Files:** `Services/Background/FileWatcherService.cs` (`LoadWatchersAsync` branches for real;
`DisposeWatchers` tears down polling CTS's), `Components/Pages/Settings.razor` (add
`TriggerReloadChannel.RequestReload()` call + any copy/bounds tweaks from Stage 2 learnings)
**Commit theme:** "Wire global FileWatchMethod setting to live-switch File Trigger detection
between FileSystemWatcher and Polling"
**Verify:**
- `dotnet build` clean.
- End-to-end: default FSW behavior unchanged → switch to Polling, save, confirm live teardown
  of FSW + polling loops start without app restart, file dropped is detected within the
  configured interval → switch back to FSW, confirm clean teardown/restart, no leaked
  watchers/loops.
- Concurrency/load check: many (10-20+, ideally near 100) local test folders in Polling mode;
  monitor CPU during scan cycles — no spike correlating with simultaneous scans (jitter
  working).
- Change `FilePollingIntervalInSeconds` while in Polling mode, save, confirm running loops
  pick up the new interval on next reload.
- Regression-check other background services (Mail polling, Graph subscriptions, Retention
  cleanup) unaffected by the `TriggerReloadChannel` broadcast triggered from Settings.
**Merge:** to `main` — feature is live/production-ready for detection switching.

### Stage 4 — Trigger status/health UI
**Branch:** `feature/file-trigger-status-ui`
**Files:** `Services/Background/FileWatcherService.cs` (add `GetStatuses()` query method),
`Components/Pages/FileTriggers.razor` (status column + periodic refresh)
**Commit theme:** "Show live Healthy/Down status per File Trigger, for both FileSystemWatcher
and Polling detection"
**Verify:**
- `dotnet build` clean.
- Run app; FileTriggers list shows status badges reflecting real state for both detection
  modes; kill/restore a watched folder's accessibility (e.g. rename it temporarily) and confirm
  the badge flips to Down and back; confirm polling-mode entries show `LastScanAt`-based status
  correctly.
- Confirm the periodic UI refresh doesn't introduce noticeable load (simple in-memory read, no
  new DB/SMB calls triggered by the UI poll itself).
**Merge:** to `main`.

## Risks / Notes Carried Forward
- Mode switches tear down in-flight `Task.Run(ProcessFileAsync)` calls' *parent* entries
  immediately, but since those calls are already detached (fire-and-forget), in-flight
  processing itself completes independently — this matches existing reload-on-trigger-change
  behavior, not a new risk.
- Detection latency under Polling is bounded by `FilePollingIntervalInSeconds` (30s default) —
  called out in the Settings UI copy so admins understand the tradeoff versus FSW's near-instant
  detection.
- Per-trigger override (mixing FSW and Polling per trigger) is explicitly out of scope — this
  plan treats the requirement as global-only, per the original ask.

## Critical Files
- `Services/Background/FileWatcherService.cs`
- `Data/Entities/OrchSettings.cs`
- `Services/DatabaseMigrator.cs`
- `Components/Pages/Settings.razor`
- `Components/Pages/FileTriggers.razor`
- `Services/TriggerReloadChannel.cs`
