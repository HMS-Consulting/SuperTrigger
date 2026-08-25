# SuperTrigger.Web

A .NET 8 Blazor Server application that monitors **email inboxes** and **file system folders** and pushes queue items to [UiPath Orchestrator](https://docs.uipath.com/orchestrator) to trigger RPA processes automatically.

---

## Table of Contents

- [Overview](#overview)
- [Prerequisites](#prerequisites)
- [Installation](#installation)
  - [Building the installer](#building-the-installer)
  - [Installing](#installing)
  - [Upgrading](#upgrading)
  - [Uninstalling](#uninstalling)
- [Database](#database)
  - [Choosing a provider during installation](#choosing-a-provider-during-installation)
  - [appsettings.json](#appsettingsjson)
  - [Switching between SQLite and SQL Server](#switching-between-sqlite-and-sql-server)
  - [Setting up the database by hand](#setting-up-the-database-by-hand)
- [Configuration](#configuration)
  - [UiPath Orchestrator](#uipath-orchestrator)
  - [Mail Triggers](#mail-triggers)
  - [File Triggers](#file-triggers)
  - [Microsoft Graph (Webhooks)](#microsoft-graph-webhooks)
- [Authentication](#authentication)
- [Trigger Types](#trigger-types)
  - [Mail Triggers](#mail-triggers-1)
  - [File Triggers](#file-triggers-1)
- [Queue Item Payload](#queue-item-payload)
- [Security — Credential Storage](#security--credential-storage)
- [Logging](#logging)

---

## Overview

SuperTrigger.Web replaces manual polling scripts with a managed web application that:

1. Listens for new emails (via Exchange Web Services or Microsoft Graph)
2. Watches folders for new or renamed files
3. Creates a queue item in the appropriate UiPath Orchestrator queue when a trigger fires

All configuration (triggers, credentials, settings) is stored in a database — either a local SQLite file or a Microsoft SQL Server database, chosen during installation (see [Database](#database)) — and managed through the built-in web UI.

---

## Prerequisites

| Requirement | Details |
|---|---|
| Windows Server (or Windows 10/11 Pro) | IIS-hosted deployment |
| IIS — Web Server role | Include the IIS Management Console feature |
| ASP.NET Core 8 Hosting Bundle | Installs the ASP.NET Core Module (ANCM) IIS uses to host the app, plus the `Microsoft.NETCore.App` / `Microsoft.AspNetCore.App` runtimes — [download](https://dotnet.microsoft.com/download/dotnet/8.0) |
| .NET 8 Desktop Runtime (x64) | `SuperTrigger.Web.runtimeconfig.json` also asks for `Microsoft.WindowsDesktop.App`, which the Hosting Bundle does **not** ship — [download](https://dotnet.microsoft.com/download/dotnet/8.0) |
| UiPath Orchestrator | On-premises or Cloud |
| Microsoft Exchange | Exchange 2013 SP1 or later (EWS mode) |
| Azure AD App Registration | Required for Microsoft Graph / OAuth2 modes |

The Hosting Bundle must be (re)installed *after* the IIS role is enabled — if IIS wasn't present yet when it ran, it silently skips registering ANCM and the app pool will fail with `500.19`.

The MSI detects and installs both runtimes itself (PrerequisitesDlg → `EnsurePrerequisites`), so this table only matters for a manual deployment. Verify with `dotnet --list-runtimes`: all three of `Microsoft.NETCore.App 8.x`, `Microsoft.AspNetCore.App 8.x` and `Microsoft.WindowsDesktop.App 8.x` must be listed. If `Microsoft.WindowsDesktop.App` is absent, the app's own apphost exits with `0x80008096` (`-2147450730`, *FrameworkMissingFailure*) before any managed code runs — during installation that surfaces as a failure of the database setup step, which is misleading: the database is fine, the runtime is missing.

`Microsoft.WindowsDesktop.App` is not required by anything in this project directly; it is pulled in transitively because the `Mail` and `HmsTeam.Shared` project references are built with `<UseWpf>true</UseWpf>`. Dropping it from those two shared libraries (they are consumed by eight sibling UiPath activity projects) would remove the requirement, but that change has to be validated against those consumers first.

---

## Installation

The app ships as an MSI (`HMS_SuperTriggerWebInstaller.msi`, built from the sibling `CreateMSI.Web` project) that installs the published files, creates the IIS Application Pool + Website, and binds an SSL certificate — no manual IIS configuration needed.

### Building the installer

```powershell
"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe" CreateMSI.Web.wixproj -t:Build -p:Configuration=Release
```
This automatically runs `dotnet publish` for `SuperTrigger.Web` (framework-dependent) and produces:
```
CreateMSI.Web\bin\Release\HMS_SuperTriggerWebInstaller.msi
```
Requires WiX Toolset v3.11+ on the **build** machine only — not on the target server.

### Installing

Run the MSI as Administrator. It asks for:
- **Database** — a SQLite file (and its path), or a SQL Server connection (see [Database](#database))
- **Application Pool identity** — the built-in `ApplicationPoolIdentity`, or a specific `DOMAIN\user` + password
- **SSL certificate** — pick an existing certificate from the machine's store, or generate a new self-signed one

For unattended installs:
```powershell
msiexec /i HMS_SuperTriggerWebInstaller.msi /quiet /norestart /l*v install.log
```
Any of the database properties can be set on the command line for an unattended install, e.g.:
```powershell
msiexec /i HMS_SuperTriggerWebInstaller.msi /quiet /norestart /l*v install.log `
  DBPROVIDER=SqlServer SQLSERVER=sql01 SQLDATABASE=SuperTriggerWeb SQLAUTHMODE=Windows
```

Default result:

| Item | Value |
|---|---|
| IIS site name | `SuperTrigger.Web` |
| App pool name | `SuperTrigger.Web` |
| Physical path | `C:\inetpub\SuperTrigger.Web` |
| Binding | HTTPS only, port 443 — no port 80 listener |
| Database | SQLite at `C:\ProgramData\HMS\SuperTriggerWeb\supertrigger.db` |

A default admin account (`admin` / `Admin123!`) is created on first run — **change the password immediately** in **Settings → Users & Access**.

> **Self-signed certificates are never automatically trusted.** Browsers will show an "unsecure connection" warning on *any* machine that hasn't explicitly imported that specific certificate into its Trusted Root store — including the server itself when browsing locally. For production, use a certificate issued by a real CA (internal AD CS or public) via the "existing certificate" option instead.

### Upgrading

1. Bump the `Version` attribute on `<Product>` in `CreateMSI.Web\Product.wxs` (keep the same `UpgradeCode`).

   Version format is **`YY.M.Build`** (calendar-based) — e.g. the first build in August 2026 is `26.8.1`, the second `26.8.2`, the first in September `26.9.1`. This is a deliberate workaround, not a style choice: MSI's `ProductVersion` field is packed as Major/Minor/Build with hard caps of 255/255/65535, so a full 4-digit year (`2026.8.1`) is rejected outright by `candle.exe` (`CNDL0242: Invalid product version`) — two-digit year fits comfortably until year 2255.

   Also update `<Version>` in `SuperTrigger.Web.csproj` to the same value — it's what the in-app **About** page displays, and it's easy for it to silently drift out of sync with the installer version otherwise.
2. Rebuild the MSI — this re-publishes the app with your latest code changes automatically.
3. Run the new MSI on the target server. Same `UpgradeCode` + higher `Version` means Windows Installer replaces the old version in one operation — **do not uninstall first**.

Upgrades skip the identity/certificate dialogs entirely and preserve whatever is already configured (including a `SpecificUser` app pool password, which IIS never exposes for re-reading anyway). Those choices are only asked again on a genuinely fresh install.

The database dialogs *are* shown on an upgrade, prefilled with the settings the existing installation is using — clicking straight through keeps the same database and just brings its schema up to date. Changing them there moves the installation to a different database, and the installer then asks what to do with the existing data (see [Switching between SQLite and SQL Server](#switching-between-sqlite-and-sql-server)).

### Uninstalling

Via **Control Panel → Programs and Features** (listed as `HMS_SuperTrigger.Web`) is the reliable way. This removes the IIS website, application pool, and the bound SSL certificate registration, but **preserves**:
- The database — the SQLite file at `C:\ProgramData\HMS\SuperTriggerWeb\`, or the SQL Server database, which is never dropped
- Any files IIS created at runtime that the installer never tracked (e.g. the ANCM `logs\` folder under the install directory)

> **Command-line uninstall pitfall:** `msiexec /x path\to\HMS_SuperTriggerWebInstaller.msi` only works if that exact `.msi` file is the one currently installed — every build gets a new internal ProductCode (`Id="*"`), so an MSI you rebuilt after installing will fail with error 1605/1603. Find the actual installed ProductCode instead:
> ```powershell
> $code = (Get-ItemProperty HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*, HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\* -ErrorAction SilentlyContinue |
>   Where-Object { $_.DisplayName -like "*SuperTrigger.Web*" }).PSChildName
> msiexec /x $code /quiet /norestart
> ```

---

## Database

The application runs on either of two providers, with the same schema and the same feature set:

| Provider | When to use it |
|---|---|
| **SQLite** (default) | Single server, no external dependencies. One file, backed up by copying it. |
| **Microsoft SQL Server** | An existing SQL Server is already backed up/maintained centrally, or company policy requires the data to live there. SQL Server 2016 or later. |

Neither the app nor the installer requires the other provider's server to be present — the choice is made per installation.

### Choosing a provider during installation

The installer collects everything needed and hands it to the app's own setup mode, which creates or upgrades the schema *before* the website starts serving traffic. For SQL Server it asks for:

| Field | Installer property | Notes |
|---|---|---|
| Server | `SQLSERVER` | Host name or IP |
| Port | `SQLPORT` | Optional — omit for the default 1433 |
| Instance | `SQLINSTANCE` | Optional — e.g. `SQLEXPRESS`. Can be combined with a port |
| Database | `SQLDATABASE` | Created during installation if it does not exist yet |
| Authentication | `SQLAUTHMODE` | `Windows` (the IIS application pool identity) or `Sql` (SQL login) |
| Login / Password | `SQLUSER` / `SQLPASSWORD` | SQL authentication only |
| Trust server certificate | `SQLTRUSTCERT` | `1` (default) accepts a self-signed SQL Server certificate; the connection is encrypted either way |

**Test Connection** on that screen connects to `master` on the given server, so it reports a working server and credentials even when the database itself doesn't exist yet, and tells you whether it will be created or upgraded.

Two things to get right with **Windows authentication**:
- The installer runs the setup step as `LOCAL SYSTEM`, i.e. as the *machine account* (`DOMAIN\SERVERNAME$`). That account needs enough rights to create the database (or the database must already exist, in which case `db_owner` on it is enough).
- At runtime the app connects as the **IIS application pool identity**. With the default `ApplicationPoolIdentity` that is also the machine account; with a specific domain user it is that user. Whichever it is needs `db_datareader`, `db_datawriter` and `db_ddladmin` on the database — or simply `db_owner`.

### appsettings.json

The choice is recorded in `appsettings.json` in the install directory (`C:\inetpub\SuperTrigger.Web` by default), and that file is what the app reads at startup:

```jsonc
"Database": {
  "Provider": "SqlServer",              // "Sqlite" (default) or "SqlServer"
  "Path": "C:\\ProgramData\\HMS\\SuperTriggerWeb\\supertrigger.db",   // SQLite only
  "SqlServer": {
    "ConnectionString": "",             // set this to bypass every field below
    "Server": "sql01",
    "Port": "",
    "Instance": "",
    "Database": "SuperTriggerWeb",
    "AuthMode": "Windows",              // or "Sql"
    "Username": "",
    "Password": "DPAPI:AQAAAN…",        // DPAPI-encrypted; plaintext is also accepted
    "Encrypt": true,
    "TrustServerCertificate": true,
    "ConnectTimeoutSeconds": 30
  }
}
```

- `ConnectionString`, when non-empty, is used verbatim and every other SQL Server field is ignored — the escape hatch for anything the fields above can't express (failover partners, `MultiSubnetFailover`, Azure AD authentication, …).
- `Password` is written DPAPI-encrypted (see [Security — Credential Storage](#security--credential-storage)). A plaintext value put there by hand also works, which is the supported way to fix a password without re-running the installer.
- Editing this file requires an app pool recycle (or `iisreset`) to take effect. The app does not migrate anything at startup: if it points at a database that doesn't exist or is a schema version behind, it logs the reason and exits rather than starting up half-working.

### Switching between SQLite and SQL Server

Run the installer for a newer version and change the database on the **Database** screens. Because the location differs from what the installation is using, an extra screen asks what to do with the existing data:

- **Copy it into the new database** (default) — triggers, users, AD principals, settings and logs are read from the old database and written to the new one. The copy runs through the same application model, so the encrypted fields are decrypted from the old database and re-encrypted into the new one; primary keys are preserved.
- **Start with a new, empty database** — only the default `admin` account and default settings are created.

For an unattended upgrade the same choice is the `DBDATAACTION` property (`Copy`, the default, or `Fresh`).

Either way **the old database is left exactly as it was** — no file is deleted, no SQL Server database is dropped. To go back, point `appsettings.json` at it again and recycle the app pool.

Details worth knowing:
- Copying skips any table that already has rows in the target, so re-running an upgrade cannot duplicate data. The single settings row is the exception: it is always overwritten from the old database.
- The old database is brought up to the current schema before being read, so a database several versions behind can still be migrated in one step.
- Both directions work — SQLite → SQL Server and SQL Server → SQLite.
- Copying happens on the application server, which is where the DPAPI keys for the encrypted fields live. Moving data between *machines* is not what this does; credentials would have to be re-entered (see the DPAPI note under [Security — Credential Storage](#security--credential-storage)).

### Setting up the database by hand

The installer runs the same command any administrator can run from the install directory:

```powershell
# SQLite: create or upgrade a database file
.\SuperTrigger.Web.exe --migrate-db "C:\ProgramData\HMS\SuperTriggerWeb\supertrigger.db"

# Anything else: describe the target in a JSON file
.\SuperTrigger.Web.exe --setup-db C:\Temp\dbsetup.json
```

```jsonc
// C:\Temp\dbsetup.json
{
  "Provider": "SqlServer",
  "Server": "sql01",
  "Database": "SuperTriggerWeb",
  "AuthMode": "Sql",
  "Username": "supertrigger",
  "Password": "…",                        // plaintext here; written back encrypted
  "TrustServerCertificate": true,
  "DataAction": "Copy",                   // or "Fresh"
  "PreviousConfigFile": "C:\\inetpub\\SuperTrigger.Web\\appsettings.json"
}
```

Both forms create/upgrade the schema, then update `appsettings.json` next to the executable. `PreviousConfigFile` points at an `appsettings.json` describing the database to copy *from* — omit it when there is nothing to migrate. Progress and any error go to standard output, and the exit code is `0` only on success.

SQL Server schema upgrades are applied by reconciling the live schema against the application model (create missing tables, add missing columns and indexes) rather than from a migration history table, so an existing SQL Server database can be upgraded from any earlier version of this product. Nothing is ever dropped or retyped.

---

## Configuration

All settings are managed in the **Settings** page of the web UI.

### UiPath Orchestrator

| Field | Description |
|---|---|
| Orchestrator URL | Base URL, e.g. `https://orchestrator.company.com` |
| Authentication | **UserPass** — tenant / username / password; **ExternalApp** — OAuth2 client credentials |
| SSO | Use Windows Authentication of the service account |
| Main Folder | Root folder name in Orchestrator (default: `Root`) |
| Poll Interval | How often mail triggers are polled (minutes, default: `0.5`) |
| Same-File Interval | Debounce window for duplicate file events (seconds, default: `20`) |

### Mail Triggers

Each mail trigger defines:
- Which mailbox and folder to monitor
- Optional filters: subject, sender, body text, attachment type
- Which Orchestrator queue to push items to

### File Triggers

Each file trigger defines:
- The folder path to watch
- File extensions to match (comma-separated, e.g. `xlsx,pdf`)
- Optional filename prefix filter
- Optional Windows credentials for network shares
- Which Orchestrator queue to push items to

### Microsoft Graph (Webhooks)

To use Graph-based mail triggers (AppIdGlobal, AppIdPerTrigger, OAuth2Interactive), set the following in **Settings → Microsoft Graph**:

| Field | Description |
|---|---|
| Tenant ID | Azure AD tenant ID |
| Client ID | App registration Client ID |
| Client Secret | App registration Client Secret (optional for PKCE flows) |
| Public Base URL | Publicly reachable HTTPS URL of this server (e.g. `https://supertrigger.company.com`). Required for webhooks — must not be `localhost`. |

The application registers Microsoft Graph change notification subscriptions on startup and renews them automatically every hour.

---

## Authentication

The web UI supports three login methods:

| Method | Description |
|---|---|
| **Local account** | Username/password stored in the local database (BCrypt hashed) |
| **Windows SSO** | NTLM/Kerberos via Negotiate — available on domain-joined clients |
| **AD user** | Active Directory users explicitly added in the **Users** page |

Sessions use secure cookies and expire after 8 hours.

---

## Trigger Types

### Mail Triggers

Supported authentication modes:

| Mode | Protocol | Mechanism | Requires |
|---|---|---|---|
| `Interactive` | Exchange Web Services (EWS) | Polling | Exchange server + credentials |
| `OAuth2Interactive` | Microsoft Graph | Polling or Webhook | Azure AD app + delegated user sign-in |
| `AppIdGlobal` | Whatever Settings → Mail's Authentication Mode is (Graph or EWS) | Follows the global mode | Nothing per-trigger — configured once in Settings → Mail |
| `AppIdPerTrigger` | Microsoft Graph | Webhook only | Azure AD app (client credentials) per trigger |

`AppIdGlobal` ("Use Global Setting") is not a distinct protocol — it delegates entirely to
whichever Authentication Mode is currently set in Settings → Mail, including Interactive/EWS.

**Webhook mode** (`AppIdGlobal`/`AppIdPerTrigger`/`OAuth2Interactive`, when the effective mode uses Microsoft Graph) receives real-time push notifications from Microsoft Graph — no polling delay.  
**Interactive mode** polls on the configured interval (default: every 30 seconds).

Available filters (all case-insensitive, optional):
- Subject contains
- From (semicolon-separated list)
- Body contains
- Attachment type

### File Triggers

Uses `FileSystemWatcher` with debouncing (configurable `SameFileIntervalInSeconds`) to prevent duplicate events.  
Supports Windows impersonation to access network shares under a different account.  
Temporary Office files (`~$` prefix) are automatically ignored.

---

## Queue Item Payload

Every queue item added to Orchestrator follows this structure:

```json
{
  "itemData": {
    "Name": "<QueueName>",
    "Priority": "Normal",
    "Reference": "<auto-generated>",
    "SpecificContent": { ... }
  }
}
```

**Mail trigger** specific content:

| Field | Description |
|---|---|
| `TriggerName` | Name of the trigger that fired |
| `MailId` | Message ID (Graph) or UID (EWS) |
| `MailInternetUID` | Internet Message-ID header |
| `MailFolder` | Folder that was monitored |
| `MailSharedBox` | Shared mailbox UPN (if applicable) |
| `Sender` | Sender email address |
| `Subject` | Email subject |
| `DateTimeReceived` | Date/time in `dd/MM/yyyy HH:mm:ss` format |
| `HasAttachments` | `True` / `False` |
| `IsMeetingRequest` / `IsMeetingResponse` / `IsMeetingCancellation` | Meeting type flags |
| `AppointmentId`, `MeetingStart`, `MeetingEnd`, … | Meeting details (when applicable) |

**File trigger** specific content:

| Field | Description |
|---|---|
| `FilePath` | Full path to the detected file |

---

## Security — Credential Storage

All sensitive fields are encrypted in the database — SQLite file or SQL Server alike — using **Windows DPAPI** (`ProtectedData`, `DataProtectionScope.LocalMachine`). Encryption and decryption are applied transparently via EF Core value converters — no changes are required in application code when reading or writing these fields.

| Table | Encrypted fields |
|---|---|
| `OrchSettings` | Password, AdPassword, GraphClientSecret, OAuthGlobalAccessToken, OAuthGlobalRefreshToken |
| `MailTriggers` | Password, GraphClientSecret, OAuthAccessToken, OAuthRefreshToken |
| `FileTriggers` | WatcherPassword |

The SQL Server password in `appsettings.json` is protected the same way — DPAPI, `LocalMachine` scope — and is recognisable by its `DPAPI:` prefix. The installer encrypts it before writing it anywhere, so the plaintext never reaches disk, and it is marked as a hidden installer property so it does not appear in the MSI log. A plaintext value written into that field by hand is still accepted, which is how a password can be corrected without re-running the installer.

**What this means in practice:**
- If the database (SQLite file, or a SQL Server backup) is copied off the server, the encrypted values cannot be read without access to the same Windows machine's DPAPI master key. That also means a single SQL Server database cannot be shared by app instances on *different* machines — each machine could only read the credentials it wrote itself.
- `LocalUser` passwords are separately protected with BCrypt hashing (one-way) and are never decryptable.
- `GraphClientId`, `AzureTenantId`, and usernames/UPNs are not secrets and are stored as plain text.

**Backward compatibility:** The `Decrypt()` method returns the value unchanged if it was stored before encryption was introduced, so existing installations continue to work. Values are re-encrypted automatically on next save.

> **Note:** DPAPI `LocalMachine` scope ties the encrypted data to the host machine. If the application is migrated to a new server, all credentials must be re-entered in the Settings page — including the SQL Server password in `appsettings.json`, which the new machine cannot decrypt (replace it with the plaintext password, or re-run the installer there).

---

## Logging

Logs are written via NLog. Configuration is in `nlog.config` in the application directory.  
A log of every queue item created (including errors) is also stored in the database and visible in the **Logs** page of the web UI.
