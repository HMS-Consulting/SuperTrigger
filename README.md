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

All configuration (triggers, credentials, settings) is stored in a local SQLite database and managed through the built-in web UI.

---

## Prerequisites

| Requirement | Details |
|---|---|
| Windows Server (or Windows 10/11 Pro) | IIS-hosted deployment |
| IIS — Web Server role | Include the IIS Management Console feature |
| ASP.NET Core 8 Hosting Bundle | Installs the ASP.NET Core Module (ANCM) IIS uses to host the app — [download](https://dotnet.microsoft.com/download/dotnet/8.0) |
| UiPath Orchestrator | On-premises or Cloud |
| Microsoft Exchange | Exchange 2013 SP1 or later (EWS mode) |
| Azure AD App Registration | Required for Microsoft Graph / OAuth2 modes |

The Hosting Bundle must be (re)installed *after* the IIS role is enabled — if IIS wasn't present yet when it ran, it silently skips registering ANCM and the app pool will fail with `500.19`.

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

Run the MSI as Administrator. It asks for two choices:
- **Application Pool identity** — the built-in `ApplicationPoolIdentity`, or a specific `DOMAIN\user` + password
- **SSL certificate** — pick an existing certificate from the machine's store, or generate a new self-signed one

For unattended installs:
```powershell
msiexec /i HMS_SuperTriggerWebInstaller.msi /quiet /norestart /l*v install.log
```

Default result:

| Item | Value |
|---|---|
| IIS site name | `SuperTrigger.Web` |
| App pool name | `SuperTrigger.Web` |
| Physical path | `C:\inetpub\SuperTrigger.Web` |
| Binding | HTTPS only, port 443 — no port 80 listener |
| Database | `C:\ProgramData\HMS\SuperTriggerWeb\supertrigger.db` |

A default admin account (`admin` / `Admin123!`) is created on first run — **change the password immediately** in **Settings → Users & Access**.

> **Self-signed certificates are never automatically trusted.** Browsers will show an "unsecure connection" warning on *any* machine that hasn't explicitly imported that specific certificate into its Trusted Root store — including the server itself when browsing locally. For production, use a certificate issued by a real CA (internal AD CS or public) via the "existing certificate" option instead.

### Upgrading

1. Bump the `Version` attribute on `<Product>` in `CreateMSI.Web\Product.wxs` (keep the same `UpgradeCode`).

   Version format is **`YY.M.Build`** (calendar-based) — e.g. the first build in August 2026 is `26.8.1`, the second `26.8.2`, the first in September `26.9.1`. This is a deliberate workaround, not a style choice: MSI's `ProductVersion` field is packed as Major/Minor/Build with hard caps of 255/255/65535, so a full 4-digit year (`2026.8.1`) is rejected outright by `candle.exe` (`CNDL0242: Invalid product version`) — two-digit year fits comfortably until year 2255.

   Also update `<Version>` in `SuperTrigger.Web.csproj` to the same value — it's what the in-app **About** page displays, and it's easy for it to silently drift out of sync with the installer version otherwise.
2. Rebuild the MSI — this re-publishes the app with your latest code changes automatically.
3. Run the new MSI on the target server. Same `UpgradeCode` + higher `Version` means Windows Installer replaces the old version in one operation — **do not uninstall first**.

Upgrades skip the identity/certificate dialogs entirely and preserve whatever is already configured (including a `SpecificUser` app pool password, which IIS never exposes for re-reading anyway). Those choices are only asked again on a genuinely fresh install.

### Uninstalling

Via **Control Panel → Programs and Features** (listed as `HMS_SuperTrigger.Web`) is the reliable way. This removes the IIS website, application pool, and the bound SSL certificate registration, but **preserves**:
- The SQLite database at `C:\ProgramData\HMS\SuperTriggerWeb\`
- Any files IIS created at runtime that the installer never tracked (e.g. the ANCM `logs\` folder under the install directory)

> **Command-line uninstall pitfall:** `msiexec /x path\to\HMS_SuperTriggerWebInstaller.msi` only works if that exact `.msi` file is the one currently installed — every build gets a new internal ProductCode (`Id="*"`), so an MSI you rebuilt after installing will fail with error 1605/1603. Find the actual installed ProductCode instead:
> ```powershell
> $code = (Get-ItemProperty HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*, HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\* -ErrorAction SilentlyContinue |
>   Where-Object { $_.DisplayName -like "*SuperTrigger.Web*" }).PSChildName
> msiexec /x $code /quiet /norestart
> ```

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
| `AppIdGlobal` | Microsoft Graph | Webhook only | Azure AD app (client credentials) in global settings |
| `AppIdPerTrigger` | Microsoft Graph | Webhook only | Azure AD app (client credentials) per trigger |

**Webhook mode** (`AppIdGlobal`, `AppIdPerTrigger`, `OAuth2Interactive`) receives real-time push notifications from Microsoft Graph — no polling delay.  
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

All sensitive fields are encrypted in the SQLite database using **Windows DPAPI** (`ProtectedData`, `DataProtectionScope.LocalMachine`). Encryption and decryption are applied transparently via EF Core value converters — no changes are required in application code when reading or writing these fields.

| Table | Encrypted fields |
|---|---|
| `OrchSettings` | Password, AdPassword, GraphClientSecret, OAuthGlobalAccessToken, OAuthGlobalRefreshToken |
| `MailTriggers` | Password, GraphClientSecret, OAuthAccessToken, OAuthRefreshToken |
| `FileTriggers` | WatcherPassword |

**What this means in practice:**
- If the SQLite database file is copied off the server, the encrypted values cannot be read without access to the same Windows machine's DPAPI master key.
- `LocalUser` passwords are separately protected with BCrypt hashing (one-way) and are never decryptable.
- `GraphClientId`, `AzureTenantId`, and usernames/UPNs are not secrets and are stored as plain text.

**Backward compatibility:** The `Decrypt()` method returns the value unchanged if it was stored before encryption was introduced, so existing installations continue to work. Values are re-encrypted automatically on next save.

> **Note:** DPAPI `LocalMachine` scope ties the encrypted data to the host machine. If the application is migrated to a new server, all credentials must be re-entered in the Settings page.

---

## Logging

Logs are written via NLog. Configuration is in `nlog.config` in the application directory.  
A log of every queue item created (including errors) is also stored in the database and visible in the **Logs** page of the web UI.
