using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace SuperTrigger.Web.Services.Graph;

public record MailFolderInfo(string Id, string DisplayName, int ChildFolderCount);

public class GraphMailService(IHttpClientFactory httpFactory, ILogger<GraphMailService> logger)
{
    private const string GraphBase = "https://graph.microsoft.com/v1.0";

    private readonly Dictionary<string, (string Token, DateTime Expiry)> _tokenCache = new();
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private static readonly HashSet<string> WellKnownFolders = new(StringComparer.OrdinalIgnoreCase)
        { "inbox", "sentitems", "deleteditems", "drafts", "junkemail", "archive", "clutter" };

    public async Task<string> GetAccessTokenAsync(string tenantId, string clientId, string clientSecret)
    {
        var key = $"{tenantId}:{clientId}";
        await _tokenLock.WaitAsync();
        try
        {
            if (_tokenCache.TryGetValue(key, out var cached) && DateTime.UtcNow < cached.Expiry)
                return cached.Token;

            var client = httpFactory.CreateClient();
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["scope"] = "https://graph.microsoft.com/.default"
            });

            var url = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";
            logger.LogDebug("Graph token request: POST {Url} client_id={ClientId}", url, clientId);

            var resp = await client.PostAsync(url, content);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new Exception($"Graph token failed [{resp.StatusCode}]: {body}");

            var json = JObject.Parse(body);
            var token = json["access_token"]!.ToString();
            var expiresIn = json["expires_in"]?.Value<int>() ?? 3600;
            _tokenCache[key] = (token, DateTime.UtcNow.AddSeconds(expiresIn - 60));

