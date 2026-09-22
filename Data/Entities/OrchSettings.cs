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
    public int MailTriggerIntervalInSeconds { get; set; } = 30;
    public int SameFileIntervalInSeconds { get; set; } = 20;
    public string MailServerURL { get; set; } = "";
    public string MailServerVersion { get; set; } = "Exchange2013_SP1";
    public string PublicBaseUrl { get; set; } = "";

    // Active Directory service account for LDAP search
    public string AdUsername { get; set; } = "";
    public string AdPassword { get; set; } = "";

    // The mail authentication method this installation uses by default — same values as
    // MailTrigger.MailAuthMode ("Interactive" | "OAuth2Interactive" | "AppIdGlobal" |
    // "AppIdPerTrigger" | "GraphUsernamePassword"). Drives which fields Settings shows, and which
    // credentials an "AppIdGlobal" trigger authenticates with.
    public string GlobalMailAuthMode { get; set; } = "Interactive";

    // Shared mailbox used by a trigger set to "Use Global Setting" (MailAuthMode="AppIdGlobal")
    // when that trigger doesn't specify its own SharedMailBox override. Applies regardless of
    // which GlobalMailAuthMode is active (Graph or Interactive/EWS).
    public string GlobalSharedMailBox { get; set; } = "";

    // Global Microsoft Graph / Azure AD app credentials
    public string GraphTenantId { get; set; } = "";
    public string GraphClientId { get; set; } = "";
    public string GraphClientSecret { get; set; } = "";

    // Global Graph username/password (used when GlobalMailAuthMode = "GraphUsernamePassword")
    public string GraphUsername { get; set; } = "";
    public string GraphPassword { get; set; } = "";

    // Default detection mode for Graph mail triggers ("Webhook" | "Polling") — used both as the
    // default when creating a new trigger, and to decide whether Settings shows the Public Base
    // URL (webhook) or Mail Poll Interval (polling) field.
    public string GraphDetectionMode { get; set; } = "Webhook";

    // Global OAuth2 interactive token (signed in once via Settings)
    public string? OAuthGlobalAccessToken { get; set; }
    public string? OAuthGlobalRefreshToken { get; set; }
    public DateTime? OAuthGlobalTokenExpiry { get; set; }
    public string? OAuthGlobalUserUpn { get; set; }

    // Retention policy for Logs / Audit Logs (days to keep before deletion)
    public int RetentionDays { get; set; } = 14;

    // Global detection method for File Triggers ("FileSystemWatcher" | "Polling"). Always global —
    // there is no per-trigger override. Default preserves existing behavior for upgrades.
    public string FileWatchMethod { get; set; } = "FileSystemWatcher";

    // Sampling interval (seconds) used when FileWatchMethod = "Polling".
    public int FilePollingIntervalInSeconds { get; set; } = 30;
}
