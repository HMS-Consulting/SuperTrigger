namespace SuperTrigger.Web.Data.Entities;

public class FileTrigger : IFolderTrigger
{
    public int Id { get; set; }
    public bool Active { get; set; } = true;
    public string TriggerName { get; set; } = "";
    public string FolderPath { get; set; } = "";
    public string FileNameContains { get; set; } = "";
    public string FileTypes { get; set; } = "xlsx";
    public string Priority { get; set; } = "Normal";
    public string QueueName { get; set; } = "";
    public string BusinessDepartmentName { get; set; } = "";
    public string BusinessProcessName { get; set; } = "";
    public string DivisionName { get; set; } = "";
    public string CompanyName { get; set; } = "";
    public string WatcherUsername { get; set; } = "";
    public string WatcherPassword { get; set; } = "";
}
