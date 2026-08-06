using System.ComponentModel.DataAnnotations;

namespace LabLock.Server.Models;

public class AiConversation
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<AiMessage> Messages { get; set; } = new();
}
