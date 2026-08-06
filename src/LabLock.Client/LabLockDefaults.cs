namespace LabLock.Client;

public static class LabLockDefaults
{
    public const string ServerUrl = "http://192.168.1.58:5000";
    public const string ApiKey = "LabLockKey-2026";
    public const int HeartbeatIntervalSeconds = 30;
    public const int LogBatchIntervalSeconds = 10;
    public const int ReconnectDelaySeconds = 2;
    public const int WindowPollIntervalMs = 2000;
}
