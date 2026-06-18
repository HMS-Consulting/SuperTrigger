namespace SuperTrigger.Web.Data.Entities;

public enum TriggerType { File, Mail }

public class QueueItemLog
{
    public int Id { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string TriggerName { get; set; } = "";
    public TriggerType TriggerType { get; set; }
    public string QueueName { get; set; } = "";
    public string Reference { get; set; } = "";
    public string Payload { get; set; } = "";
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}
