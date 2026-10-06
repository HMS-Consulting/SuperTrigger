# SuperTrigger.Web — Security Code Review & Threat Model

- **Date:** 2026-10-07
- **Scope:** `main` @ `f25a383` (ASP.NET Core Blazor Server app, IIS-hosted, SQLite/SQL Server, AD + local auth, Microsoft Graph/EWS, UiPath Orchestrator)
- **Method:** manual source review plus a few non-intrusive, unauthenticated GET/HEAD probes against the live install (no login attempted, no data changed).
- **Related issue:** #2

## 1. System overview and trust boundaries

| Boundary | Description |
|---|---|
| Browser → IIS/Kestrel | Blazor Server (SignalR) UI, Razor login pages, cookie auth (8h sliding) or Negotiate (Windows) |
| Internet/Graph → `/api/graph-notifications` | Anonymous webhook, called by Microsoft Graph |
| App → SQL (SQLite/SQL Server) | Holds triggers, settings, credentials, OAuth tokens (DPAPI-encrypted per field) |
| App → UiPath Orchestrator / Graph / EWS / AD | Outbound, using stored credentials |
| App → File shares | FileSystemWatcher/polling with optional stored watcher credentials |

Roles: `Admin`, `Operator`, `Viewer`. Local `admin` account (BCrypt) plus AD users/groups mapped in `AdPrincipals`.

**Assets:** Orchestrator credentials, Graph client secret and OAuth tokens (mailbox access), AD service account, SQL password, and the ability to enqueue Orchestrator work (which drives robots).

## 2. Findings summary

| ID | Severity | Title |
|---|---|---|
| F-01 | **High** | Graph webhook is anonymous and its `clientState` is the guessable trigger ID |
| F-02 | **High** | Default local admin `admin` / `Admin123!`, documented publicly, no forced change |
| F-03 | Medium | OAuth callback `state` is a bare integer: no CSRF/state binding, and no role check |
| F-04 | Medium | No brute-force protection or lockout on `/account/login` (local and AD) |
| F-05 | Medium | Open redirect via `returnUrl` on `/account/windows-login[-probe]` |
| F-06 | Medium | `OrchSettings.GraphPassword` is stored unencrypted in the DB |
| F-07 | Medium | Field encryption is DPAPI `LocalMachine` with static in-source entropy; decrypt failures silently fall back to plaintext |
| F-08 | Medium | Missing HTTP security headers (CSP, X-Frame-Options, X-Content-Type-Options, Referrer-Policy) |
| F-09 | Low | Logout is a state-changing GET |
| F-10 | Low | Local password change: no policy, no current-password check, hardcoded `admin` target |
| F-11 | Low | `TrustServerCertificate` defaults to `true` (SQL Server TLS not validated) |
| F-12 | Low | OAuth/token-exchange errors (including raw response body) are reflected in redirect URLs |
| F-13 | Low | Domain is discarded when mapping AD identities (`DOMAIN\user` → `user`) |
| F-14 | Info | Webhook `validationToken` reflected as `text/plain` (confirmed live) |
| F-15 | Info | Authorization is enforced mostly at page level; per-action checks rely on `AuthorizeView` |

## 3. Detailed findings

### F-01 — Unauthenticated Graph webhook with predictable `clientState` (High)
`Program.cs` maps `POST /api/graph-notifications` as `AllowAnonymous`. `GraphSubscriptionService.ProcessSingleNotificationAsync` treats `clientState` as the trigger ID (`int.TryParse`) and, if the trigger is active and webhook-based, fetches the message and **adds a queue item to Orchestrator**. Subscriptions are created with `clientState = triggerId`, so there is no secret. Anyone who can reach the URL can POST a notification with `clientState` `1`, `2`, … Impact is bounded because the message is re-fetched from Graph by ID (a random ID yields nothing), but a *known* real message ID can be replayed → duplicate queue items → duplicate robot runs, plus Graph quota burn and log noise.
**Fix:** generate a random per-subscription `clientState` (≥32 bytes), store it with the trigger, compare in constant time and ignore mismatches; deduplicate by message ID; optionally restrict by IP to Microsoft's ranges.

### F-02 — Well-known default admin credentials (High)
`AuthService.EnsureDefaultAdminExistsAsync` creates `admin` / `Admin123!` when no local user exists (called from `DatabaseMigrator`), and `README.md` documents the password. Nothing forces a change. The local admin bypasses AD and has the `Admin` role.
**Fix:** generate a random password at install time and show it once (or have the MSI prompt for it); add a `MustChangePassword` flag enforced after login; remove the password from the README.