            logger.LogInformation("Graph token acquired for tenant {TenantId}", tenantId);
            return token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    // Resource Owner Password Credentials (ROPC) grant — trades a mailbox's own username/password
    // for a delegated Graph token, without an interactive sign-in. clientSecret is optional: only
    // needed when the Azure AD app registration is a confidential client rather than a public one.
    public async Task<string> GetAccessTokenByPasswordAsync(
        string tenantId, string clientId, string? clientSecret, string username, string password)
    {
        var key = $"ropc:{tenantId}:{clientId}:{username}";
        await _tokenLock.WaitAsync();
        try
        {
            if (_tokenCache.TryGetValue(key, out var cached) && DateTime.UtcNow < cached.Expiry)
                return cached.Token;

            var client = httpFactory.CreateClient();
            var formData = new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = clientId,
                ["username"] = username,
                ["password"] = password,
                ["scope"] = "https://graph.microsoft.com/.default"
            };
            if (!string.IsNullOrWhiteSpace(clientSecret))
                formData["client_secret"] = clientSecret;

            var url = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";
            logger.LogDebug("Graph ROPC token request: POST {Url} client_id={ClientId} user={User}", url, clientId, username);

            var resp = await client.PostAsync(url, new FormUrlEncodedContent(formData));
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new Exception($"Graph ROPC token failed [{resp.StatusCode}]: {body}");

            var json = JObject.Parse(body);
            var token = json["access_token"]!.ToString();
            var expiresIn = json["expires_in"]?.Value<int>() ?? 3600;
            _tokenCache[key] = (token, DateTime.UtcNow.AddSeconds(expiresIn - 60));

            logger.LogInformation("Graph ROPC token acquired for {User}", username);
            return token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    public async Task<(string SubscriptionId, DateTime Expiry)> CreateSubscriptionAsync(
        string tenantId, string clientId, string clientSecret,
        string mailboxUpn, string folderName,
        string notificationUrl, string clientState)
    {
        var token = await GetAccessTokenAsync(tenantId, clientId, clientSecret);
        return await CreateSubscriptionAsync(token, mailboxUpn, folderName, notificationUrl, clientState);
    }

    public async Task<(string SubscriptionId, DateTime Expiry)> CreateSubscriptionAsync(
        string accessToken, string mailboxUpn, string folderName,
        string notificationUrl, string clientState)
    {
        var folderId = await ResolveFolderAsync(accessToken, mailboxUpn, folderName);
        var expiry = DateTime.UtcNow.AddMinutes(4200);

        var body = new JObject
        {
            ["changeType"] = "created",
            ["notificationUrl"] = notificationUrl,
            ["resource"] = $"users/{mailboxUpn}/mailFolders/{folderId}/messages",
            ["expirationDateTime"] = expiry.ToString("O"),
            ["clientState"] = clientState
        };

        var client = CreateAuthClient(accessToken);
        logger.LogInformation("Creating Graph subscription: mailbox={Mailbox} folder={Folder}", mailboxUpn, folderName);

        var resp = await client.PostAsync($"{GraphBase}/subscriptions",
            new StringContent(body.ToString(), Encoding.UTF8, "application/json"));
        var respBody = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Create subscription failed [{resp.StatusCode}]: {respBody}");

        var result = JObject.Parse(respBody);
        var subscriptionId = result["id"]!.ToString();
        logger.LogInformation("Graph subscription created: {SubscriptionId}", subscriptionId);
        return (subscriptionId, expiry);
    }

    public async Task<DateTime> RenewSubscriptionAsync(
        string tenantId, string clientId, string clientSecret, string subscriptionId)
    {
        var token = await GetAccessTokenAsync(tenantId, clientId, clientSecret);
        return await RenewSubscriptionAsync(token, subscriptionId);
    }

    public async Task<DateTime> RenewSubscriptionAsync(string accessToken, string subscriptionId)
    {
        var expiry = DateTime.UtcNow.AddMinutes(4200);
        var body = new JObject { ["expirationDateTime"] = expiry.ToString("O") };
        var client = CreateAuthClient(accessToken);

        var req = new HttpRequestMessage(new HttpMethod("PATCH"), $"{GraphBase}/subscriptions/{subscriptionId}")
        {
            Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json")
        };
        var resp = await client.SendAsync(req);

        if (!resp.IsSuccessStatusCode)
        {
            var respBody = await resp.Content.ReadAsStringAsync();
            throw new Exception($"Renew subscription failed [{resp.StatusCode}]: {respBody}");
        }

        logger.LogInformation("Graph subscription renewed: {SubscriptionId}", subscriptionId);
        return expiry;
    }

    public async Task DeleteSubscriptionAsync(
        string tenantId, string clientId, string clientSecret, string subscriptionId)
    {
        try
        {
            var token = await GetAccessTokenAsync(tenantId, clientId, clientSecret);
            await DeleteSubscriptionAsync(token, subscriptionId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete Graph subscription {SubscriptionId} (may already be expired)", subscriptionId);
        }
    }

    public async Task DeleteSubscriptionAsync(string accessToken, string subscriptionId)
    {
        try
        {
            var client = CreateAuthClient(accessToken);
            await client.DeleteAsync($"{GraphBase}/subscriptions/{subscriptionId}");
            logger.LogInformation("Graph subscription deleted: {SubscriptionId}", subscriptionId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete Graph subscription {SubscriptionId} (may already be expired)", subscriptionId);
        }
    }

    public async Task<JObject?> GetMessageAsync(
        string tenantId, string clientId, string clientSecret,
        string mailboxUpn, string messageId)
    {
        var token = await GetAccessTokenAsync(tenantId, clientId, clientSecret);
        return await GetMessageAsync(token, mailboxUpn, messageId);
    }

    public async Task<JObject?> GetMessageAsync(string accessToken, string mailboxUpn, string messageId)
    {
        var client = CreateAuthClient(accessToken);

        var resp = await client.GetAsync(
            $"{GraphBase}/users/{mailboxUpn}/messages/{messageId}" +
            "?$select=id,subject,from,toRecipients,body,receivedDateTime,hasAttachments,importance,internetMessageId,meetingMessageType" +
            "&$expand=event($select=id,iCalUId,start,end,location,recurrence,type)");

        if (!resp.IsSuccessStatusCode)
        {
            logger.LogWarning("GetMessage failed [{Status}] for messageId={MessageId}", resp.StatusCode, messageId);
            return null;
        }

        return JObject.Parse(await resp.Content.ReadAsStringAsync());
    }

    // Supports nested folder paths like "Inbox/test333" by walking childFolders segment by
    // segment — a plain top-level displayName filter can't find a folder nested inside another.
    private async Task<string> ResolveFolderAsync(string token, string mailboxUpn, string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
            return "inbox";

        if (WellKnownFolders.Contains(folderName))
            return folderName;

        var segments = folderName.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
            return "inbox";

        var client = CreateAuthClient(token);

        try
        {
            string? currentId = null;
            foreach (var segment in segments)
            {
                if (currentId == null && WellKnownFolders.Contains(segment))
                {
                    currentId = segment;
                    continue;
                }

                var childId = await ResolveChildFolderIdAsync(client, mailboxUpn, currentId, segment);
                if (childId == null)
                {
                    logger.LogWarning(
                        "Could not resolve mail folder segment '{Segment}' in path '{FolderName}', falling back to inbox",
                        segment, folderName);
                    return "inbox";
                }

                currentId = childId;
            }

            return currentId ?? "inbox";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not resolve folder {FolderName}, falling back to inbox", folderName);
            return "inbox";
        }
    }

    private async Task<string?> ResolveChildFolderIdAsync(HttpClient client, string mailboxUpn, string? parentId, string displayName)
    {
        var encodedName = Uri.EscapeDataString(displayName.Replace("'", "''"));
        var resource = parentId == null
            ? $"{GraphBase}/users/{mailboxUpn}/mailFolders"
            : $"{GraphBase}/users/{mailboxUpn}/mailFolders/{parentId}/childFolders";

        var resp = await client.GetAsync($"{resource}?$filter=displayName eq '{encodedName}'");
        if (!resp.IsSuccessStatusCode) return null;

        var body = JObject.Parse(await resp.Content.ReadAsStringAsync());
        return body["value"]?.FirstOrDefault()?["id"]?.ToString();
    }

    // ─── Delegated-access methods (OAuth2Interactive polling) ───────────────

    public async Task<List<JObject>> ListUnreadMessagesAsync(
        string accessToken, string mailboxUpn, string folderName, int maxCount = 50)
    {
        var folderId = await ResolveFolderAsync(accessToken, mailboxUpn, folderName);
        var client = CreateAuthClient(accessToken);

        var url = $"{GraphBase}/users/{mailboxUpn}/mailFolders/{folderId}/messages" +
                  "?$filter=isRead eq false" +
                  "&$orderby=receivedDateTime asc" +
                  $"&$top={maxCount}" +
                  "&$select=id,subject,from,toRecipients,body,receivedDateTime,hasAttachments,importance,internetMessageId,isRead";

        var resp = await client.GetAsync(url);
        if (!resp.IsSuccessStatusCode)
        {
            logger.LogWarning("ListUnreadMessages failed [{Status}] mailbox={Mailbox}", resp.StatusCode, mailboxUpn);
            return [];
        }

        var body = JObject.Parse(await resp.Content.ReadAsStringAsync());
        return body["value"]?.ToObject<List<JObject>>() ?? [];
    }

    public async Task MarkAsReadAsync(string accessToken, string mailboxUpn, string messageId)
    {
        try
        {
            var client = CreateAuthClient(accessToken);
            var req = new HttpRequestMessage(new HttpMethod("PATCH"),
                $"{GraphBase}/users/{mailboxUpn}/messages/{messageId}")
            {
                Content = new StringContent("{\"isRead\":true}", Encoding.UTF8, "application/json")
            };
            await client.SendAsync(req);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MarkAsRead failed for message {MessageId}", messageId);
        }
    }

    // ─── Folder browsing ────────────────────────────────────────────────────

    public async Task<List<MailFolderInfo>> GetMailFoldersAsync(
        string accessToken, string mailboxUpn, string? parentFolderId = null)
    {
        var client = CreateAuthClient(accessToken);
        var baseResource = string.IsNullOrWhiteSpace(mailboxUpn)
            ? $"{GraphBase}/me"
            : $"{GraphBase}/users/{mailboxUpn}";
        var url = parentFolderId == null
            ? $"{baseResource}/mailFolders?$select=id,displayName,childFolderCount&$top=100"
            : $"{baseResource}/mailFolders/{parentFolderId}/childFolders?$select=id,displayName,childFolderCount&$top=100";

        var resp = await client.GetAsync(url);
        if (!resp.IsSuccessStatusCode)
        {
            var errorBody = await resp.Content.ReadAsStringAsync();
            logger.LogWarning("GetMailFolders failed [{Status}] mailbox={Mailbox}: {Body}", resp.StatusCode, mailboxUpn, errorBody);

            var hint = resp.StatusCode switch
            {
                System.Net.HttpStatusCode.Forbidden => "Access denied — the signed-in account/app does not have permission to this mailbox.",
                System.Net.HttpStatusCode.NotFound => "Mailbox not found — check the email address.",
                System.Net.HttpStatusCode.Unauthorized => "Not authorized — the access token may have expired.",
                _ => "Could not list mail folders."
            };
            throw new Exception($"{hint} [{resp.StatusCode}]: {errorBody}");
        }

        var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
        return json["value"]?.ToObject<List<JObject>>()
            ?.Select(f => new MailFolderInfo(
                f["id"]!.ToString(),
                f["displayName"]!.ToString(),
                f["childFolderCount"]?.Value<int>() ?? 0))
            .OrderBy(f => f.DisplayName)
            .ToList() ?? [];
    }

    private HttpClient CreateAuthClient(string token)
    {
        var client = httpFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
