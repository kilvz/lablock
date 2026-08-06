namespace LabLock.Shared.Models;

public class CommandRequestDto
{
    public string Command { get; set; } = "";
    public string Args { get; set; } = "";
    public string SentBy { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 60;
}