### F-03 — OAuth callback lacks CSRF `state` and role check (Medium)
`BuildAuthUrlAsync` uses `state=<triggerId>` (`0` for global). The callback only requires an authenticated session. An attacker who gets a logged-in user to open `/account/mail-oauth-callback?code=<attacker code>&state=3` can bind **the attacker's mailbox tokens** to trigger 3 (which is then set `Active=true`), so mail from the attacker's mailbox would flow into Orchestrator queues. PKCE only helps if a flow for that trigger was started within the last 15 minutes; when a client secret is configured, the exchange proceeds even without a verifier. The callback also has no role check: any authenticated user, including `Viewer`, can complete flows, and the error path calls `ClearTriggerTokensAsync` for an arbitrary ID (token wipe → trigger outage).
**Fix:** use a random, single-use `state` bound to the user session and trigger ID; always require PKCE; require `Admin,Operator` on the callback; don't clear tokens on forged error callbacks.

### F-04 — No brute-force protection on login (Medium)
`LoginModel.OnPostAsync` has no throttling, lockout or CAPTCHA, for both BCrypt local login and AD `ValidateCredentials`. AD attempts can also lock out real domain accounts (denial of service). Failed logins are written only to the application log, not to the audit table.
**Fix:** per-IP and per-username rate limiting (`AddRateLimiter`) with back-off; audit failed logins.

