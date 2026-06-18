namespace SuperTrigger.Web.Data.Entities;

public enum AdPrincipalType { User, Group }
public enum AppRole { Admin, Operator, Viewer }

public class AdPrincipal
{
    public int Id { get; set; }
    public string Identifier { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public AdPrincipalType Type { get; set; }
    public AppRole Role { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.Now;
}
