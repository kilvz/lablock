namespace LabLock.Shared.Models;

public class ClientRegistrationDto
{
    public string ClientId { get; set; } = "";
    public string Hostname { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public string MacAddress { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public string AgentVersion { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string InteractiveUser { get; set; } = "";
    public int InteractiveSessionId { get; set; }
    public int AgentSessionId { get; set; }
}
