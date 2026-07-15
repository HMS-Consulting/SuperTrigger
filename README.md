# SuperTrigger.Web

A .NET 8 Blazor Server application that monitors **email inboxes** and **file system folders** and pushes queue items to [UiPath Orchestrator](https://docs.uipath.com/orchestrator) to trigger RPA processes automatically.

---

## Table of Contents

- [Overview](#overview)
- [Prerequisites](#prerequisites)
- [Installation](#installation)
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
| .NET 8 Runtime | Windows, x64 |
| UiPath Orchestrator | On-premises or Cloud |
| Windows Server | Required for Windows Authentication and file-system impersonation |
| Microsoft Exchange | Exchange 2013 SP1 or later (EWS mode) |
| Azure AD App Registration | Required for Microsoft Graph / OAuth2 modes |

---

## Installation

1. Publish the application:
   ```
   dotnet publish -c Release -r win-x64 --self-contained false
   ```
2. Deploy the published output to the target server (e.g. `C:\HMS\SuperTriggerWeb`).
3. Run as a Windows Service or IIS application pool (recommended: run as a domain service account).
4. On first startup, the database is created automatically at:
   ```
   C:\ProgramData\HMS\SuperTriggerWeb\supertrigger.db
   ```
5. A default admin account (`admin` / `Admin1234!`) is created on first run — **change the password immediately**.

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
| `filePath` | Full path to the detected file |

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
