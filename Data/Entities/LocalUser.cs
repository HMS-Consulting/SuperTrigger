namespace SuperTrigger.Web.Data.Entities;

public class LocalUser
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
}