### F-05 — Open redirect on Windows-login endpoints (Medium)
`ctx.Response.Redirect(returnUrl ?? "/")` and `Results.Ok(new { redirect = returnUrl ?? "/" })` accept any URL, e.g. `/account/windows-login?returnUrl=https://evil.example`. (The Razor login page correctly uses `LocalRedirect`.)
**Fix:** accept only local paths (`StartsWith('/')`, not `//` or `/\`), otherwise `/`.

### F-06 — `GraphPassword` stored in plaintext (Medium)
`AppDbContext` applies the DPAPI converter to `Password`, `AdPassword`, `GraphClientSecret` and OAuth tokens, but not to `OrchSettings.GraphPassword` (global Graph username/password). Anyone with DB read access or a leaked backup obtains a mailbox password.
**Fix:** add `.HasConversion(enc)` for `GraphPassword` (the decrypt fallback makes existing rows compatible; re-save to encrypt). Prefer moving off the password grant entirely.

### F-07 — Weak key management for field encryption (Medium)
`FieldEncryption` uses `DataProtectionScope.LocalMachine` with a constant entropy string embedded in the source. Any process or user on the server can decrypt every secret; the entropy adds no real protection. `Decrypt` swallows all exceptions and returns the input unchanged, so corrupted or foreign-machine ciphertext would be used as if it were the password, and there is no version marker (unlike `SecretProtector`'s `DPAPI:` prefix). The `appsettings.json` DB password (`SecretProtector`) has the same LocalMachine design.
**Fix:** use ASP.NET Core Data Protection with keys persisted to a file ACL'd to the app-pool identity, or `CurrentUser` scope for the pool identity; add a versioned prefix; log decrypt failures. Restrict NTFS ACLs on the DB file and `appsettings.json` to the pool identity and administrators.

### F-08 — Missing security headers (Medium)
Live `HEAD /` returned only `Strict-Transport-Security` (`max-age=2592000`, 30 days). No `Content-Security-Policy`, `X-Frame-Options`/`frame-ancestors`, `X-Content-Type-Options` or `Referrer-Policy`. `X-Powered-By: ASP.NET` and `Server: Microsoft-IIS/10.0` disclose the stack. The admin UI and OAuth flow are clickjackable.
**Fix:** add `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: same-origin`, a CSP (Blazor needs `'self'` plus `wss:`; MudBlazor needs inline styles), HSTS ≥ 1 year; remove `X-Powered-By`.

### F-09 — Logout via GET (Low)
`LogoutModel.OnGetAsync` signs out on GET, so a third-party page can log users out (`<img src="/account/logout">`).
**Fix:** POST with antiforgery.

### F-10 — Password change weaknesses (Low)
`Users.razor` calls `ChangePasswordAsync("admin", …)`: no minimum length/complexity, no current-password re-authentication, hardcoded target (any `Admin`, including AD admins, can reset the local admin). Existing sessions stay valid.
**Fix:** require the current password, enforce a policy (≥12 chars), and invalidate other sessions.

### F-11 — SQL Server TLS validation disabled by default (Low)
`DbConfig.TrustServerCertificate` defaults to `true`, and the installer passes `true` unless `SQLTRUSTCERT == "0"`. This permits MITM between app and SQL Server.
**Fix:** default to `false`; document installing the CA certificate.

### F-12 — Verbose OAuth errors (Low)
`MailOAuthCallback` redirects with `auth_error=<exception message>`; the message includes the full Azure AD token-endpoint response body. Token-refresh failures also log response bodies. `error_description` from the query string is user-controlled (Blazor encodes output by default; keep it that way).
**Fix:** show a generic message to the user; keep detail in logs only.

### F-13 — AD identity matching ignores the domain (Low)
`username.Split('\\')[1]` / `Split('@')[0]` drops the domain/UPN suffix and `AdPrincipals` matches on the bare `Identifier`. In multi-domain or trusted-forest setups, `OTHERDOM\jsmith` receives the role assigned to `jsmith`.
**Fix:** store and compare `DOMAIN\sAMAccountName` or the SID.

### F-14 — Reflected webhook validation token (Info)
`GET /api/graph-notifications?validationToken=<b>x` returns the input verbatim as `text/plain` (confirmed live; HTTP 200). Not XSS because of the content type, but without `nosniff` (F-08) old browsers may sniff.
**Fix:** `X-Content-Type-Options: nosniff`; cap the length.

### F-15 — Authorization granularity (Info)
Pages use `[Authorize(Roles=…)]`; write controls are hidden with `AuthorizeView`, but handlers (save settings, add AD principal, change password) do not re-check the role server-side. `Home`, `Logs`, `MailTriggers` and `FileTriggers` are readable by any authenticated role including `Viewer`, and they expose UNC paths and account names. `Settings` and `Users` are viewable by `Operator`.
**Fix:** use policy-based authorization (`AdminOnly`) and re-check inside mutating methods.

## 4. Threat model (STRIDE, abbreviated)

| Threat | Entry point | Mitigations present | Gaps |
|---|---|---|---|
| **S**poofing: attacker logs in as admin | `/account/login` | BCrypt, AD validation, secure cookie, HTTPS redirect | F-02, F-04 |
| **S**poofing: forged Graph notification | `/api/graph-notifications` | Message re-fetched from Graph | F-01 |
| **T**ampering: forged OAuth callback | `/account/mail-oauth-callback` | PKCE cache, auth required | F-03 |
| **T**ampering: DB edit | DB file / SQL | Field-level DPAPI encryption | F-06, F-07, file ACLs |
| **R**epudiation | Admin actions | `AuditLogs` for settings/user changes | Failed logins and OAuth events not audited; 14-day default retention |
| **I**nfo disclosure: secrets | DB, `appsettings.json`, logs | DPAPI (LocalMachine) | F-06, F-07, F-12 |
| **I**nfo disclosure: headers/stack | HTTP responses | HSTS | F-08 |
| **D**oS: login flood / AD lockout | Login | None | F-04; webhook flood (F-01) |
| **E**oP: Viewer → higher privilege | Blazor handlers, OAuth callback | Page-level roles | F-03, F-15 |
| EoP: cross-domain name collision | AdPrincipals | Case-insensitive match | F-13 |

## 5. Positive observations
- Local passwords hashed with BCrypt; auth cookie is `Secure` outside Development, `HttpOnly` by default, 8 h expiry.
- HTTPS redirect and HSTS; forwarded headers limited to `X-Forwarded-For/Proto` (default trust is loopback only).
- EF Core used throughout (parameterised queries); no raw SQL string concatenation found.
- The Razor login page uses antiforgery (default for Razor Pages) and `LocalRedirect`.
- OAuth uses authorization-code + PKCE (S256) with a random verifier.
- Secrets (DB password, tokens, client secret, Orchestrator/AD passwords) are encrypted at rest; `DbConfig.Describe()` avoids logging passwords.
- Live probe: `HEAD /settings` and the Windows-login endpoints returned no content to an unauthenticated client.

## 6. Recommended remediation order
1. F-01 random `clientState`; F-02 default admin; F-05 open redirect (small, quick).
2. F-03 OAuth state + role check; F-04 rate limiting; F-06 encrypt `GraphPassword`.
3. F-08 security headers; F-07 key management and ACLs; F-09/F-10 hygiene.
4. F-11–F-13, F-15 hardening.

## 7. Limitations
Review was static plus unauthenticated probes of one install. Not covered: authenticated testing, NuGet dependency CVE scan (run `dotnet list package --vulnerable`), the WiX installer and custom actions, file-trigger credential handling, and load testing. Re-test after fixes.
