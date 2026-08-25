using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SuperTrigger.Web.Data.Entities;

namespace SuperTrigger.Web.Services;

public class OrchestratorService(
    IHttpClientFactory httpFactory,
    AppSettingsService settingsService,
    ILogger<OrchestratorService> logger)
{
    private string? _cachedToken;
    private DateTime _tokenExpiry = DateTime.MinValue;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    public async Task AddQueueItemAsync(
        JObject payload,
        string queueName,
        string folderPath,
        QueueItemPriority priority = QueueItemPriority.Normal)
    {
        var settings = await settingsService.GetAsync();
        var token = await GetBearerTokenAsync(settings);

        payload["itemData"] ??= new JObject();
        payload["itemData"]!["Name"] = queueName;
        payload["itemData"]!["Priority"] = priority.ToString();

        var client = CreateClient(settings);
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"{settings.OrchestratorURL.TrimEnd('/')}/odata/Queues/UiPathODataSvc.AddQueueItem");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-UIPATH-FolderPath-Encoded",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(folderPath)));
        request.Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");

        var requestBody = payload.ToString();
        var url = request.RequestUri?.ToString();

        logger.LogDebug("AddQueueItem → POST {Url} | Folder: {Folder} | Body: {Body}", url, folderPath, requestBody);

        var response = await client.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError(
                "AddQueueItem FAILED\n  URL: {Url}\n  Folder: {Folder}\n  Status: {Status}\n  Request: {Request}\n  Response: {Response}",
                url, folderPath, (int)response.StatusCode, requestBody, responseBody);
            throw new Exception($"AddQueueItem failed [{(int)response.StatusCode} {response.StatusCode}]: {responseBody}");
        }

        logger.LogInformation("Queue item added: {Queue} | Folder: {Folder}", queueName, folderPath);
    }

    public async Task RaiseAlertAsync(string message, string folderPath)
    {
        try
        {
            var settings = await settingsService.GetAsync();
            var token = await GetBearerTokenAsync(settings);
            var client = CreateClient(settings);

            var payload = new JObject
            {
                ["severity"] = "Error",
                ["component"] = "SuperTrigger",
                ["message"] = message
            };

            var request = new HttpRequestMessage(HttpMethod.Post,
                $"{settings.OrchestratorURL.TrimEnd('/')}/odata/RobotLogs");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("X-UIPATH-FolderPath-Encoded",
                Convert.ToBase64String(Encoding.Unicode.GetBytes(folderPath)));
            request.Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");

            await client.SendAsync(request);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to raise alert");
        }
    }

    private async Task<string> GetBearerTokenAsync(OrchSettings settings)
    {
        await _tokenLock.WaitAsync();
        try
        {
            if (_cachedToken != null && DateTime.Now < _tokenExpiry)
                return _cachedToken;

            if (settings.SSO)
            {
                _cachedToken = "";
                _tokenExpiry = DateTime.Now.AddHours(1);
                return _cachedToken;
            }

            string token;
            if (settings.AuthNewMethod)
                token = await AuthenticateExternalAppAsync(settings);
            else
                token = await AuthenticateUserPassAsync(settings);

            _cachedToken = token;
            _tokenExpiry = DateTime.Now.AddMinutes(55);
            return token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<string> AuthenticateUserPassAsync(OrchSettings settings)
    {
        var client = CreateClient(settings);
        var payload = new JObject
        {
            ["tenancyName"] = settings.TenantName,
            ["usernameOrEmailAddress"] = settings.Username,
            ["password"] = settings.Password
        };

        var authUrl = $"{settings.OrchestratorURL.TrimEnd('/')}/api/account/authenticate";
        var requestContent = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");

        var response = await client.PostAsync(authUrl, requestContent);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Auth (UserPass) FAILED\n  URL: {Url}\n  Status: {Status}\n  Response: {Body}",
                authUrl, (int)response.StatusCode, body);
            throw new Exception($"Auth failed [{(int)response.StatusCode}]: {body}");
        }

        var json = JObject.Parse(body);
        return json["result"]?.ToString() ?? throw new Exception("No token in auth response");
    }

    private async Task<string> AuthenticateExternalAppAsync(OrchSettings settings)
    {
        var client = CreateClient(settings);
        var baseUri = new Uri(settings.OrchestratorURL.TrimEnd('/'));
        string identityUrl;
        
        if(baseUri.Host.Equals("cloud.uipath.com", StringComparison.OrdinalIgnoreCase))
            identityUrl = "https://cloud.uipath.com/identity_/connect/token";
        else if (baseUri.ToString().EndsWith("orchestrator_", StringComparison.OrdinalIgnoreCase))
            identityUrl = $"{baseUri.ToString().ToLower().Replace("orchestrator_","identity_")}/connect/token";
        else
            identityUrl = $"{baseUri.Scheme}://{baseUri.Host}/identity/connect/token";

        var scopes = string.Join(" ",
            settings.ExternalAppScopes.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries));

        var formFields = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = settings.Username,
            ["client_secret"] = settings.Password,
            ["scope"] = scopes
        };
        var content = new FormUrlEncodedContent(formFields);

        var response = await client.PostAsync(identityUrl, content);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Auth (ExternalApp/OAuth2) FAILED\n  URL: {Url}\n  Status: {Status}\n  Response: {Body}",
                identityUrl, (int)response.StatusCode, body);
            throw new Exception($"Auth failed [{(int)response.StatusCode}]: {body}");
        }

        var json = JObject.Parse(body);
        return json["access_token"]?.ToString() ?? throw new Exception("No access_token in response");
    }

    private HttpClient CreateClient(OrchSettings settings)
    {
        var client = httpFactory.CreateClient("Orchestrator");
        if (settings.UseDefaultProxy)
            client.DefaultRequestHeaders.Add("X-Use-Default-Proxy", "true");
        return client;
    }

    public void InvalidateToken() => _cachedToken = null;
}

public enum QueueItemPriority { Low, Normal, High }
