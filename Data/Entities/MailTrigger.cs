namespace SuperTrigger.Web.Data.Entities;

public class MailTrigger
{
    public int Id { get; set; }
    public bool Active { get; set; } = true;
    public string TriggerName { get; set; } = "";

    // "Interactive" | "OAuth2Interactive" | "AppIdGlobal" | "AppIdPerTrigger"
    public string MailAuthMode { get; set; } = "Interactive";

    // EWS / Interactive credentials
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public string UserDomain { get; set; } = "";

    // OAuth2Interactive: Azure AD tenant override (empty = use global GraphTenantId)
    // AppIdPerTrigger: tenant for client credentials
    public string AzureTenantId { get; set; } = "";

    // Graph per-trigger app credentials (when MailAuthMode = "AppIdPerTrigger")
    public string GraphClientId { get; set; } = "";
    public string GraphClientSecret { get; set; } = "";

    // OAuth2Interactive stored tokens
    public string? OAuthAccessToken { get; set; }
    public string? OAuthRefreshToken { get; set; }
    public DateTime? OAuthTokenExpiry { get; set; }
    public string? OAuthUserUpn { get; set; }

    public string CustomMailServer { get; set; } = "";
    public string CustomMailServerVersion { get; set; } = "";
    public string MailFolder { get; set; } = "Inbox";
    public string SharedMailBox { get; set; } = "";
    public string SubjectFilterContains { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string BodyFilterContains { get; set; } = "";
    public string AttachmentsType { get; set; } = "";
    public string Priority { get; set; } = "Normal";
    public string QueueName { get; set; } = "";
    public string BusinessDepartmentName { get; set; } = "";
    public string BusinessProcessName { get; set; } = "";
    public string DivisionName { get; set; } = "";
    public string CompanyName { get; set; } = "";

    // Graph subscription tracking (AppIdGlobal / AppIdPerTrigger)
    public string? GraphSubscriptionId { get; set; }
    public DateTime? GraphSubscriptionExpiry { get; set; }

    public bool UsesWebhook =>
        MailAuthMode == "AppIdGlobal" || MailAuthMode == "AppIdPerTrigger" || MailAuthMode == "OAuth2Interactive";
}
