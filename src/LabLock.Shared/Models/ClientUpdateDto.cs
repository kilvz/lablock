namespace LabLock.Shared.Models;

public class ClientUpdateDto
{
    public string Version { get; set; } = "";
    public string Checksum { get; set; } = "";
    public string Filename { get; set; } = "LabLock.Client.exe";
}
