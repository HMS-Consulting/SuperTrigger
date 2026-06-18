using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json.Linq;
using SuperTrigger.Web.Data;
using SuperTrigger.Web.Data.Entities;

namespace SuperTrigger.Web.Services.Auth;

public class MailOAuthService(
    IDbContextFactory<AppDbContext> dbFactory,
    AppSettingsService settingsService,
    IHttpClientFactory httpFactory,
    IMemoryCache pkceCache,
    TriggerReloadChannel reloadChannel,
    ILogger<MailOAuthService> logger)
{
    // Delegated Graph scopes — Mail.ReadWrite is superset of Mail.Read
    private const string Scope = "https://graph.microsoft.com/Mail.ReadWrite";

    // triggerId = 0 is reserved for global Settings auth
    public async Task<string> BuildGlobalAuthUrlAsync(string redirectUri)
    {
        var settings = await settingsService.GetAsync();
        if (string.IsNullOrWhiteSpace(settings.GraphClientId))
            throw new InvalidOperationException(
                "Client ID (App ID) is not configured. Set it in Settings → Microsoft Graph.");

        var tenantId = string.IsNullOrWhiteSpace(settings.GraphTenantId) ? "common" : settings.GraphTenantId;
        var verifier = GenerateCodeVerifier();
        var challenge = ComputeCodeChallenge(verifier);
        pkceCache.Set("pkce:0", verifier, TimeSpan.FromMinutes(15));

        return "https://login.microsoftonline.com/" + tenantId + "/oauth2/v2.0/authorize" +
               "?client_id=" + Uri.EscapeDataString(settings.GraphClientId) +
               "&response_type=code" +
               "&redirect_uri=" + Uri.EscapeDataString(redirectUri) +
               "&scope=" + Uri.EscapeDataString(Scope) +
               "&state=0" +
               "&response_mode=query" +
               "&prompt=select_account" +
               "&code_challenge=" + challenge +
               "&code_challenge_method=S256";
    }

    public async Task HandleGlobalCallbackAsync(string code, string redirectUri)
    {
        var settings = await settingsService.GetAsync();
        var tenantId = string.IsNullOrWhiteSpace(settings.GraphTenantId) ? "common" : settings.GraphTenantId;

        pkceCache.TryGetValue("pkce:0", out string? codeVerifier);
        pkceCache.Remove("pkce:0");

        if (string.IsNullOrEmpty(codeVerifier) && string.IsNullOrEmpty(settings.GraphClientSecret))
            throw new InvalidOperationException(
                "PKCE verifier expired (try signing in again). If the problem persists, add a Client Secret in Settings.");

        var formData = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = settings.GraphClientId,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["scope"] = Scope
        };
        if (!string.IsNullOrEmpty(codeVerifier))
            formData["code_verifier"] = codeVerifier;
        if (!string.IsNullOrEmpty(settings.GraphClientSecret))
            formData["client_secret"] = settings.GraphClientSecret;

        var client = httpFactory.CreateClient();
        var resp = await client.PostAsync(
            $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token",
            new FormUrlEncodedContent(formData));
        var body = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Token exchange failed [{resp.StatusCode}] (verifier={!string.IsNullOrEmpty(codeVerifier)}, secret={!string.IsNullOrEmpty(settings.GraphClientSecret)}): {body}");

        var json = JObject.Parse(body);
        var accessToken = json["access_token"]!.ToString();
        var refreshToken = json["refresh_token"]?.ToString();
        var expiresIn = json["expires_in"]?.Value<int>() ?? 3600;
        var upn = await GetUserUpnAsync(accessToken);

        await using var db = await dbFactory.CreateDbContextAsync();
        var s = await db.OrchSettings.FindAsync(1) ?? new OrchSettings { Id = 1 };
        s.OAuthGlobalAccessToken = accessToken;
        if (!string.IsNullOrEmpty(refreshToken)) s.OAuthGlobalRefreshToken = refreshToken;
        s.OAuthGlobalTokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn - 60);
        if (!string.IsNullOrEmpty(upn)) s.OAuthGlobalUserUpn = upn;
        db.OrchSettings.Update(s);
        await db.SaveChangesAsync();
        logger.LogInformation("Global OAuth2 authentication completed as {Upn}", upn);
    }

    public async Task<string?> GetValidGlobalAccessTokenAsync()
    {
        var settings = await settingsService.GetAsync();
        if (!string.IsNullOrEmpty(settings.OAuthGlobalAccessToken) &&
            settings.OAuthGlobalTokenExpiry.HasValue &&
            DateTime.UtcNow < settings.OAuthGlobalTokenExpiry.Value)
            return settings.OAuthGlobalAccessToken;
        return null;
    }

    public async Task<string> BuildAuthUrlAsync(int triggerId, string redirectUri)
    {
        var settings = await settingsService.GetAsync();
        var tenantId = await ResolveTenantIdAsync(triggerId, settings);
        var clientId = settings.GraphClientId;

        if (string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException(
                "Client ID (App ID) is not configured. Set it in Settings → Microsoft Graph.");

        var verifier = GenerateCodeVerifier();
        var challenge = ComputeCodeChallenge(verifier);
        pkceCache.Set($"pkce:{triggerId}", verifier, TimeSpan.FromMinutes(15));

        return "https://login.microsoftonline.com/" + tenantId + "/oauth2/v2.0/authorize" +
               "?client_id=" + Uri.EscapeDataString(clientId) +
               "&response_type=code" +
               "&redirect_uri=" + Uri.EscapeDataString(redirectUri) +
               "&scope=" + Uri.EscapeDataString(Scope) +
               "&state=" + triggerId +
               "&response_mode=query" +
               "&prompt=select_account" +
               "&code_challenge=" + challenge +
               "&code_challenge_method=S256";
    }

    public async Task HandleCallbackAsync(string code, string state, string redirectUri)
    {
        if (!int.TryParse(state, out var triggerId))
            throw new InvalidOperationException($"Invalid OAuth state: {state}");

        var settings = await settingsService.GetAsync();
        var tenantId = await ResolveTenantIdAsync(triggerId, settings);

        pkceCache.TryGetValue($"pkce:{triggerId}", out string? codeVerifier);
        pkceCache.Remove($"pkce:{triggerId}");

        if (string.IsNullOrEmpty(codeVerifier) && string.IsNullOrEmpty(settings.GraphClientSecret))
            throw new InvalidOperationException(
                "PKCE verifier expired (try signing in again). If the problem persists, add a Client Secret in Settings.");

        var formData = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = settings.GraphClientId,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["scope"] = Scope
        };

        if (!string.IsNullOrEmpty(codeVerifier))
            formData["code_verifier"] = codeVerifier;
        if (!string.IsNullOrEmpty(settings.GraphClientSecret))
            formData["client_secret"] = settings.GraphClientSecret;

        var client = httpFactory.CreateClient();
        var resp = await client.PostAsync(
            $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token",
            new FormUrlEncodedContent(formData));
        var body = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Token exchange failed [{resp.StatusCode}] (verifier={!string.IsNullOrEmpty(codeVerifier)}, secret={!string.IsNullOrEmpty(settings.GraphClientSecret)}): {body}");

        var json = JObject.Parse(body);
        var accessToken = json["access_token"]!.ToString();
        var refreshToken = json["refresh_token"]?.ToString();
        var expiresIn = json["expires_in"]?.Value<int>() ?? 3600;

        var upn = await GetUserUpnAsync(accessToken);
        await SaveTokensAsync(triggerId, accessToken, refreshToken, expiresIn, upn);
        logger.LogInformation("OAuth2 authentication completed for trigger {Id} as {Upn}", triggerId, upn);
    }

    public async Task<string?> GetValidAccessTokenAsync(MailTrigger trigger)
    {
        if (!string.IsNullOrEmpty(trigger.OAuthAccessToken) &&
            trigger.OAuthTokenExpiry.HasValue &&
            DateTime.UtcNow < trigger.OAuthTokenExpiry.Value)
            return trigger.OAuthAccessToken;

        if (string.IsNullOrEmpty(trigger.OAuthRefreshToken))
        {
            logger.LogWarning("Trigger [{Name}] has no refresh token — re-authentication required", trigger.TriggerName);
            return null;
        }

        return await RefreshTokenAsync(trigger);
    }

    private async Task<string?> RefreshTokenAsync(MailTrigger trigger)
    {
        try
        {
            var settings = await settingsService.GetAsync();
            var tenantId = string.IsNullOrWhiteSpace(trigger.AzureTenantId)
                ? (string.IsNullOrWhiteSpace(settings.GraphTenantId) ? "common" : settings.GraphTenantId)
                : trigger.AzureTenantId;

            var formData = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = settings.GraphClientId,
                ["refresh_token"] = trigger.OAuthRefreshToken!,
                ["scope"] = Scope
            };

            if (!string.IsNullOrEmpty(settings.GraphClientSecret))
                formData["client_secret"] = settings.GraphClientSecret;

            var client = httpFactory.CreateClient();
            var resp = await client.PostAsync(
                $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token",
                new FormUrlEncodedContent(formData));
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("Token refresh failed for trigger [{Name}] [{Status}]: {Body}",
                    trigger.TriggerName, resp.StatusCode, body);
                return null;
            }

            var json = JObject.Parse(body);
            var accessToken = json["access_token"]!.ToString();
            var newRefreshToken = json["refresh_token"]?.ToString();
            var expiresIn = json["expires_in"]?.Value<int>() ?? 3600;

            await SaveTokensAsync(trigger.Id, accessToken, newRefreshToken ?? trigger.OAuthRefreshToken, expiresIn, null);
            logger.LogInformation("Token refreshed for trigger [{Name}]", trigger.TriggerName);
            return accessToken;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Token refresh exception for trigger [{Name}]", trigger.TriggerName);
            return null;
        }
    }

    private async Task SaveTokensAsync(int triggerId, string accessToken,
        string? refreshToken, int expiresIn, string? upn)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var trigger = await db.MailTriggers.FindAsync(triggerId)
            ?? throw new InvalidOperationException($"Trigger {triggerId} not found");

        trigger.OAuthAccessToken = accessToken;
        if (!string.IsNullOrEmpty(refreshToken)) trigger.OAuthRefreshToken = refreshToken;
        trigger.OAuthTokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn - 60);

        // Fresh auth (upn is set) — store UPN and activate the trigger automatically
        if (!string.IsNullOrEmpty(upn))
        {
            trigger.OAuthUserUpn = upn;
            trigger.Active = true;
        }

        db.MailTriggers.Update(trigger);
        await db.SaveChangesAsync();

        // Notify background services to reload so polling starts immediately
        if (!string.IsNullOrEmpty(upn))
            reloadChannel.RequestReload();
    }

    private async Task<string> GetUserUpnAsync(string accessToken)
    {
        // Try /me first; fall back to decoding JWT claims
        try
        {
            var client = httpFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", accessToken);
            var resp = await client.GetAsync(
                "https://graph.microsoft.com/v1.0/me?$select=userPrincipalName,displayName");
            var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
            var upn = json["userPrincipalName"]?.ToString();
            if (!string.IsNullOrEmpty(upn)) return upn;
        }
        catch { /* fall through to JWT decode */ }

        return ExtractUpnFromJwt(accessToken);
    }

    private static string ExtractUpnFromJwt(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2) return "";
            var payload = parts[1];
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var json = JObject.Parse(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            return json["upn"]?.ToString()
                ?? json["preferred_username"]?.ToString()
                ?? json["email"]?.ToString()
                ?? "";
        }
        catch { return ""; }
    }

    private async Task<string> ResolveTenantIdAsync(int triggerId, OrchSettings settings)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var trigger = await db.MailTriggers.FindAsync(triggerId);
        if (trigger != null && !string.IsNullOrWhiteSpace(trigger.AzureTenantId))
            return trigger.AzureTenantId;

        return string.IsNullOrWhiteSpace(settings.GraphTenantId) ? "common" : settings.GraphTenantId;
    }

    // ─── Token clearing ─────────────────────────────────────────────────────

    public async Task ClearTriggerTokensAsync(int triggerId)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var trigger = await db.MailTriggers.FindAsync(triggerId);
            if (trigger != null)
            {
                trigger.OAuthAccessToken = null;
                trigger.OAuthRefreshToken = null;
                trigger.OAuthTokenExpiry = null;
                trigger.OAuthUserUpn = null;
                db.MailTriggers.Update(trigger);
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "Failed to clear trigger {Id} OAuth tokens", triggerId); }
    }

    public async Task ClearGlobalTokensAsync()
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var s = await db.OrchSettings.FindAsync(1);
            if (s != null)
            {
                s.OAuthGlobalAccessToken = null;
                s.OAuthGlobalRefreshToken = null;
                s.OAuthGlobalTokenExpiry = null;
                s.OAuthGlobalUserUpn = null;
                db.OrchSettings.Update(s);
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "Failed to clear global OAuth tokens"); }
    }

    // ─── PKCE helpers ────────────────────────────────────────────────────────

    private static string GenerateCodeVerifier()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    private static string ComputeCodeChallenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64UrlEncode(hash);
    }

    private static string Base64UrlEncode(byte[] input) =>
        Convert.ToBase64String(input).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
