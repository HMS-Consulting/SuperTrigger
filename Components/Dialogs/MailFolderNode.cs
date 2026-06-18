namespace SuperTrigger.Web.Components.Dialogs;

public class MailFolderNode
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool HasChildren { get; set; }
    public List<MailFolderNode>? Children { get; set; }  // null = not yet loaded
    public bool IsExpanded { get; set; }
    public bool IsLoading { get; set; }
    public string Path { get; set; } = "";
}
