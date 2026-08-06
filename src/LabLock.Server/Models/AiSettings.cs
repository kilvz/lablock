using System.ComponentModel.DataAnnotations;

namespace LabLock.Server.Models;

public class AiSettings
{
    [Key]
    public int Id { get; set; }
    public string Provider { get; set; } = "none";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public double Temperature { get; set; } = 0.3;
    public int MaxTokens { get; set; } = 4096;
    public string SystemPrompt { get; set; } = "";
}
