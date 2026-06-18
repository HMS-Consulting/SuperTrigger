namespace SuperTrigger.Web.Data.Entities;

public class OrchSettings
{
    public int Id { get; set; } = 1;
    public string OrchestratorURL { get; set; } = "";
    public string TenantName { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public bool SSO { get; set; } = false;
    public bool AuthNewMethod { get; set; } = false;
    public string ExternalAppScopes { get; set; } = "OR.Assets.Read,OR.Queues";
    public string OrchestratorMainFolderName { get; set; } = "Root";
    public string OrchestratorGeneralFolderName { get; set; } = ".General";
    public string CyberArkSDKFilePath { get; set; } = "";
    public bool UseDefaultProxy { get; set; } = false;
    public double MailTriggerIntervalInMinutes { get; set; } = 0.5;
    public int SameFileIntervalInSeconds { get; set; } = 20;
    public string MailServerURL { get; set; } = "";
    public string MailServerVersion { get; set; } = "Exchange2013_SP1";
    public string PublicBaseUrl { get; set; } = "";

    // Active Directory service account for LDAP search
    public string AdUsername { get; set; } = "";
    public string AdPassword { get; set; } = "";

    // Global Microsoft Graph / Azure AD app credentials
    public string GraphTenantId { get; set; } = "";
    public string GraphClientId { get; set; } = "";
    public string GraphClientSecret { get; set; } = "";

    // Global OAuth2 interactive token (signed in once via Settings)
    public string? OAuthGlobalAccessToken { get; set; }
    public string? OAuthGlobalRefreshToken { get; set; }
    public DateTime? OAuthGlobalTokenExpiry { get; set; }
    public string? OAuthGlobalUserUpn { get; set; }
}
