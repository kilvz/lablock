namespace LabLock.Shared.Models;

public enum EventType
{
    Keystroke,
    ProcessStart,
    ProcessStop,
    WindowFocus,
    Login,
    Logoff,
    Idle,
    UsbInsert,
    UsbRemove,
    Clipboard
}

public enum CommandStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Timeout
}
