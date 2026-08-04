namespace SuperTrigger.Web.Data.Entities;

public class AuditLog
{
    public int Id { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string Username { get; set; } = "";
    public string Action { get; set; } = "";       // Create, Update, Delete
    public string EntityType { get; set; } = "";   // FileTrigger, MailTrigger, Settings, User, AdPrincipal
    public string EntityName { get; set; } = "";
    public string Details { get; set; } = "";
}
