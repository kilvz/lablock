using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LabLock.Server.Models;

public class AiMessage
{
    [Key]
    public long Id { get; set; }
    public string ConversationId { get; set; } = "";
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
    public string? ToolCallsJson { get; set; }
    public string? ToolCallId { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(ConversationId))]
    public AiConversation? Conversation { get; set; }
}
