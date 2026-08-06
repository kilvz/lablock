# LabLock — Complete Implementation Plan

> **Purpose**: Centralized management & monitoring system for ~100 Windows lab PCs with AI-powered control.  
> **Server runs on**: Linux  
> **Clients run on**: Windows  
> **This document is the single source of truth. Implement exactly as specified.**

---

## Table of Contents

1. [System Overview](#system-overview)
2. [Project Structure](#project-structure)
3. [Shared Library — LabLock.Shared](#1-shared-library--lablockshared)
4. [Client Agent — LabLock.Client](#2-client-agent--lablockclient)
5. [Server — LabLock.Server](#3-server--lablockserver)
6. [Web Dashboard](#4-web-dashboard)
7. [AI Integration](#5-ai-integration)
8. [Database Schema](#6-database-schema)
9. [SignalR Protocol](#7-signalr-protocol-contract)
10. [REST API Contract](#8-rest-api-contract)
11. [Security](#9-security)
12. [Deployment](#10-deployment)
13. [Implementation Order](#11-implementation-order)

---

## System Overview

```
                        ┌──────────────────────────────────┐
                        │         Linux Server             │
                        │                                  │
                        │  ASP.NET Core 8 (Kestrel)        │
                        │  ┌────────────┐ ┌─────────────┐  │
                        │  │ SignalR Hub │ │  REST API   │  │
                        │  └─────┬──────┘ └──────┬──────┘  │
                        │        │               │         │
                        │  ┌─────┴───────────────┴──────┐  │
                        │  │      Core Services          │  │
                        │  │  ClientState | LogStorage   │  │
                        │  │  CommandSvc  | AiChatSvc    │  │
                        │  └─────────────┬──────────────┘  │
                        │                │                 │
                        │  ┌─────────────┴──────────────┐  │
                        │  │     SQLite Database         │  │
                        │  └────────────────────────────┘  │
                        │                                  │
                        │  ┌────────────────────────────┐  │
                        │  │  Static Files (Dashboard)  │  │
                        │  └────────────────────────────┘  │
                        └──────────┬───────────────────────┘
                                   │ SignalR (WebSocket)
              ┌────────────────────┼────────────────────┐
              │                    │                    │
     ┌────────┴───────┐  ┌────────┴───────┐  ┌────────┴───────┐
     │  Win PC Client  │  │  Win PC Client  │  │  Win PC Client  │
     │  (×100)         │  │                 │  │                 │
     │  - Keystroke    │  │                 │  │                 │
     │  - Processes    │  │    ...same...   │  │    ...same...   │
     │  - Windows      │  │                 │  │                 │
     │  - PowerShell   │  │                 │  │                 │
     └────────────────┘  └─────────────────┘  └─────────────────┘
```

### Core Capabilities

| Capability | Description |
|---|---|
| **Activity Logging** | Keystroke capture, process start/stop, window focus tracking, login/logoff, USB events |
| **Remote PowerShell** | Execute any PowerShell command on any connected client, stream output in real-time |
| **AI Chat** | Natural language control — "Install Firefox on all PCs", "Which PCs are idle?" |
| **Real-time Dashboard** | Live grid of all PCs with status, current user, active app |
| **Configurable AI** | Plug in OpenAI, Anthropic, Google Gemini, Ollama, or any OpenAI-compatible endpoint |

### Tech Stack

| Layer | Technology | Rationale |
|---|---|---|
| **Client Agent** | C# .NET 8 Windows Service | Native Windows, runs at startup, excellent PowerShell interop |
| **Server** | ASP.NET Core 8 + SignalR | Cross-platform (runs on Linux), built-in WebSocket, auto-reconnect |
| **Dashboard** | Vanilla HTML/CSS/JS | No build tools needed, served directly by ASP.NET Core |
| **Database** | SQLite via EF Core | Zero-config, single-file, sufficient for ~100 clients |
| **Communication** | SignalR (WebSocket + fallback) | Bidirectional real-time, handles reconnection, RPC-style calls |
| **AI** | Multi-provider (OpenAI, Anthropic, Gemini, Ollama) | Function-calling for full PC access |

---

## Project Structure

```
lck/
├── LabLock.sln
├── IMPLEMENTATION_PLAN.md                       ← this file
│
├── src/
│   ├── LabLock.Shared/                          # Shared DTOs and contracts
│   │   ├── LabLock.Shared.csproj
│   │   └── Models/
│   │       ├── ActivityLogDto.cs
│   │       ├── HeartbeatDto.cs
│   │       ├── ClientRegistrationDto.cs
│   │       ├── CommandRequestDto.cs
│   │       ├── CommandResultDto.cs
│   │       ├── SystemInfoDto.cs
│   │       └── Enums.cs
│   │
│   ├── LabLock.Client/                          # Windows Service agent
│   │   ├── LabLock.Client.csproj
│   │   ├── Program.cs
│   │   ├── appsettings.json
│   │   ├── Services/
│   │   │   ├── ConnectionService.cs
│   │   │   ├── KeystrokeLoggerService.cs
│   │   │   ├── ProcessMonitorService.cs
│   │   │   ├── WindowMonitorService.cs
│   │   │   ├── PowerShellExecutorService.cs
│   │   │   ├── LogBufferService.cs
│   │   │   └── SystemInfoService.cs
│   │   └── Helpers/
│   │       ├── NativeMethods.cs
│   │       └── KeyMapper.cs
│   │
│   └── LabLock.Server/                          # Linux-hosted ASP.NET Core server
│       ├── LabLock.Server.csproj
│       ├── Program.cs
│       ├── appsettings.json
│       ├── Hubs/
│       │   └── ClientHub.cs
│       ├── Controllers/
│       │   ├── ClientsController.cs
│       │   ├── LogsController.cs
│       │   ├── CommandsController.cs
│       │   ├── AiController.cs
│       │   ├── AiSettingsController.cs
│       │   └── AuthController.cs
│       ├── Services/
│       │   ├── ClientStateService.cs
│       │   ├── LogStorageService.cs
│       │   ├── CommandService.cs
│       │   └── Ai/
│       │       ├── IAiProvider.cs
│       │       ├── AiProviderFactory.cs
│       │       ├── AiChatService.cs
│       │       ├── AiToolExecutor.cs
│       │       ├── Providers/
│       │       │   ├── OpenAiProvider.cs
│       │       │   ├── AnthropicProvider.cs
│       │       │   ├── GeminiProvider.cs
│       │       │   └── OllamaProvider.cs
│       │       └── Tools/
│       │           └── ToolDefinitions.cs
│       ├── Data/
│       │   ├── AppDbContext.cs
│       │   └── Migrations/
│       ├── Models/
│       │   ├── Client.cs
│       │   ├── ActivityLog.cs
│       │   ├── CommandHistory.cs
│       │   ├── AiSettings.cs
│       │   ├── AiConversation.cs
│       │   ├── AiMessage.cs
│       │   └── DashboardUser.cs
│       └── wwwroot/
│           ├── index.html
│           ├── login.html
│           ├── css/
│           │   ├── app.css
│           │   └── terminal.css
│           ├── js/
│           │   ├── app.js
│           │   ├── router.js
│           │   ├── api.js
│           │   ├── signalr.min.js
│           │   ├── pages/
│           │   │   ├── overview.js
│           │   │   ├── pc-detail.js
│           │   │   ├── terminal.js
│           │   │   ├── activity-log.js
│           │   │   ├── ai-chat.js
│           │   │   └── settings.js
│           │   └── components/
│           │       ├── pc-card.js
│           │       ├── log-table.js
│           │       ├── chat-message.js
│           │       └── toast.js
│           └── assets/
│               └── logo.svg
│
└── deploy/
    ├── install-client.ps1
    ├── uninstall-client.ps1
    ├── deploy-clients.ps1
    ├── server-setup.sh
    └── lablock-server.service
```

---

## 1. Shared Library — `LabLock.Shared`

### `LabLock.Shared.csproj`

- Target: `net8.0`
- No NuGet dependencies (plain DTOs only)
- This project is referenced by both Client and Server

### `Models/Enums.cs`

```csharp
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
```

### `Models/ActivityLogDto.cs`

```csharp
namespace LabLock.Shared.Models;

public class ActivityLogDto
{
    public string ClientId { get; set; } = "";
    public EventType EventType { get; set; }
    public string Details { get; set; } = "";       // JSON string with event-specific data
    public DateTime Timestamp { get; set; }
    public string Username { get; set; } = "";
}
```

**Details JSON format per EventType:**

| EventType | Details JSON |
|---|---|
| `Keystroke` | `{"keys": "Hello world", "window": "Notepad", "process": "notepad.exe"}` |
| `ProcessStart` | `{"name": "chrome.exe", "pid": 1234, "path": "C:\\...\\chrome.exe", "cmdline": "..."}` |
| `ProcessStop` | `{"name": "chrome.exe", "pid": 1234, "duration_sec": 3600}` |
| `WindowFocus` | `{"title": "Google - Chrome", "process": "chrome.exe", "duration_sec": 120}` |
| `Login` | `{"username": "worker01", "type": "interactive"}` |
| `Logoff` | `{"username": "worker01"}` |
| `Idle` | `{"idle_seconds": 300}` |
| `UsbInsert` | `{"device": "USB Mass Storage", "drive": "E:"}` |
| `UsbRemove` | `{"device": "USB Mass Storage", "drive": "E:"}` |
| `Clipboard` | `{"text": "copied text...", "source_window": "Chrome"}` |

### `Models/HeartbeatDto.cs`

```csharp
namespace LabLock.Shared.Models;

public class HeartbeatDto
{
    public string ClientId { get; set; } = "";
    public string CurrentUser { get; set; } = "";
    public string ActiveWindow { get; set; } = "";
    public string ActiveProcess { get; set; } = "";
    public double CpuPercent { get; set; }
    public double MemoryPercent { get; set; }
    public long UptimeSeconds { get; set; }
    public DateTime Timestamp { get; set; }
}
```

### `Models/ClientRegistrationDto.cs`

```csharp
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
}
```

### `Models/CommandRequestDto.cs`

```csharp
namespace LabLock.Shared.Models;

public class CommandRequestDto
{
    public string Command { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 60;
}
```

### `Models/CommandResultDto.cs`

```csharp
namespace LabLock.Shared.Models;

public class CommandResultDto
{
    public int CommandId { get; set; }
    public string Output { get; set; } = "";
    public string Error { get; set; } = "";
    public int ExitCode { get; set; }
    public bool IsPartial { get; set; }  // true = streaming chunk, false = final result
}
```

### `Models/SystemInfoDto.cs`

```csharp
namespace LabLock.Shared.Models;

public class SystemInfoDto
{
    public string ClientId { get; set; } = "";
    public string Hostname { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public string CpuName { get; set; } = "";
    public int CpuCores { get; set; }
    public long TotalMemoryMb { get; set; }
    public long FreeMemoryMb { get; set; }
    public long TotalDiskMb { get; set; }
    public long FreeDiskMb { get; set; }
    public string[] InstalledSoftware { get; set; } = [];
    public string[] RunningProcesses { get; set; } = [];
    public string CurrentUser { get; set; } = "";
    public string IpAddress { get; set; } = "";
}
```

---

## 2. Client Agent — `LabLock.Client`

### `LabLock.Client.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk.Worker">
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <PublishSingleFile>true</PublishSingleFile>
    <SelfContained>true</SelfContained>
    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.SignalR.Client" Version="8.*" />
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="8.*" />
    <PackageReference Include="Microsoft.Extensions.Hosting.WindowsServices" Version="8.*" />
    <PackageReference Include="System.Management" Version="8.*" />
    <PackageReference Include="Microsoft.PowerShell.SDK" Version="7.*" />
    <PackageReference Include="Microsoft.Data.Sqlite" Version="8.*" />
    <ProjectReference Include="..\LabLock.Shared\LabLock.Shared.csproj" />
  </ItemGroup>
</Project>
```

### `appsettings.json` (Client)

```json
{
  "LabLock": {
    "ServerUrl": "http://SERVER_IP:5000",
    "ApiKey": "CHANGE_ME_SHARED_KEY",
    "ClientId": "",
    "HeartbeatIntervalSeconds": 30,
    "LogBatchIntervalSeconds": 10,
    "MaxLocalBufferDays": 7,
    "EnableKeystrokeLogging": true,
    "EnableProcessMonitoring": true,
    "EnableWindowMonitoring": true,
    "WindowPollIntervalMs": 2000,
    "ReconnectDelaySeconds": 5,
    "MaxReconnectDelaySeconds": 60
  }
}
```

> If `ClientId` is empty string or missing, auto-generate from `Environment.MachineName`.

### `Program.cs` (Client)

```
IMPLEMENTATION SPEC:

1. Use Host.CreateDefaultBuilder(args)
2. Call .UseWindowsService() for Windows Service support
3. Configure logging: Console + File (to C:\LabLock\logs\)
4. Load configuration from:
   - appsettings.json (from app directory)
   - Environment variables (prefix: LABLOCK_)
   - Command-line args: --server-url, --api-key, --client-id
5. Register services in this order:
   - services.AddSingleton<LogBufferService>()
   - services.AddSingleton<SystemInfoService>()
   - services.AddSingleton<PowerShellExecutorService>()
   - services.AddHostedService<ConnectionService>()
   - services.AddHostedService<KeystrokeLoggerService>()
   - services.AddHostedService<ProcessMonitorService>()
   - services.AddHostedService<WindowMonitorService>()
6. Build and run host
```

### `Services/ConnectionService.cs`

```
CLASS: ConnectionService : BackgroundService

DEPENDENCIES (inject via constructor):
  - IConfiguration
  - ILogger<ConnectionService>
  - LogBufferService
  - PowerShellExecutorService
  - SystemInfoService

FIELDS:
  - HubConnection? _connection
  - string _clientId          (from config or Environment.MachineName)
  - string _serverUrl         (from config)
  - string _apiKey            (from config)
  - int _heartbeatInterval    (from config, default 30s)
  - int _logBatchInterval     (from config, default 10s)
  - Timer? _heartbeatTimer
  - Timer? _logFlushTimer
  - bool _isConnected

PUBLIC PROPERTY:
  - HubConnection? Connection { get; }   (other services read this)
  - bool IsConnected { get; }

METHOD ExecuteAsync(CancellationToken stoppingToken):
  1. Read config values
  2. Set _clientId = config value or Environment.MachineName
  3. LOOP while not cancelled:
     a. Try ConnectAsync()
     b. If connected, await until disconnected or cancelled
     c. On disconnect, wait reconnect delay (exponential: 5s → 10s → 30s → 60s max)

METHOD ConnectAsync():
  1. Build HubConnection:
     new HubConnectionBuilder()
       .WithUrl($"{_serverUrl}/hub/client?clientId={_clientId}&apiKey={_apiKey}")
       .WithAutomaticReconnect(new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60) })
       .Build()
  
  2. Register server-to-client handlers:
  
     _connection.On<CommandRequestDto>("ExecuteCommand", async (request) => {
       var result = await PowerShellExecutorService.ExecuteAsync(request.Command, request.TimeoutSeconds);
       return result;  // CommandResultDto
     });

     _connection.On("GetSystemInfo", async () => {
       return SystemInfoService.Collect();  // SystemInfoDto
     });

     _connection.On("RestartAgent", () => {
       // Schedule a self-restart:
       Process.Start("cmd", "/c timeout 3 && net start LabLockAgent");
       Environment.Exit(0);
     });

     _connection.On<Dictionary<string,string>>("UpdateConfig", (newConfig) => {
       // Read current appsettings.json, merge newConfig, write back, restart
     });

  3. Register reconnect handlers:
     _connection.Reconnecting += (ex) => { _isConnected = false; log; }
     _connection.Reconnected += (connId) => { _isConnected = true; re-register; }
     _connection.Closed += (ex) => { _isConnected = false; log; }

  4. await _connection.StartAsync()
  5. _isConnected = true

  6. Register with server:
     await _connection.InvokeAsync("RegisterClient", new ClientRegistrationDto {
       ClientId = _clientId,
       Hostname = Environment.MachineName,
       IpAddress = <get local IP>,
       MacAddress = <get MAC>,
       OsVersion = Environment.OSVersion.ToString(),
       AgentVersion = Assembly.GetExecutingAssembly().GetName().Version.ToString(),
       ApiKey = _apiKey
     });

  7. Start heartbeat timer (every _heartbeatInterval seconds):
     Build HeartbeatDto with current user, active window, CPU%, memory%, uptime
     await _connection.InvokeAsync("Heartbeat", heartbeatDto)

  8. Start log flush timer (every _logBatchInterval seconds):
     var batch = LogBufferService.FlushBatch(500);
     if (batch.Length > 0)
       await _connection.InvokeAsync("SendActivityBatch", batch);
```

### `Services/KeystrokeLoggerService.cs`

```
CLASS: KeystrokeLoggerService : BackgroundService

DEPENDENCIES:
  - LogBufferService
  - IConfiguration (to check EnableKeystrokeLogging)
  - ILogger

FIELDS:
  - IntPtr _hookId
  - NativeMethods.LowLevelKeyboardProc _hookProc  (prevent GC)
  - StringBuilder _keyBuffer = new()
  - string _currentWindow = ""
  - string _currentProcess = ""
  - DateTime _lastKeyTime
  - Thread? _hookThread

METHOD ExecuteAsync(CancellationToken stoppingToken):
  1. If EnableKeystrokeLogging is false, return immediately
  2. Create dedicated STA thread for the hook:
     _hookThread = new Thread(() => {
       _hookProc = HookCallback;  // prevent GC collection of delegate
       _hookId = NativeMethods.SetWindowsHookEx(
         NativeMethods.WH_KEYBOARD_LL,
         _hookProc,
         NativeMethods.GetModuleHandle(Process.GetCurrentProcess().MainModule.ModuleName),
         0
       );
       // Run message loop:
       while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) {
         NativeMethods.TranslateMessage(ref msg);
         NativeMethods.DispatchMessage(ref msg);
         if (stoppingToken.IsCancellationRequested) break;
       }
       NativeMethods.UnhookWindowsHookEx(_hookId);
     });
     _hookThread.SetApartmentState(ApartmentState.STA);
     _hookThread.IsBackground = true;
     _hookThread.Start();
  3. Start a flush timer: every 5 seconds, call FlushKeyBuffer()
  4. await Task.Delay(Timeout.Infinite, stoppingToken)

METHOD HookCallback(int nCode, IntPtr wParam, IntPtr lParam) → IntPtr:
  1. If nCode >= 0 and (wParam == WM_KEYDOWN or wParam == WM_SYSKEYDOWN):
     a. Read KBDLLHOOKSTRUCT from lParam
     b. Map vkCode to string via KeyMapper.Map(vkCode)
     c. Get current foreground window + process
     d. If window changed from _currentWindow → call FlushKeyBuffer() first
     e. Update _currentWindow, _currentProcess
     f. Append mapped key to _keyBuffer
     g. Update _lastKeyTime
     h. If _keyBuffer.Length > 200 → FlushKeyBuffer()
  2. Return CallNextHookEx(_hookId, nCode, wParam, lParam)

METHOD FlushKeyBuffer():
  1. If _keyBuffer.Length == 0, return
  2. Create ActivityLogDto:
     ClientId = <from config>,
     EventType = EventType.Keystroke,
     Details = JsonSerializer.Serialize(new {
       keys = _keyBuffer.ToString(),
       window = _currentWindow,
       process = _currentProcess
     }),
     Timestamp = DateTime.UtcNow,
     Username = Environment.UserName
  3. LogBufferService.Add(dto)
  4. _keyBuffer.Clear()
```

### `Services/ProcessMonitorService.cs`

```
CLASS: ProcessMonitorService : BackgroundService

DEPENDENCIES:
  - LogBufferService
  - IConfiguration
  - ILogger

FIELDS:
  - ConcurrentDictionary<int, (string name, string path, DateTime startTime)> _trackedProcesses

METHOD ExecuteAsync(CancellationToken stoppingToken):
  1. If EnableProcessMonitoring is false, return immediately
  
  2. PRIMARY APPROACH — WMI event watchers:
     Try to create ManagementEventWatcher for process creation:
       query = "SELECT * FROM __InstanceCreationEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_Process'"
       watcher.EventArrived += OnProcessStarted
       watcher.Start()
     
     Try to create ManagementEventWatcher for process deletion:
       query = "SELECT * FROM __InstanceDeletionEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_Process'"
       watcher.EventArrived += OnProcessStopped
       watcher.Start()
  
  3. FALLBACK APPROACH — if WMI fails, use polling:
     Every 3 seconds:
       var current = Process.GetProcesses().ToDictionary(p => p.Id, p => p)
       Compare with previous snapshot
       New PIDs → log ProcessStart
       Missing PIDs → log ProcessStop with duration
       Update previous snapshot
  
  4. await Task.Delay(Timeout.Infinite, stoppingToken)

METHOD OnProcessStarted(object sender, EventArrivedEventArgs e):
  1. Extract process info from e.NewEvent["TargetInstance"]
  2. Get name, pid, path, command line
  3. Track in _trackedProcesses
  4. Log ActivityLogDto with EventType.ProcessStart

METHOD OnProcessStopped(object sender, EventArrivedEventArgs e):
  1. Extract pid from event
  2. Look up in _trackedProcesses
  3. Calculate duration
  4. Log ActivityLogDto with EventType.ProcessStop
  5. Remove from _trackedProcesses

STARTUP SNAPSHOT:
  On start, take a snapshot of all running processes and add to _trackedProcesses
  (so we can track their stop events even though we missed their start)
```

### `Services/WindowMonitorService.cs`

```
CLASS: WindowMonitorService : BackgroundService

DEPENDENCIES:
  - LogBufferService
  - IConfiguration
  - ILogger

FIELDS:
  - string _lastTitle = ""
  - string _lastProcess = ""
  - DateTime _lastChangeTime

METHOD ExecuteAsync(CancellationToken stoppingToken):
  1. If EnableWindowMonitoring is false, return immediately
  2. _lastChangeTime = DateTime.UtcNow
  3. LOOP every WindowPollIntervalMs (default 2000):
     a. IntPtr hwnd = NativeMethods.GetForegroundWindow()
     b. string title = GetWindowText(hwnd)  (via NativeMethods, buffer size 256)
     c. Get process: GetWindowThreadProcessId(hwnd, out pid) → Process.GetProcessById(pid).ProcessName
        (wrap in try-catch, process may have exited)
     d. If title != _lastTitle or process != _lastProcess:
        i.  If _lastTitle is not empty, log PREVIOUS window:
            ActivityLogDto {
              EventType = WindowFocus,
              Details = {"title": _lastTitle, "process": _lastProcess, "duration_sec": (now - _lastChangeTime).TotalSeconds},
              Timestamp = _lastChangeTime
            }
        ii. Update _lastTitle = title, _lastProcess = process, _lastChangeTime = now
     e. Check stoppingToken, break if cancelled
```

### `Services/PowerShellExecutorService.cs`

```
CLASS: PowerShellExecutorService

DEPENDENCIES:
  - ILogger

METHOD async Task<CommandResultDto> ExecuteAsync(string command, int timeoutSeconds, CancellationToken ct = default):
  1. Create new PowerShell instance:
     using var ps = PowerShell.Create();
  2. Set execution policy for this runspace:
     ps.AddScript("Set-ExecutionPolicy -ExecutionPolicy Unrestricted -Scope Process -Force");
     ps.Invoke();
     ps.Commands.Clear();
  3. ps.AddScript(command)
  4. var outputBuffer = new PSDataCollection<PSObject>();
  5. var result = new StringBuilder();
  6. var errorResult = new StringBuilder();
  7. Use timeout:
     using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
     timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
  8. try:
     var asyncResult = ps.BeginInvoke<PSObject, PSObject>(null, outputBuffer);
     // Collect output
     WaitHandle.WaitAny(new[] { asyncResult.AsyncWaitHandle, timeoutCts.Token.WaitHandle });
     if (timeoutCts.IsCancellationRequested) {
       ps.Stop();
       return new CommandResultDto { Output = result.ToString(), Error = "Command timed out", ExitCode = -1 };
     }
     ps.EndInvoke(asyncResult);
     foreach (var obj in outputBuffer) result.AppendLine(obj?.ToString());
     foreach (var err in ps.Streams.Error) errorResult.AppendLine(err.ToString());
     return new CommandResultDto {
       Output = result.ToString(),
       Error = errorResult.ToString(),
       ExitCode = ps.HadErrors ? 1 : 0
     };
  9. catch (Exception ex):
     return new CommandResultDto { Output = "", Error = ex.Message, ExitCode = -1 };

METHOD async Task<string> ExecuteSimpleAsync(string command):
  // Convenience wrapper for AI tool calls
  var result = await ExecuteAsync(command, 60);
  if (!string.IsNullOrEmpty(result.Error))
    return $"OUTPUT:\n{result.Output}\n\nERROR:\n{result.Error}";
  return result.Output;
```

### `Services/LogBufferService.cs`

```
CLASS: LogBufferService : IDisposable

FIELDS:
  - ConcurrentQueue<ActivityLogDto> _buffer = new()
  - string _dbPath = Path.Combine(AppContext.BaseDirectory, "buffer.db")
  - SqliteConnection? _db

CONSTRUCTOR:
  InitializeDb()

METHOD InitializeDb():
  _db = new SqliteConnection($"Data Source={_dbPath}");
  _db.Open();
  Execute SQL:
    CREATE TABLE IF NOT EXISTS buffered_logs (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      client_id TEXT NOT NULL,
      event_type INTEGER NOT NULL,
      details TEXT NOT NULL,
      timestamp TEXT NOT NULL,
      username TEXT NOT NULL
    );

METHOD void Add(ActivityLogDto log):
  _buffer.Enqueue(log);
  if (_buffer.Count > 5000) OverflowToDb();

METHOD ActivityLogDto[] FlushBatch(int maxCount = 500):
  var results = new List<ActivityLogDto>();
  while (results.Count < maxCount && _buffer.TryDequeue(out var log))
    results.Add(log);
  // If buffer empty and we need more, pull from SQLite:
  if (results.Count < maxCount)
    results.AddRange(ReadFromDb(maxCount - results.Count));
  return results.ToArray();

METHOD void OverflowToDb():
  // Dequeue half the buffer into SQLite
  var toStore = new List<ActivityLogDto>();
  int count = _buffer.Count / 2;
  for (int i = 0; i < count && _buffer.TryDequeue(out var log); i++)
    toStore.Add(log);
  BulkInsertToDb(toStore);

METHOD void PersistAll():
  // Called on shutdown — dump everything to SQLite
  var all = new List<ActivityLogDto>();
  while (_buffer.TryDequeue(out var log)) all.Add(log);
  BulkInsertToDb(all);

METHOD void PurgeOldLogs(int maxDays):
  Execute SQL: DELETE FROM buffered_logs WHERE timestamp < @cutoff

METHOD void Dispose():
  PersistAll();
  _db?.Dispose();
```

### `Services/SystemInfoService.cs`

```
CLASS: SystemInfoService

METHOD SystemInfoDto Collect():
  Return new SystemInfoDto populated with:
  - ClientId: from config or Environment.MachineName
  - Hostname: Environment.MachineName
  - OsVersion: $"{Environment.OSVersion} {RuntimeInformation.OSDescription}"
  - CpuName: WMI query "SELECT Name FROM Win32_Processor" → first result
  - CpuCores: Environment.ProcessorCount
  - TotalMemoryMb: WMI "SELECT TotalVisibleMemorySize FROM Win32_OperatingSystem" / 1024
  - FreeMemoryMb: WMI "SELECT FreePhysicalMemory FROM Win32_OperatingSystem" / 1024
  - TotalDiskMb: DriveInfo("C:").TotalSize / (1024*1024)
  - FreeDiskMb: DriveInfo("C:").AvailableFreeSpace / (1024*1024)
  - InstalledSoftware: Registry HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall
      → read DisplayName from each subkey, filter non-empty, take first 200
  - RunningProcesses: Process.GetProcesses().Select(p => p.ProcessName).Distinct().OrderBy(n => n).ToArray()
  - CurrentUser: Environment.UserName
  - IpAddress: Dns.GetHostEntry(Dns.GetHostName()).AddressList.FirstOrDefault(a => a.AddressFamily == IPv4)?.ToString()
```

### `Helpers/NativeMethods.cs`

```csharp
// P/Invoke declarations needed by KeystrokeLoggerService and WindowMonitorService:

public static class NativeMethods
{
    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_SYSKEYDOWN = 0x0104;

    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")] public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("kernel32.dll")] public static extern IntPtr GetModuleHandle(string lpModuleName);
    [DllImport("user32.dll")] public static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int x;
        public int y;
    }
}
```

### `Helpers/KeyMapper.cs`

```
CLASS: KeyMapper

STATIC METHOD string Map(uint vkCode) → string:
  Map virtual key codes to readable strings:
  - 0x08 → "[Backspace]"
  - 0x09 → "[Tab]"
  - 0x0D → "[Enter]"
  - 0x1B → "[Esc]"
  - 0x20 → " "
  - 0x2E → "[Delete]"
  - 0x24 → "[Home]"
  - 0x23 → "[End]"
  - 0x21 → "[PageUp]"
  - 0x22 → "[PageDown]"
  - 0x25 → "[Left]"
  - 0x26 → "[Up]"
  - 0x27 → "[Right]"
  - 0x28 → "[Down]"
  - 0x10 → "[Shift]"
  - 0x11 → "[Ctrl]"
  - 0x12 → "[Alt]"
  - 0x5B → "[Win]"
  - 0x70-0x7B → "[F1]"-"[F12]"
  - 0x30-0x39 → "0"-"9"
  - 0x41-0x5A → "a"-"z" (lowercase by default, uppercase if shift held)
  - 0xBA-0xE2 → map OEM keys based on keyboard layout
  - For all printable characters, use ToUnicode() Win32 API for accurate mapping
  - Unknown → "[0xNN]"
```

---

## 3. Server — `LabLock.Server`

### `LabLock.Server.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="8.*" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="8.*" />
    <PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="8.*" />
    <PackageReference Include="BCrypt.Net-Next" Version="4.*" />
    <ProjectReference Include="..\LabLock.Shared\LabLock.Shared.csproj" />
  </ItemGroup>
</Project>
```

### `appsettings.json` (Server)

```json
{
  "ConnectionStrings": {
    "Default": "Data Source=lablock.db"
  },
  "LabLock": {
    "ApiKey": "CHANGE_ME_SERVER_MASTER_KEY",
    "DashboardUsername": "admin",
    "DashboardPassword": "admin",
    "JwtSecret": "CHANGE_ME_MIN_32_CHARS_LONG_RANDOM_STRING_HERE",
    "MaxLogRetentionDays": 90,
    "CommandTimeoutSeconds": 120
  },
  "Ai": {
    "Provider": "none",
    "ApiKey": "",
    "Model": "",
    "BaseUrl": "",
    "MaxTokens": 4096,
    "Temperature": 0.3,
    "SystemPrompt": ""
  },
  "Kestrel": {
    "Endpoints": {
      "Http": {
        "Url": "http://0.0.0.0:5000"
      }
    }
  }
}
```

### `Program.cs` (Server)

```
IMPLEMENTATION SPEC:

var builder = WebApplication.CreateBuilder(args);

// 1. Services
builder.Services.AddSignalR(options => {
    options.MaximumReceiveMessageSize = 1024 * 1024;  // 1MB for large command outputs
    options.EnableDetailedErrors = true;
});

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddSingleton<ClientStateService>();
builder.Services.AddScoped<LogStorageService>();
builder.Services.AddScoped<CommandService>();
builder.Services.AddSingleton<AiProviderFactory>();
builder.Services.AddSingleton<AiToolExecutor>();
builder.Services.AddSingleton<AiChatService>();
builder.Services.AddHttpClient();  // for AI providers

builder.Services.AddControllers();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options => {
        var key = Encoding.UTF8.GetBytes(builder.Configuration["LabLock:JwtSecret"]!);
        options.TokenValidationParameters = new TokenValidationParameters {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(key)
        };
    });

builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

// 2. Build
var app = builder.Build();

// 3. Apply migrations on startup
using (var scope = app.Services.CreateScope()) {
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

// 4. Middleware pipeline
app.UseCors();
app.UseDefaultFiles();     // serves index.html for /
app.UseStaticFiles();      // serves wwwroot/
app.UseAuthentication();
app.UseAuthorization();

// 5. Endpoints
app.MapHub<ClientHub>("/hub/client");
app.MapControllers();

app.Run();
```

### `Hubs/ClientHub.cs`

```
CLASS: ClientHub : Hub

DEPENDENCIES (inject via constructor):
  - ClientStateService _clientState
  - IServiceScopeFactory _scopeFactory   (to get scoped services like LogStorageService)
  - ILogger<ClientHub> _logger
  - IConfiguration _config

OVERRIDE OnConnectedAsync():
  var httpContext = Context.GetHttpContext();
  var clientId = httpContext.Request.Query["clientId"].ToString();
  var apiKey = httpContext.Request.Query["apiKey"].ToString();
  var role = httpContext.Request.Query["role"].ToString();
  
  // Dashboard connections
  if (role == "dashboard") {
    // Validate JWT from query: httpContext.Request.Query["token"]
    await Groups.AddToGroupAsync(Context.ConnectionId, "dashboard");
    return;
  }
  
  // Client connections
  if (string.IsNullOrEmpty(clientId)) { Context.Abort(); return; }
  var expectedKey = _config["LabLock:ApiKey"];
  if (apiKey != expectedKey) { Context.Abort(); return; }
  
  // Store connection mapping
  Context.Items["clientId"] = clientId;
  _logger.LogInformation("Client connected: {ClientId}", clientId);

OVERRIDE OnDisconnectedAsync(Exception? exception):
  if (Context.Items.TryGetValue("clientId", out var clientIdObj)) {
    var clientId = clientIdObj.ToString()!;
    _clientState.SetOffline(clientId);
    await Clients.Group("dashboard").SendAsync("ClientDisconnected", clientId);
    _logger.LogInformation("Client disconnected: {ClientId}", clientId);
  }

[HubMethodName("RegisterClient")]
async Task RegisterClient(ClientRegistrationDto dto):
  _clientState.Register(dto, Context.ConnectionId);
  // Also persist to DB:
  using var scope = _scopeFactory.CreateScope();
  var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
  var existing = await db.Clients.FindAsync(dto.ClientId);
  if (existing == null) {
    db.Clients.Add(new Client { ClientId = dto.ClientId, Hostname = dto.Hostname, ... FirstSeen = DateTime.UtcNow, LastSeen = DateTime.UtcNow });
  } else {
    existing.Hostname = dto.Hostname; existing.IpAddress = dto.IpAddress; ... existing.LastSeen = DateTime.UtcNow;
  }
  await db.SaveChangesAsync();
  await Clients.Group("dashboard").SendAsync("ClientConnected", _clientState.GetClient(dto.ClientId));

[HubMethodName("Heartbeat")]
async Task Heartbeat(HeartbeatDto dto):
  _clientState.UpdateHeartbeat(dto);
  await Clients.Group("dashboard").SendAsync("ClientHeartbeat", dto);

[HubMethodName("SendActivityBatch")]
async Task SendActivityBatch(ActivityLogDto[] logs):
  using var scope = _scopeFactory.CreateScope();
  var storage = scope.ServiceProvider.GetRequiredService<LogStorageService>();
  await storage.BulkInsertAsync(logs);

[HubMethodName("ReportCommandResult")]
async Task ReportCommandResult(CommandResultDto result):
  using var scope = _scopeFactory.CreateScope();
  var cmdService = scope.ServiceProvider.GetRequiredService<CommandService>();
  await cmdService.UpdateResultAsync(result.CommandId, result);
  await Clients.Group("dashboard").SendAsync("CommandResult", result);
```

### `Services/ClientStateService.cs`

```
CLASS: ClientStateService (Singleton, thread-safe)

INNER CLASS ClientState:
  string ClientId
  string Hostname
  string IpAddress
  string OsVersion
  string AgentVersion
  string CurrentUser
  string ActiveWindow
  string ActiveProcess
  double CpuPercent
  double MemoryPercent
  bool IsOnline
  DateTime LastHeartbeat
  DateTime FirstSeen
  string ConnectionId     // SignalR connection ID

FIELDS:
  ConcurrentDictionary<string, ClientState> _clients = new()

METHODS:
  void Register(ClientRegistrationDto dto, string connectionId):
    _clients.AddOrUpdate(dto.ClientId, 
      new ClientState { ClientId = dto.ClientId, ..., IsOnline = true, ConnectionId = connectionId, FirstSeen = DateTime.UtcNow },
      (key, existing) => { existing.IsOnline = true; existing.ConnectionId = connectionId; existing.LastHeartbeat = DateTime.UtcNow; return existing; });

  void UpdateHeartbeat(HeartbeatDto dto):
    if (_clients.TryGetValue(dto.ClientId, out var state)) {
      state.CurrentUser = dto.CurrentUser;
      state.ActiveWindow = dto.ActiveWindow;
      state.ActiveProcess = dto.ActiveProcess;
      state.CpuPercent = dto.CpuPercent;
      state.MemoryPercent = dto.MemoryPercent;
      state.LastHeartbeat = DateTime.UtcNow;
      state.IsOnline = true;
    }

  void SetOffline(string clientId):
    if (_clients.TryGetValue(clientId, out var state)) {
      state.IsOnline = false;
    }

  ClientState? GetClient(string clientId) → _clients.GetValueOrDefault(clientId)
  List<ClientState> GetAllClients() → _clients.Values.ToList()
  string? GetConnectionId(string clientId) → GetClient(clientId)?.ConnectionId
  int OnlineCount => _clients.Values.Count(c => c.IsOnline)
  int TotalCount => _clients.Count
```

### `Services/LogStorageService.cs`

```
CLASS: LogStorageService (Scoped)

DEPENDENCIES:
  - AppDbContext _db

METHOD async Task BulkInsertAsync(ActivityLogDto[] logs):
  var entities = logs.Select(l => new ActivityLog {
    ClientId = l.ClientId,
    EventType = l.EventType,
    Details = l.Details,
    Timestamp = l.Timestamp,
    Username = l.Username
  });
  _db.ActivityLogs.AddRange(entities);
  await _db.SaveChangesAsync();

METHOD async Task<(List<ActivityLog> Data, int Total)> QueryAsync(
    string? clientId, EventType? eventType, DateTime? from, DateTime? to,
    string? keyword, int page = 1, int pageSize = 50):
  
  var query = _db.ActivityLogs.AsQueryable();
  if (clientId != null) query = query.Where(l => l.ClientId == clientId);
  if (eventType != null) query = query.Where(l => l.EventType == eventType);
  if (from != null) query = query.Where(l => l.Timestamp >= from);
  if (to != null) query = query.Where(l => l.Timestamp <= to);
  if (keyword != null) query = query.Where(l => l.Details.Contains(keyword));
  
  var total = await query.CountAsync();
  var data = await query
    .OrderByDescending(l => l.Timestamp)
    .Skip((page - 1) * pageSize)
    .Take(pageSize)
    .ToListAsync();
  
  return (data, total);

METHOD async Task<Dictionary<string, int>> GetEventCountsAsync(string? clientId, DateTime from, DateTime to):
  var query = _db.ActivityLogs.Where(l => l.Timestamp >= from && l.Timestamp <= to);
  if (clientId != null) query = query.Where(l => l.ClientId == clientId);
  return await query
    .GroupBy(l => l.EventType)
    .ToDictionaryAsync(g => g.Key.ToString(), g => g.Count());

METHOD async Task PurgeOldLogsAsync(int retentionDays):
  var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
  await _db.ActivityLogs.Where(l => l.Timestamp < cutoff).ExecuteDeleteAsync();
```

### `Services/CommandService.cs`

```
CLASS: CommandService (Scoped)

DEPENDENCIES:
  - AppDbContext _db
  - IHubContext<ClientHub> _hubContext
  - ClientStateService _clientState
  - ILogger

STATIC FIELD:
  - ConcurrentDictionary<int, TaskCompletionSource<CommandResultDto>> _pendingCommands = new()

METHOD async Task<int> SendCommandAsync(string clientId, string command, string sentBy, int timeoutSec = 60):
  // 1. Verify client is online
  var connId = _clientState.GetConnectionId(clientId);
  if (connId == null) throw new InvalidOperationException($"Client {clientId} is not online");
  
  // 2. Store in DB
  var entry = new CommandHistory {
    ClientId = clientId,
    Command = command,
    Status = CommandStatus.Pending,
    SentAt = DateTime.UtcNow,
    SentBy = sentBy
  };
  _db.CommandHistory.Add(entry);
  await _db.SaveChangesAsync();
  
  // 3. Send to client via SignalR
  var request = new CommandRequestDto { Command = command, TimeoutSeconds = timeoutSec };
  await _hubContext.Clients.Client(connId).SendAsync("ExecuteCommand", entry.Id, request);
  
  return entry.Id;

METHOD async Task UpdateResultAsync(int commandId, CommandResultDto result):
  var entry = await _db.CommandHistory.FindAsync(commandId);
  if (entry == null) return;
  entry.Output = result.Output;
  entry.Error = result.Error;
  entry.ExitCode = result.ExitCode;
  entry.Status = result.ExitCode == 0 ? CommandStatus.Completed : CommandStatus.Failed;
  entry.CompletedAt = DateTime.UtcNow;
  await _db.SaveChangesAsync();
  
  // Notify any waiters (for AI tool executor)
  if (_pendingCommands.TryRemove(commandId, out var tcs))
    tcs.TrySetResult(result);

METHOD async Task<string> ExecuteAndWaitAsync(string clientId, string command, int timeoutSec = 60):
  var tcs = new TaskCompletionSource<CommandResultDto>();
  var commandId = await SendCommandAsync(clientId, command, "ai", timeoutSec);
  _pendingCommands[commandId] = tcs;
  
  using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec + 5));
  cts.Token.Register(() => tcs.TrySetCanceled());
  
  try {
    var result = await tcs.Task;
    var output = result.Output;
    if (!string.IsNullOrEmpty(result.Error)) output += $"\n\nSTDERR:\n{result.Error}";
    return output;
  } catch (TaskCanceledException) {
    _pendingCommands.TryRemove(commandId, out _);
    return "ERROR: Command timed out";
  }

METHOD async Task<List<CommandHistory>> GetHistoryAsync(string? clientId, int limit = 50):
  var query = _db.CommandHistory.AsQueryable();
  if (clientId != null) query = query.Where(c => c.ClientId == clientId);
  return await query.OrderByDescending(c => c.SentAt).Take(limit).ToListAsync();

METHOD async Task<CommandHistory?> GetByIdAsync(int id):
  return await _db.CommandHistory.FindAsync(id);
```

### `Controllers/AuthController.cs`

```
[Route("api/auth")]
[ApiController]

POST /api/auth/login
  Request body: { "username": string, "password": string }
  
  Implementation:
    1. Look up DashboardUser by username
    2. If not found, check against config LabLock:DashboardUsername/DashboardPassword (bootstrap login)
       - If bootstrap matches, create DashboardUser in DB with BCrypt hashed password
    3. Verify password with BCrypt.Net.BCrypt.Verify(password, user.PasswordHash)
    4. If invalid → return 401 { error: "Invalid credentials" }
    5. Generate JWT:
       var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["LabLock:JwtSecret"]));
       var token = new JwtSecurityToken(
         expires: DateTime.UtcNow.AddHours(24),
         claims: [new Claim("username", username)],
         signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
       );
    6. Return 200 { "token": tokenString }
```

### `Controllers/ClientsController.cs`

```
[Route("api/clients")]
[ApiController]
[Authorize]

GET /api/clients
  → return _clientState.GetAllClients()

GET /api/clients/{clientId}
  → return _clientState.GetClient(clientId) or 404

GET /api/clients/{clientId}/system-info
  → Get connectionId from _clientState
  → Invoke "GetSystemInfo" on client via IHubContext
  → Wait for response (with timeout)
  → Return SystemInfoDto
```

### `Controllers/LogsController.cs`

```
[Route("api/logs")]
[ApiController]
[Authorize]

GET /api/logs?clientId=&eventType=&from=&to=&keyword=&page=1&pageSize=50
  → call LogStorageService.QueryAsync(...)
  → return { data: [...], total: int, page: int, pageSize: int }

GET /api/logs/stats?clientId=&from=&to=
  → call LogStorageService.GetEventCountsAsync(...)
  → return { keystroke: N, processStart: N, ... }
```

### `Controllers/CommandsController.cs`

```
[Route("api/commands")]
[ApiController]
[Authorize]

POST /api/commands
  Request: { "clientId": "PC-001", "command": "Get-Process", "timeoutSeconds": 60 }
  → call CommandService.SendCommandAsync(clientId, command, "admin", timeout)
  → return { commandId: N }

POST /api/commands/broadcast
  Request: { "command": "Get-Process", "timeoutSeconds": 60 }
  → for each online client, call SendCommandAsync
  → return { commandIds: [N, N, ...] }

GET /api/commands/{commandId}
  → CommandService.GetByIdAsync(id)
  → return CommandHistory or 404

GET /api/commands/history?clientId=&limit=50
  → CommandService.GetHistoryAsync(clientId, limit)
  → return [CommandHistory]
```

---

## 4. Web Dashboard

### Design System

Use these exact design tokens across all CSS:

```css
:root {
  /* Colors */
  --bg-primary: #0a0a0f;
  --bg-surface: #12121a;
  --bg-elevated: #1a1a2e;
  --bg-hover: #222238;
  --color-primary: #6c5ce7;
  --color-primary-light: #a29bfe;
  --color-accent: #00cec9;
  --color-success: #00b894;
  --color-warning: #fdcb6e;
  --color-danger: #ff7675;
  --color-text: #e2e2e2;
  --color-text-muted: #6c6c80;
  --color-border: rgba(255, 255, 255, 0.06);

  /* Typography */
  --font-family: 'Inter', -apple-system, BlinkMacSystemFont, sans-serif;
  --font-mono: 'JetBrains Mono', 'Fira Code', 'Consolas', monospace;

  /* Spacing */
  --radius-sm: 8px;
  --radius-md: 12px;
  --radius-lg: 20px;
  --radius-full: 9999px;

  /* Effects */
  --glass-bg: rgba(255, 255, 255, 0.03);
  --glass-blur: blur(20px);
  --shadow-sm: 0 2px 8px rgba(0, 0, 0, 0.3);
  --shadow-md: 0 4px 16px rgba(0, 0, 0, 0.4);
  --shadow-lg: 0 8px 32px rgba(0, 0, 0, 0.5);
  --transition: all 0.2s ease;
}
```

Google Fonts link: `https://fonts.googleapis.com/css2?family=Inter:wght@300;400;500;600;700&family=JetBrains+Mono:wght@400;500&display=swap`

### `login.html`

```
Full-page dark background (#0a0a0f).
Centered vertically and horizontally:
  - Glass card (glass-bg + glass-blur, border: 1px solid var(--color-border), radius-md)
  - Inside card:
    - Logo/Title: "🔒 LabLock" in large font, color-primary
    - Subtitle: "Lab PC Management System" in text-muted
    - 30px gap
    - Username input (dark bg, rounded, focus: primary border glow)
    - Password input (same style, type="password")
    - 20px gap
    - "Sign In" button (bg: color-primary, full width, rounded, hover: lighten, transition)
    - Error message area (color-danger, hidden by default)
  
  JS behavior:
    - On submit → POST /api/auth/login with {username, password}
    - On success → store token in localStorage("lablock_token"), redirect to index.html
    - On error → show error message, shake animation on card
```

### `index.html`

```html
<!-- Structure (implement exactly): -->
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>LabLock Dashboard</title>
  <link href="https://fonts.googleapis.com/css2?family=Inter:wght@300;400;500;600;700&family=JetBrains+Mono:wght@400;500&display=swap" rel="stylesheet">
  <link rel="stylesheet" href="css/app.css">
  <link rel="stylesheet" href="css/terminal.css">
</head>
<body>
  <!-- Sidebar Navigation -->
  <nav id="sidebar">
    <div class="sidebar-logo">🔒 LabLock</div>
    <div class="sidebar-nav">
      <a href="#overview" class="nav-item active" data-page="overview">
        <span class="nav-icon">📊</span> Overview
      </a>
      <a href="#terminal" class="nav-item" data-page="terminal">
        <span class="nav-icon">💻</span> Terminal
      </a>
      <a href="#activity" class="nav-item" data-page="activity">
        <span class="nav-icon">📋</span> Activity Log
      </a>
      <a href="#ai-chat" class="nav-item" data-page="ai-chat">
        <span class="nav-icon">🤖</span> AI Chat
      </a>
      <a href="#settings" class="nav-item" data-page="settings">
        <span class="nav-icon">⚙️</span> Settings
      </a>
    </div>
    <div class="sidebar-footer">
      <div class="online-badge">
        <span class="pulse-dot"></span>
        <span id="online-count">0</span> Online
      </div>
      <button id="logout-btn" class="btn-ghost">Logout</button>
    </div>
  </nav>

  <!-- Main Content Area -->
  <main id="content">
    <!-- Pages rendered here by router.js -->
  </main>

  <!-- Toast Container -->
  <div id="toast-container"></div>

  <!-- Scripts -->
  <script src="js/signalr.min.js"></script>
  <script src="js/api.js"></script>
  <script src="js/components/toast.js"></script>
  <script src="js/components/pc-card.js"></script>
  <script src="js/components/log-table.js"></script>
  <script src="js/components/chat-message.js"></script>
  <script src="js/pages/overview.js"></script>
  <script src="js/pages/terminal.js"></script>
  <script src="js/pages/pc-detail.js"></script>
  <script src="js/pages/activity-log.js"></script>
  <script src="js/pages/ai-chat.js"></script>
  <script src="js/pages/settings.js"></script>
  <script src="js/router.js"></script>
  <script src="js/app.js"></script>
</body>
</html>
```

### `css/app.css`

Design requirements — implement ALL of these:

```
GLOBAL:
  - body: bg-primary, font-family, color-text, margin 0, overflow hidden
  - *, *::before, *::after: box-sizing border-box
  - Scrollbar styling: thin, bg-surface track, color-primary thumb

LAYOUT:
  - #sidebar: fixed left, width 240px, height 100vh, bg-surface, border-right color-border
    - Flex column, padding 20px
    - .sidebar-logo: font-size 1.5rem, font-weight 700, color-primary, margin-bottom 40px
    - .nav-item: padding 12px 16px, radius-sm, color text-muted, transition
      - hover: bg-hover, color-text
      - .active: bg with primary at 15% opacity, color-primary-light
    - .sidebar-footer: margin-top auto, border-top color-border
    - .online-badge: flex, align-items center, gap 8px
    - .pulse-dot: 8px circle, bg-success, animate pulse (scale 1→1.5→1, opacity 1→0.5→1)
  
  - #content: margin-left 240px, height 100vh, overflow-y auto, padding 32px

CARDS:
  - .card: bg-surface, radius-md, border 1px color-border, shadow-sm, transition
    - hover: transform translateY(-2px), shadow-md, border-color primary at 30%
  - .card-glass: glass-bg, glass-blur, border 1px color-border

BUTTONS:
  - .btn-primary: bg color-primary, color white, padding 10px 20px, radius-sm, border none, cursor pointer
    - hover: lighten 10%, transform scale(1.02)
  - .btn-ghost: bg transparent, color text-muted, border 1px color-border
    - hover: color-text, border-color text-muted
  - .btn-danger: bg color-danger, color white

INPUTS:
  - .input: bg bg-elevated, color text, border 1px color-border, padding 10px 14px, radius-sm
    - focus: border-color color-primary, box-shadow 0 0 0 3px primary at 20%, outline none
  - .select: same as input, appearance none, custom dropdown arrow

BADGES / PILLS:
  - .badge: display inline-flex, padding 4px 10px, radius-full, font-size 0.75rem, font-weight 500
  - .badge-success: bg success at 15%, color success
  - .badge-danger: bg danger at 15%, color danger
  - .badge-warning: bg warning at 15%, color warning
  - .badge-primary: bg primary at 15%, color primary-light

TABLES:
  - .table: width 100%, border-collapse collapse
  - .table th: bg bg-elevated, padding 12px 16px, text-align left, color text-muted, font-weight 500
  - .table td: padding 12px 16px, border-bottom 1px color-border
  - .table tr:hover: bg bg-hover

STATS BAR:
  - .stats-grid: display grid, grid-template-columns repeat(auto-fit, minmax(200px, 1fr)), gap 16px
  - .stat-card: card style, padding 20px, text-align center
    - .stat-value: font-size 2rem, font-weight 700
    - .stat-label: font-size 0.85rem, color text-muted

PC GRID:
  - .pc-grid: display grid, grid-template-columns repeat(auto-fill, minmax(220px, 1fr)), gap 16px

ANIMATIONS:
  @keyframes pulse { 0%,100% { transform: scale(1); opacity: 1; } 50% { transform: scale(1.5); opacity: 0.5; } }
  @keyframes slideIn { from { transform: translateY(-20px); opacity: 0; } to { transform: translateY(0); opacity: 1; } }
  @keyframes fadeIn { from { opacity: 0; } to { opacity: 1; } }
  .animate-slide-in { animation: slideIn 0.3s ease; }

TOAST:
  - #toast-container: fixed top-right, z-index 10000
  - .toast: card style, padding 12px 20px, margin-bottom 8px, animation slideIn
    - .toast-success: left border 3px success
    - .toast-error: left border 3px danger

RESPONSIVE:
  @media (max-width: 768px) { #sidebar { width 60px; text hidden } #content { margin-left 60px } }
```

### `css/terminal.css`

```
.terminal-container:
  bg: #0d1117 (GitHub dark)
  border: 1px color-border
  radius-md
  overflow: hidden
  height: calc(100vh - 200px)
  display: flex
  flex-direction: column

.terminal-header:
  bg: bg-elevated
  padding: 8px 16px
  border-bottom: 1px color-border
  display: flex
  align-items: center
  gap: 12px
  - Three dots (red, yellow, green circles, 12px each)
  - PC selector dropdown
  - Status indicator

.terminal-output:
  flex: 1
  overflow-y: auto
  padding: 16px
  font-family: var(--font-mono)
  font-size: 14px
  line-height: 1.6
  white-space: pre-wrap
  word-break: break-all
  
  .output-stdout: color #e6edf3
  .output-stderr: color var(--color-danger)
  .output-system: color var(--color-warning), font-style italic
  .output-prompt: color var(--color-accent)

.terminal-input:
  display: flex
  align-items: center
  padding: 8px 16px
  border-top: 1px color-border
  bg: #0d1117
  
  .prompt-symbol: color var(--color-accent), font-family mono, margin-right 8px, content "PS>"
  input: flex 1, bg transparent, color text, border none, font-family mono, font-size 14px, outline none
```

### `js/api.js`

```javascript
// IMPLEMENT EXACTLY:

const API = {
  getToken() {
    return localStorage.getItem('lablock_token');
  },

  async get(path) {
    const res = await fetch(path, {
      headers: { 'Authorization': `Bearer ${this.getToken()}` }
    });
    if (res.status === 401) { window.location.href = 'login.html'; return null; }
    return res.json();
  },

  async post(path, body) {
    const res = await fetch(path, {
      method: 'POST',
      headers: {
        'Authorization': `Bearer ${this.getToken()}`,
        'Content-Type': 'application/json'
      },
      body: JSON.stringify(body)
    });
    if (res.status === 401) { window.location.href = 'login.html'; return null; }
    return res.json();
  },

  async put(path, body) {
    const res = await fetch(path, {
      method: 'PUT',
      headers: {
        'Authorization': `Bearer ${this.getToken()}`,
        'Content-Type': 'application/json'
      },
      body: JSON.stringify(body)
    });
    if (res.status === 401) { window.location.href = 'login.html'; return null; }
    return res.json();
  },

  async del(path) {
    const res = await fetch(path, {
      method: 'DELETE',
      headers: { 'Authorization': `Bearer ${this.getToken()}` }
    });
    if (res.status === 401) { window.location.href = 'login.html'; return null; }
    return res.ok;
  },

  // SSE streaming for AI chat
  async stream(path, body, onEvent) {
    const res = await fetch(path, {
      method: 'POST',
      headers: {
        'Authorization': `Bearer ${this.getToken()}`,
        'Content-Type': 'application/json'
      },
      body: JSON.stringify(body)
    });
    const reader = res.body.getReader();
    const decoder = new TextDecoder();
    let buffer = '';
    
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      buffer += decoder.decode(value, { stream: true });
      const lines = buffer.split('\n');
      buffer = lines.pop();
      for (const line of lines) {
        if (line.startsWith('data: ')) {
          const data = JSON.parse(line.slice(6));
          onEvent(data);
        }
      }
    }
  }
};
```

### `js/router.js`

```javascript
// Hash-based SPA router. IMPLEMENT EXACTLY:

const Router = {
  routes: {
    'overview': OverviewPage,
    'terminal': TerminalPage,
    'pc': PcDetailPage,       // #pc/{clientId}
    'activity': ActivityLogPage,
    'ai-chat': AiChatPage,
    'settings': SettingsPage
  },

  init() {
    window.addEventListener('hashchange', () => this.navigate());
    this.navigate();
  },

  navigate() {
    const hash = window.location.hash.slice(1) || 'overview';
    const [page, ...params] = hash.split('/');
    const container = document.getElementById('content');
    
    // Update active nav
    document.querySelectorAll('.nav-item').forEach(el => {
      el.classList.toggle('active', el.dataset.page === page);
    });

    // Render page
    const PageModule = this.routes[page];
    if (PageModule) {
      container.innerHTML = '';
      PageModule.render(container, ...params);
    }
  }
};
```

### `js/app.js`

```javascript
// Main initialization. IMPLEMENT EXACTLY:

(async function() {
  // 1. Check auth
  if (!API.getToken()) {
    window.location.href = 'login.html';
    return;
  }

  // 2. Connect SignalR for real-time dashboard updates
  const connection = new signalR.HubConnectionBuilder()
    .withUrl(`/hub/client?role=dashboard&token=${API.getToken()}`)
    .withAutomaticReconnect()
    .build();

  connection.on('ClientConnected', (client) => {
    window.dispatchEvent(new CustomEvent('client-connected', { detail: client }));
    Toast.show(`${client.hostname} connected`, 'success');
  });

  connection.on('ClientDisconnected', (clientId) => {
    window.dispatchEvent(new CustomEvent('client-disconnected', { detail: clientId }));
    Toast.show(`${clientId} disconnected`, 'error');
  });

  connection.on('ClientHeartbeat', (heartbeat) => {
    window.dispatchEvent(new CustomEvent('client-heartbeat', { detail: heartbeat }));
  });

  connection.on('CommandResult', (result) => {
    window.dispatchEvent(new CustomEvent('command-result', { detail: result }));
  });

  await connection.start();
  window.signalrConnection = connection;

  // 3. Update online count periodically
  async function updateOnlineCount() {
    const clients = await API.get('/api/clients');
    if (clients) {
      const online = clients.filter(c => c.isOnline).length;
      document.getElementById('online-count').textContent = online;
    }
  }
  updateOnlineCount();
  setInterval(updateOnlineCount, 30000);

  // 4. Logout handler
  document.getElementById('logout-btn').addEventListener('click', () => {
    localStorage.removeItem('lablock_token');
    window.location.href = 'login.html';
  });

  // 5. Initialize router
  Router.init();
})();
```

### `js/pages/overview.js` — Brief Spec

```
const OverviewPage = {
  render(container) {
    1. Fetch GET /api/clients
    2. Render stats bar: Online count, Offline count, Idle count (CPU < 5%), Total count
    3. Render search bar + filter dropdown (All / Online / Offline / Idle)
    4. Render PC grid using PcCard.render() for each client
    5. Listen for 'client-heartbeat' events to update cards in real-time
    6. Listen for 'client-connected' / 'client-disconnected' to add/remove cards
    7. Search filters client list by hostname or current user
    8. Click on card → navigate to #pc/{clientId}
  }
};
```

### `js/pages/terminal.js` — Brief Spec

```
const TerminalPage = {
  render(container) {
    1. Render terminal UI: header with PC dropdown, output area, input area
    2. Fetch online clients for dropdown
    3. On command submit:
       a. Display command in output area with prompt styling
       b. POST /api/commands { clientId, command }
       c. Listen for 'command-result' event matching commandId
       d. Display output (stdout white, stderr red)
    4. Support command history: Up/Down arrow keys cycle through previous commands
    5. Support "Broadcast" toggle → POST /api/commands/broadcast
    6. Auto-scroll output area to bottom
    7. Clear button to reset output
  }
};
```

### `js/pages/activity-log.js` — Brief Spec

```
const ActivityLogPage = {
  render(container) {
    1. Render filter bar: PC dropdown, event type dropdown, date picker, search input
    2. Fetch GET /api/logs with current filters
    3. Render table with columns: Time, PC, Type (colored badge), User, Details (truncated)
    4. Click row → expand to show full JSON details
    5. Pagination controls at bottom
    6. "Export CSV" button → POST /api/logs/export
  }
};
```

### `js/pages/pc-detail.js` — Brief Spec

```
const PcDetailPage = {
  render(container, clientId) {
    1. Fetch client info from /api/clients/{clientId}
    2. Render header with hostname, status, IP, OS info
    3. "Open Terminal" button → navigate to #terminal (pre-select this PC)
    4. Stats cards: CPU, Memory, Disk, Uptime
    5. Recent activity table (last 50 logs for this PC)
    6. "Get System Info" button → fetch /api/clients/{clientId}/system-info → display in modal
  }
};
```

### `js/pages/ai-chat.js` — Full Spec

```
const AiChatPage = {
  currentConversationId: null,
  messages: [],

  render(container) {
    1. Build layout:
       - Left panel (70% width): chat area
         - Chat history (scrollable)
         - Input bar at bottom
       - Right panel (30% width): context sidebar
         - Online PC count
         - Recent commands
         - AI provider status
         - "New Chat" button
         - Conversation history list

    2. Load conversations: GET /api/ai/conversations
       Display in right panel as clickable list

    3. If no conversation selected, create new one: POST /api/ai/conversations
       Set currentConversationId

    4. Load messages if existing conversation: GET /api/ai/conversations/{id}
       Render each message using ChatMessage.render()

    5. On message submit:
       a. Render user message bubble (align right, primary bg)
       b. Create empty assistant message bubble (align left)
       c. Call API.stream('/api/ai/chat', { conversationId, message }, onEvent)
       d. For each event:
          - type "text": append content to assistant bubble, auto-scroll
          - type "tool_call": show action indicator in bubble:
            "⚡ Calling: {toolName}({args})" in a collapsible block
          - type "tool_executing": show spinner in action block
          - type "tool_result": show result in collapsible code block under the action
          - type "done": finalize message, stop loading indicator
          - type "error": show error in red
       e. Parse markdown in assistant messages (bold, code blocks, tables, lists)

    6. "Configure" button in header → navigate to #settings

    7. "New Chat" button:
       a. POST /api/ai/conversations → get new id
       b. Clear chat area
       c. Update currentConversationId
       d. Refresh conversation list

  MESSAGE BUBBLE STYLING:
    User messages:
      - Align right, max-width 70%
      - bg: color-primary, color: white
      - radius: 16px 16px 4px 16px
    
    Assistant messages:
      - Align left, max-width 80%
      - bg: bg-elevated, color: text
      - radius: 16px 16px 16px 4px
    
    Tool call blocks (inside assistant bubble):
      - bg: bg-primary (surface), border-left 3px accent
      - Collapsible: click to expand/collapse
      - Header: "⚡ execute_command on PC-LAB-001" with chevron
      - Body: pre/code block with monospace output
      - While executing: pulsing animation on header

  MARKDOWN RENDERING:
    Implement simple markdown parser for assistant messages:
    - **bold** → <strong>
    - `code` → <code>
    - ```code blocks``` → <pre><code>
    - Tables (| col | col |) → <table>
    - - list items → <ul><li>
    - 1. numbered → <ol><li>
    Do NOT use a library — implement a simple regex-based parser.
};
```

### `js/pages/settings.js` — Full Spec

```
const SettingsPage = {
  render(container) {
    1. Fetch GET /api/ai/settings

    2. Render form sections:

    === AI Configuration ===
    
    Provider dropdown:
      Options: "None (Disabled)", "OpenAI", "Anthropic (Claude)", "Google Gemini", "Ollama (Local)", "Custom (OpenAI-compatible)"
      On change → update model dropdown, base URL, show/hide API key field
    
    API Key input:
      type="password", with toggle visibility button (👁)
      Placeholder: "sk-..." or "Enter API key"
      Hidden when provider is "Ollama" or "None"
    
    Model dropdown:
      Populated based on provider:
        OpenAI: gpt-4o, gpt-4o-mini, gpt-4-turbo, gpt-3.5-turbo
        Anthropic: claude-sonnet-4-20250514, claude-3-5-haiku-20241022, claude-opus-4-20250514
        Gemini: gemini-2.5-pro, gemini-2.5-flash, gemini-2.0-flash
        Ollama: fetched from GET /api/ai/ollama-models (async)
        Custom: free text input instead of dropdown
    
    Base URL input:
      Auto-filled with default for provider, editable
      OpenAI: https://api.openai.com
      Anthropic: https://api.anthropic.com
      Gemini: https://generativelanguage.googleapis.com
      Ollama: http://localhost:11434
    
    Temperature slider/input: 0.0 — 2.0, step 0.1, default 0.3
    Max Tokens input: number, default 4096
    
    System Prompt textarea:
      Large textarea (6 rows), shows current or default prompt
      "Reset to Default" link below
    
    Buttons row:
      "Test Connection" button → POST /api/ai/test → show toast with result
      "Save" button → PUT /api/ai/settings → show success toast

    Status indicator below buttons:
      🟢 Connected to gpt-4o | 🔴 Not configured | 🟡 Testing...

    === Dashboard Settings ===
    
    Change Password:
      Current password, new password, confirm password inputs
      Save button → PUT /api/auth/password
    
    === Client Management ===
    
    Client API Key: display current key, "Regenerate" button
    Log Retention: number input (days), Save button
  }
};
```

### `js/components/pc-card.js`

```
const PcCard = {
  render(client) {
    Returns HTML string for a PC card:
    <div class="card pc-card" data-client-id="{clientId}" onclick="navigate to #pc/{clientId}">
      <div class="pc-card-header">
        <span class="status-dot {online ? 'status-online' : 'status-offline'}"></span>
        <span class="pc-hostname">{hostname}</span>
      </div>
      <div class="pc-card-body">
        <div class="pc-info-row"><span class="label">User:</span> <span>{currentUser || '—'}</span></div>
        <div class="pc-info-row"><span class="label">App:</span> <span>{activeProcess || '—'}</span></div>
        <div class="pc-info-row">
          <span class="label">CPU:</span> 
          <div class="mini-bar"><div class="mini-bar-fill" style="width:{cpuPercent}%"></div></div>
          <span>{cpuPercent}%</span>
        </div>
        <div class="pc-info-row">
          <span class="label">Mem:</span>
          <div class="mini-bar"><div class="mini-bar-fill" style="width:{memoryPercent}%"></div></div>
          <span>{memoryPercent}%</span>
        </div>
      </div>
    </div>
    
    CSS for status-dot:
      .status-online: bg success, animate pulse
      .status-offline: bg danger, no animation
    
    CSS for mini-bar:
      height 4px, bg bg-elevated, radius-full, overflow hidden
      .mini-bar-fill: bg accent, height 100%, transition width 0.3s
  }
};
```

### `js/components/toast.js`

```
const Toast = {
  show(message, type = 'info', duration = 3000) {
    const container = document.getElementById('toast-container');
    const toast = document.createElement('div');
    toast.className = `toast toast-${type} animate-slide-in`;
    toast.textContent = message;
    container.appendChild(toast);
    setTimeout(() => { toast.style.opacity = '0'; setTimeout(() => toast.remove(), 300); }, duration);
  }
};
```

### `js/components/chat-message.js`

```
const ChatMessage = {
  render(message) {
    Returns HTML for a chat message bubble.
    
    If message.role === 'user':
      <div class="chat-msg chat-msg-user">
        <div class="chat-bubble chat-bubble-user">{message.content}</div>
      </div>
    
    If message.role === 'assistant':
      <div class="chat-msg chat-msg-assistant">
        <div class="chat-avatar">🤖</div>
        <div class="chat-bubble chat-bubble-assistant">
          {parseMarkdown(message.content)}
          {if message.toolCalls: render tool call blocks}
        </div>
      </div>
    
    Tool call block:
      <div class="tool-call-block">
        <div class="tool-call-header" onclick="toggle body">
          ⚡ {toolName}({summarize args}) <span class="chevron">▼</span>
        </div>
        <div class="tool-call-body" style="display:none">
          <pre><code>{tool result}</code></pre>
        </div>
      </div>
  },

  parseMarkdown(text) {
    Simple regex-based markdown to HTML converter:
    - **bold** → <strong>bold</strong>
    - *italic* → <em>italic</em>
    - `code` → <code>code</code>
    - ```lang\ncode\n``` → <pre><code class="lang">code</code></pre>
    - \n → <br>
    - | table | → <table> parsing
    - - item → <ul><li>
    - 1. item → <ol><li>
  }
};
```

### `signalr.min.js`

Download the official Microsoft SignalR JavaScript client from:
`https://cdnjs.cloudflare.com/ajax/libs/microsoft-signalr/8.0.0/signalr.min.js`

Place in `wwwroot/js/signalr.min.js`.

---

## 5. AI Integration

### `Services/Ai/IAiProvider.cs`

```csharp
namespace LabLock.Server.Services.Ai;

public interface IAiProvider
{
    IAsyncEnumerable<AiStreamChunk> ChatStreamAsync(
        List<AiMessage> messages,
        List<AiToolDefinition> tools,
        AiConfig config,
        CancellationToken ct);
}

public class AiStreamChunk
{
    public string? TextDelta { get; set; }
    public AiToolCall? ToolCall { get; set; }
    public bool IsComplete { get; set; }
    public string? Error { get; set; }
}

public class AiToolCall
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public Dictionary<string, object> Arguments { get; set; } = new();
}

public class AiToolDefinition
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public Dictionary<string, AiToolParameter> Parameters { get; set; } = new();
    public List<string> Required { get; set; } = new();
}

public class AiToolParameter
{
    public string Type { get; set; } = "string";
    public string Description { get; set; } = "";
    public string[]? EnumValues { get; set; }
}

public class AiConfig
{
    public string Model { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public double Temperature { get; set; } = 0.3;
    public int MaxTokens { get; set; } = 4096;
}

public class AiMessage
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
    public List<AiToolCall>? ToolCalls { get; set; }
    public string? ToolCallId { get; set; }
}
```

### `Services/Ai/Providers/OpenAiProvider.cs`

```
CLASS: OpenAiProvider : IAiProvider

CONSTRUCTOR: OpenAiProvider(IHttpClientFactory httpClientFactory)

METHOD IAsyncEnumerable<AiStreamChunk> ChatStreamAsync(...):

  1. Build request:
     URL: "{config.BaseUrl}/v1/chat/completions"
     Headers: Authorization: Bearer {config.ApiKey}, Content-Type: application/json
     Body JSON:
     {
       "model": config.Model,
       "messages": [convert AiMessage list to OpenAI format],
       "tools": [convert AiToolDefinition list to OpenAI format],
       "stream": true,
       "temperature": config.Temperature,
       "max_tokens": config.MaxTokens
     }

  2. Message format conversion:
     AiMessage { Role: "system", Content: "..." } → { "role": "system", "content": "..." }
     AiMessage { Role: "user", Content: "..." } → { "role": "user", "content": "..." }
     AiMessage { Role: "assistant", Content: "...", ToolCalls: [...] } → {
       "role": "assistant",
       "content": "...",
       "tool_calls": [{ "id": "...", "type": "function", "function": { "name": "...", "arguments": "{...}" } }]
     }
     AiMessage { Role: "tool", ToolCallId: "...", Content: "..." } → {
       "role": "tool",
       "tool_call_id": "...",
       "content": "..."
     }

  3. Tool format conversion:
     AiToolDefinition → {
       "type": "function",
       "function": {
         "name": "...",
         "description": "...",
         "parameters": {
           "type": "object",
           "properties": { [name]: { "type": "...", "description": "...", "enum": [...] } },
           "required": [...]
         }
       }
     }

  4. Stream SSE:
     Send request with HttpCompletionOption.ResponseHeadersRead
     Read response stream line by line
     For each line starting with "data: ":
       If "data: [DONE]" → yield new AiStreamChunk { IsComplete = true }; break
       Parse JSON
       Extract choices[0].delta:
         If delta.content exists → yield new AiStreamChunk { TextDelta = delta.content }
         If delta.tool_calls exists:
           Accumulate tool call chunks (they come in pieces: id, name, arguments chunks)
           When tool call is complete (next chunk starts new tool or content comes):
             yield new AiStreamChunk { ToolCall = completed tool call }

  5. Error handling:
     If HTTP status != 200, read error body and yield AiStreamChunk { Error = "..." }
```

### `Services/Ai/Providers/AnthropicProvider.cs`

```
CLASS: AnthropicProvider : IAiProvider

METHOD IAsyncEnumerable<AiStreamChunk> ChatStreamAsync(...):

  1. Build request:
     URL: "{config.BaseUrl}/v1/messages"
     Headers:
       x-api-key: {config.ApiKey}
       anthropic-version: 2023-06-01
       Content-Type: application/json
     Body JSON:
     {
       "model": config.Model,
       "system": [extract system message content from messages list],
       "messages": [convert non-system messages to Anthropic format],
       "tools": [convert tool definitions],
       "stream": true,
       "max_tokens": config.MaxTokens,
       "temperature": config.Temperature
     }

  2. Message format conversion (Anthropic-specific):
     IMPORTANT: System messages go in top-level "system" field, NOT in messages array.
     
     User message → { "role": "user", "content": "..." }
     Assistant message with tool calls → {
       "role": "assistant",
       "content": [
         { "type": "text", "text": "..." },
         { "type": "tool_use", "id": "...", "name": "...", "input": {...} }
       ]
     }
     Tool result → {
       "role": "user",
       "content": [
         { "type": "tool_result", "tool_use_id": "...", "content": "..." }
       ]
     }

  3. Tool format conversion:
     AiToolDefinition → {
       "name": "...",
       "description": "...",
       "input_schema": {
         "type": "object",
         "properties": { ... },
         "required": [...]
       }
     }

  4. Stream SSE events:
     Parse "event: {type}\ndata: {json}\n\n" format
     
     event: message_start → ignore (metadata)
     event: content_block_start →
       If data.content_block.type == "text" → note we're in text mode
       If data.content_block.type == "tool_use" → note tool id and name, start accumulating
     event: content_block_delta →
       If data.delta.type == "text_delta" → yield AiStreamChunk { TextDelta = data.delta.text }
       If data.delta.type == "input_json_delta" → append data.delta.partial_json to accumulator
     event: content_block_stop →
       If we were accumulating a tool_use → parse accumulated JSON as arguments
       yield AiStreamChunk { ToolCall = { Id, Name, Arguments } }
     event: message_delta → check data.delta.stop_reason
     event: message_stop → yield AiStreamChunk { IsComplete = true }
```

### `Services/Ai/Providers/GeminiProvider.cs`

```
CLASS: GeminiProvider : IAiProvider

METHOD IAsyncEnumerable<AiStreamChunk> ChatStreamAsync(...):

  1. Build request:
     URL: "{config.BaseUrl}/v1beta/models/{config.Model}:streamGenerateContent?alt=sse&key={config.ApiKey}"
     Headers: Content-Type: application/json
     NOTE: Gemini uses API key in URL, not in Authorization header
     Body JSON:
     {
       "systemInstruction": { "parts": [{ "text": "[system message content]" }] },
       "contents": [convert messages to Gemini format],
       "tools": [{ "functionDeclarations": [convert tool definitions] }],
       "generationConfig": {
         "temperature": config.Temperature,
         "maxOutputTokens": config.MaxTokens
       }
     }

  2. Message format conversion (Gemini-specific):
     System message → goes in "systemInstruction", not "contents"
     User message → { "role": "user", "parts": [{ "text": "..." }] }
     Assistant message → { "role": "model", "parts": [{ "text": "..." }] }
     Assistant with tool call → { "role": "model", "parts": [{ "functionCall": { "name": "...", "args": {...} } }] }
     Tool result → { "role": "user", "parts": [{ "functionResponse": { "name": "...", "response": { "result": "..." } } }] }
     
     IMPORTANT: Gemini uses "model" not "assistant" for the AI role.

  3. Tool format conversion:
     AiToolDefinition → {
       "name": "...",
       "description": "...",
       "parameters": {
         "type": "OBJECT",
         "properties": { [name]: { "type": "STRING", "description": "...", "enum": [...] } },
         "required": [...]
       }
     }
     NOTE: Gemini types are uppercase: STRING, INTEGER, OBJECT, ARRAY, BOOLEAN

  4. Stream SSE:
     Parse "data: {json}" lines
     For each parsed JSON object:
       candidates[0].content.parts → iterate:
         If part has "text" → yield AiStreamChunk { TextDelta = part.text }
         If part has "functionCall" → yield AiStreamChunk { ToolCall = { Name = part.functionCall.name, Arguments = part.functionCall.args } }
       If candidates[0].finishReason == "STOP" → yield AiStreamChunk { IsComplete = true }
```

### `Services/Ai/Providers/OllamaProvider.cs`

```
CLASS: OllamaProvider : IAiProvider

METHOD IAsyncEnumerable<AiStreamChunk> ChatStreamAsync(...):

  1. Build request:
     URL: "{config.BaseUrl}/api/chat"
     Headers: Content-Type: application/json
     NOTE: No API key needed for Ollama
     Body JSON:
     {
       "model": config.Model,
       "messages": [OpenAI-compatible format — same as OpenAiProvider],
       "tools": [OpenAI-compatible format — same as OpenAiProvider],
       "stream": true,
       "options": {
         "temperature": config.Temperature,
         "num_predict": config.MaxTokens
       }
     }

  2. Stream (NOT SSE — Ollama uses newline-delimited JSON):
     Read line by line, each line is a complete JSON object:
     { "message": { "role": "assistant", "content": "...", "tool_calls": [...] }, "done": false }
     
     If message.content is not empty → yield AiStreamChunk { TextDelta = message.content }
     If message.tool_calls exists → yield AiStreamChunk { ToolCall = ... }
     If done == true → yield AiStreamChunk { IsComplete = true }

  3. Config.BaseUrl defaults to "http://localhost:11434"

ADDITIONAL METHOD (for settings page):
  static async Task<string[]> ListModelsAsync(string baseUrl):
    GET {baseUrl}/api/tags
    Parse response.models[].name
    Return as string array
```

### `Services/Ai/AiProviderFactory.cs`

```
CLASS: AiProviderFactory

CONSTRUCTOR: AiProviderFactory(IHttpClientFactory httpClientFactory)

METHOD IAiProvider Create(string provider):
  return provider.ToLowerInvariant() switch {
    "openai" => new OpenAiProvider(httpClientFactory),
    "anthropic" => new AnthropicProvider(httpClientFactory),
    "gemini" => new GeminiProvider(httpClientFactory),
    "ollama" => new OllamaProvider(httpClientFactory),
    "custom" => new OpenAiProvider(httpClientFactory),  // Custom uses OpenAI-compatible API
    _ => throw new ArgumentException($"Unknown AI provider: {provider}")
  };

METHOD string GetDefaultBaseUrl(string provider):
  return provider.ToLowerInvariant() switch {
    "openai" => "https://api.openai.com",
    "anthropic" => "https://api.anthropic.com",
    "gemini" => "https://generativelanguage.googleapis.com",
    "ollama" => "http://localhost:11434",
    "custom" => "",
    _ => ""
  };

METHOD string[] GetDefaultModels(string provider):
  return provider.ToLowerInvariant() switch {
    "openai" => ["gpt-4o", "gpt-4o-mini", "gpt-4-turbo", "gpt-3.5-turbo"],
    "anthropic" => ["claude-sonnet-4-20250514", "claude-3-5-haiku-20241022", "claude-opus-4-20250514"],
    "gemini" => ["gemini-2.5-pro", "gemini-2.5-flash", "gemini-2.0-flash"],
    "ollama" => [],   // fetched dynamically
    "custom" => [],   // user enters manually
    _ => []
  };
```

### `Services/Ai/Tools/ToolDefinitions.cs`

```
STATIC CLASS: ToolDefinitions

STATIC METHOD List<AiToolDefinition> GetAll():
  Return list of 6 tools:

  TOOL 1: "execute_command"
    Description: "Execute a PowerShell command on a specific lab PC. Returns the command output (stdout and stderr)."
    Parameters:
      "client_id": type "string", required, description "The ID of the target PC (e.g., PC-LAB-001). Use list_clients first if unsure."
      "command": type "string", required, description "The PowerShell command to execute on the target PC."
      "timeout_seconds": type "integer", not required, description "Command timeout in seconds. Default 60. Increase for long-running commands."

  TOOL 2: "execute_command_all"
    Description: "Execute a PowerShell command on ALL online lab PCs simultaneously. Returns aggregated results from each PC. Use when the user says 'all PCs' or 'every PC'."
    Parameters:
      "command": type "string", required, description "The PowerShell command to execute on all online PCs."
      "timeout_seconds": type "integer", not required, description "Command timeout per PC in seconds. Default 60."

  TOOL 3: "list_clients"
    Description: "List all registered lab PCs with their current status (online/offline), current user, active application, CPU and memory usage. Use this to find specific PCs before executing commands."
    Parameters:
      "status_filter": type "string", not required, enum ["all", "online", "offline", "idle"], description "Filter by status. 'idle' means CPU < 5%. Default: 'all'."

  TOOL 4: "get_system_info"
    Description: "Get detailed system information for a specific PC including OS version, CPU, memory, disk space, installed software, and running processes."
    Parameters:
      "client_id": type "string", required, description "The ID of the target PC."

  TOOL 5: "get_activity_logs"
    Description: "Retrieve activity logs for a PC. Includes keystrokes typed, processes started/stopped, and window focus changes."
    Parameters:
      "client_id": type "string", required, description "The ID of the target PC."
      "event_type": type "string", not required, enum ["all", "keystroke", "process_start", "process_stop", "window_focus", "login", "logoff"], description "Filter by event type. Default: 'all'."
      "hours_back": type "integer", not required, description "How many hours back to search. Default: 1."
      "limit": type "integer", not required, description "Maximum number of results to return. Default: 50."

  TOOL 6: "get_command_history"
    Description: "Get the history of previously executed commands on a specific PC, including command text, output, status, and who sent it."
    Parameters:
      "client_id": type "string", required, description "The ID of the target PC."
      "limit": type "integer", not required, description "Maximum number of results. Default: 20."
```

### `Services/Ai/AiToolExecutor.cs`

```
CLASS: AiToolExecutor

DEPENDENCIES:
  - ClientStateService _clientState
  - IServiceScopeFactory _scopeFactory     (to get scoped CommandService, LogStorageService)
  - IHubContext<ClientHub> _hubContext

METHOD async Task<string> ExecuteToolAsync(AiToolCall toolCall, CancellationToken ct):
  
  switch (toolCall.Name):
  
    case "execute_command":
      var clientId = toolCall.Arguments["client_id"].ToString();
      var command = toolCall.Arguments["command"].ToString();
      var timeout = toolCall.Arguments.TryGetValue("timeout_seconds", out var t) ? Convert.ToInt32(t) : 60;
      
      using (var scope = _scopeFactory.CreateScope()) {
        var cmdService = scope.ServiceProvider.GetRequiredService<CommandService>();
        return await cmdService.ExecuteAndWaitAsync(clientId, command, timeout);
      }

    case "execute_command_all":
      var command = toolCall.Arguments["command"].ToString();
      var timeout = toolCall.Arguments.TryGetValue("timeout_seconds", out var t) ? Convert.ToInt32(t) : 60;
      var onlineClients = _clientState.GetAllClients().Where(c => c.IsOnline).ToList();
      
      if (onlineClients.Count == 0) return "No clients are currently online.";
      
      using (var scope = _scopeFactory.CreateScope()) {
        var cmdService = scope.ServiceProvider.GetRequiredService<CommandService>();
        var tasks = onlineClients.Select(async c => {
          try {
            var output = await cmdService.ExecuteAndWaitAsync(c.ClientId, command, timeout);
            return $"=== {c.ClientId} ({c.Hostname}) ===\n{output}";
          } catch (Exception ex) {
            return $"=== {c.ClientId} ({c.Hostname}) ===\nERROR: {ex.Message}";
          }
        });
        var results = await Task.WhenAll(tasks);
        return string.Join("\n\n", results);
      }

    case "list_clients":
      var filter = toolCall.Arguments.TryGetValue("status_filter", out var f) ? f.ToString() : "all";
      var clients = _clientState.GetAllClients();
      clients = filter switch {
        "online" => clients.Where(c => c.IsOnline).ToList(),
        "offline" => clients.Where(c => !c.IsOnline).ToList(),
        "idle" => clients.Where(c => c.IsOnline && c.CpuPercent < 5).ToList(),
        _ => clients
      };
      
      if (clients.Count == 0) return "No clients match the filter.";
      
      // Format as readable table
      var sb = new StringBuilder();
      sb.AppendLine($"{"Client ID",-20} {"Hostname",-15} {"Status",-8} {"User",-15} {"Active App",-20} {"CPU",-6} {"Memory",-6}");
      sb.AppendLine(new string('-', 95));
      foreach (var c in clients) {
        var status = c.IsOnline ? "Online" : "Offline";
        sb.AppendLine($"{c.ClientId,-20} {c.Hostname,-15} {status,-8} {c.CurrentUser,-15} {c.ActiveProcess,-20} {c.CpuPercent,5:F1}% {c.MemoryPercent,5:F1}%");
      }
      return sb.ToString();

    case "get_system_info":
      var clientId = toolCall.Arguments["client_id"].ToString();
      var connId = _clientState.GetConnectionId(clientId);
      if (connId == null) return $"ERROR: Client {clientId} is not online.";
      
      // Invoke on client and wait
      var info = await _hubContext.Clients.Client(connId)
        .InvokeAsync<SystemInfoDto>("GetSystemInfo", ct);
      
      return $"Hostname: {info.Hostname}\n" +
             $"OS: {info.OsVersion}\n" +
             $"CPU: {info.CpuName} ({info.CpuCores} cores)\n" +
             $"Memory: {info.FreeMemoryMb}MB free / {info.TotalMemoryMb}MB total\n" +
             $"Disk: {info.FreeDiskMb}MB free / {info.TotalDiskMb}MB total\n" +
             $"User: {info.CurrentUser}\n" +
             $"IP: {info.IpAddress}\n" +
             $"Running Processes: {string.Join(", ", info.RunningProcesses.Take(30))}\n" +
             $"Installed Software ({info.InstalledSoftware.Length} total): {string.Join(", ", info.InstalledSoftware.Take(20))}";

    case "get_activity_logs":
      var clientId = toolCall.Arguments["client_id"].ToString();
      var eventType = toolCall.Arguments.TryGetValue("event_type", out var et) ? et.ToString() : "all";
      var hoursBack = toolCall.Arguments.TryGetValue("hours_back", out var hb) ? Convert.ToInt32(hb) : 1;
      var limit = toolCall.Arguments.TryGetValue("limit", out var lm) ? Convert.ToInt32(lm) : 50;
      
      EventType? eventTypeFilter = eventType != "all" ? Enum.Parse<EventType>(eventType, true) : null;
      var from = DateTime.UtcNow.AddHours(-hoursBack);
      
      using (var scope = _scopeFactory.CreateScope()) {
        var logService = scope.ServiceProvider.GetRequiredService<LogStorageService>();
        var (logs, total) = await logService.QueryAsync(clientId, eventTypeFilter, from, null, null, 1, limit);
        
        if (logs.Count == 0) return $"No activity logs found for {clientId} in the last {hoursBack} hour(s).";
        
        var sb = new StringBuilder();
        sb.AppendLine($"Activity logs for {clientId} (showing {logs.Count} of {total}):\n");
        foreach (var log in logs) {
          sb.AppendLine($"[{log.Timestamp:HH:mm:ss}] {log.EventType}: {log.Details}");
        }
        return sb.ToString();
      }

    case "get_command_history":
      var clientId = toolCall.Arguments["client_id"].ToString();
      var limit = toolCall.Arguments.TryGetValue("limit", out var lm) ? Convert.ToInt32(lm) : 20;
      
      using (var scope = _scopeFactory.CreateScope()) {
        var cmdService = scope.ServiceProvider.GetRequiredService<CommandService>();
        var history = await cmdService.GetHistoryAsync(clientId, limit);
        
        if (history.Count == 0) return $"No command history for {clientId}.";
        
        var sb = new StringBuilder();
        foreach (var cmd in history) {
          sb.AppendLine($"[{cmd.SentAt:yyyy-MM-dd HH:mm:ss}] by {cmd.SentBy} | Status: {cmd.Status}");
          sb.AppendLine($"  Command: {cmd.Command}");
          if (!string.IsNullOrEmpty(cmd.Output))
            sb.AppendLine($"  Output: {(cmd.Output.Length > 200 ? cmd.Output[..200] + "..." : cmd.Output)}");
          sb.AppendLine();
        }
        return sb.ToString();
      }

    default:
      return $"Unknown tool: {toolCall.Name}";
```

### `Services/Ai/AiChatService.cs`

```
CLASS: AiChatService

DEPENDENCIES:
  - AiProviderFactory _providerFactory
  - AiToolExecutor _toolExecutor
  - IServiceScopeFactory _scopeFactory
  - ClientStateService _clientState
  - IConfiguration _config

DEFAULT SYSTEM PROMPT (use this exact text):

"""
You are LabLock AI, an intelligent assistant for managing a network of Windows lab PCs.

Your capabilities:
- Execute PowerShell commands on any connected PC or all PCs at once
- View real-time status of all PCs (online/offline, current user, active app)
- Retrieve detailed system information (hardware, software, processes)
- Search and analyze activity logs (keystrokes, process usage, window focus)
- Review command execution history

Guidelines:
- Be concise and helpful in your responses
- Format data as tables when showing multiple items
- When asked to install software, use appropriate package managers (winget, chocolatey, or direct download)
- ALWAYS confirm with the user before executing destructive commands (shutdown, restart, format, delete system files, uninstall)
- When a user says "all PCs" or "every PC", use the execute_command_all tool
- Show command output in code blocks for readability
- If a command fails, analyze the error and suggest fixes
- For security-sensitive operations, warn the user about implications
- Use list_clients first when you need to find specific PCs
- Keep responses focused and actionable
"""

METHOD AiConfig GetCurrentConfig():
  1. Try loading from DB (AiSettings table, row id=1)
  2. Fall back to appsettings.json "Ai" section
  3. Return AiConfig object

METHOD bool IsConfigured():
  var config = GetCurrentConfig();
  return config.Provider is not "none" and not "" 
    && (config.ApiKey is not "" || config.Provider is "ollama");

METHOD string BuildSystemPrompt():
  1. Start with configured system prompt (or default above)
  2. Append dynamic context:
     "\n\n--- Current Lab Status ---"
     "Total PCs: {_clientState.TotalCount}"
     "Online: {_clientState.OnlineCount}"
     "Offline: {_clientState.TotalCount - _clientState.OnlineCount}"
     "Current time (UTC): {DateTime.UtcNow}"
     "\nOnline PCs:"
     For each online client: "- {clientId} (user: {currentUser}, app: {activeProcess}, CPU: {cpuPercent}%)"

METHOD async IAsyncEnumerable<AiChatStreamEvent> ChatAsync(
    string conversationId, string userMessage, CancellationToken ct):

  1. Load or create conversation from DB
  2. Load previous messages for this conversation from DB
  3. Add user message to DB
  
  4. Build messages list for AI:
     messages = [
       new AiMessage { Role = "system", Content = BuildSystemPrompt() },
       ...previous messages from DB (excluding system messages),
       new AiMessage { Role = "user", Content = userMessage }
     ]
  
  5. Get tools = ToolDefinitions.GetAll()
  6. Get config = GetCurrentConfig()
  7. Get provider = _providerFactory.Create(config.Provider)
  
  8. TOOL CALL LOOP (max 10 iterations to prevent infinite loops):
     
     var assistantContent = new StringBuilder();
     var toolCalls = new List<AiToolCall>();
     
     await foreach (var chunk in provider.ChatStreamAsync(messages, tools, config, ct)):
       if (chunk.Error != null):
         yield return new AiChatStreamEvent { Type = "error", Content = chunk.Error }
         break
       
       if (chunk.TextDelta != null):
         assistantContent.Append(chunk.TextDelta)
         yield return new AiChatStreamEvent { Type = "text", Content = chunk.TextDelta }
       
       if (chunk.ToolCall != null):
         toolCalls.Add(chunk.ToolCall)
         yield return new AiChatStreamEvent { 
           Type = "tool_call", 
           ToolName = chunk.ToolCall.Name,
           ToolArgs = JsonSerializer.Serialize(chunk.ToolCall.Arguments)
         }
     
     // If no tool calls, we're done
     if (toolCalls.Count == 0):
       // Save assistant message to DB
       SaveMessage(conversationId, "assistant", assistantContent.ToString())
       yield return new AiChatStreamEvent { Type = "done" }
       break  // exit tool call loop
     
     // Add assistant message (with tool calls) to messages list
     messages.Add(new AiMessage { 
       Role = "assistant", 
       Content = assistantContent.ToString(),
       ToolCalls = toolCalls
     })
     
     // Execute each tool call
     foreach (var toolCall in toolCalls):
       yield return new AiChatStreamEvent { 
         Type = "tool_executing", 
         ToolName = toolCall.Name 
       }
       
       string result;
       try:
         result = await _toolExecutor.ExecuteToolAsync(toolCall, ct)
       catch (Exception ex):
         result = $"Tool execution failed: {ex.Message}"
       
       yield return new AiChatStreamEvent { 
         Type = "tool_result", 
         ToolName = toolCall.Name, 
         Content = result 
       }
       
       // Add tool result to messages for next AI call
       messages.Add(new AiMessage {
         Role = "tool",
         ToolCallId = toolCall.Id,
         Content = result
       })
     
     // Clear for next iteration (AI will process tool results)
     assistantContent.Clear()
     toolCalls.Clear()
     // Loop continues — AI processes tool results and may make more tool calls

  9. If loop exhausted (10 iterations), yield error event

  10. Update conversation title if first message:
      Set title = first 50 chars of userMessage


DATA CLASS AiChatStreamEvent:
  string Type       // "text", "tool_call", "tool_executing", "tool_result", "done", "error"
  string? Content
  string? ToolName
  string? ToolArgs
```

### `Controllers/AiController.cs`

```
[Route("api/ai")]
[ApiController]
[Authorize]

POST /api/ai/chat
  Request body: { "conversationId": string, "message": string }
  Response: SSE stream (Content-Type: text/event-stream)
  
  Implementation:
    Response.ContentType = "text/event-stream";
    Response.Headers.Append("Cache-Control", "no-cache");
    Response.Headers.Append("Connection", "keep-alive");
    
    await foreach (var evt in _aiChatService.ChatAsync(conversationId, message, ct)) {
      var json = JsonSerializer.Serialize(evt);
      await Response.WriteAsync($"data: {json}\n\n");
      await Response.Body.FlushAsync();
    }

GET /api/ai/conversations
  → Query DB AiConversations, order by UpdatedAt desc
  → Return [{ id, title, createdAt, updatedAt, messageCount }]

POST /api/ai/conversations
  → Create new AiConversation in DB
  → Return { id }

GET /api/ai/conversations/{id}
  → Load conversation + messages from DB
  → Return { id, title, createdAt, messages: [{ role, content, toolCalls, timestamp }] }

DELETE /api/ai/conversations/{id}
  → Delete conversation + messages from DB
  → Return 204
```

### `Controllers/AiSettingsController.cs`

```
[Route("api/ai/settings")]
[ApiController]
[Authorize]

GET /api/ai/settings
  → Load AiSettings from DB (or defaults from config)
  → Return {
      provider, model, baseUrl, temperature, maxTokens, systemPrompt,
      hasApiKey: bool (true if apiKey is non-empty, NEVER return actual key),
      availableModels: string[] (from AiProviderFactory.GetDefaultModels)
    }

PUT /api/ai/settings
  Request: { provider, apiKey, model, baseUrl, temperature, maxTokens, systemPrompt }
  → Upsert AiSettings in DB (id = 1)
  → If apiKey is empty string, keep existing key (don't clear it)
  → If apiKey is non-empty, update it
  → Return 200 { success: true }

POST /api/ai/test
  → Load current settings
  → Create provider via factory
  → Send simple message: [{ role: "user", content: "Say hello in one sentence." }]
  → Read first few chunks
  → Return { success: true, response: "..." } or { success: false, error: "..." }
  → Set 10 second timeout

GET /api/ai/ollama-models
  → Fetch GET {ollamaBaseUrl}/api/tags
  → Parse model names
  → Return string[]
  → Handle connection errors gracefully
```

---

## 6. Database Schema

### EF Core Entities

See the Models/ directory in LabLock.Server. All entities are listed in Section 3 header.

**Entity definitions** (implement as C# classes in `Models/`):

```csharp
// Client.cs
public class Client {
    [Key] public string ClientId { get; set; } = "";
    public string Hostname { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public string MacAddress { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public string AgentVersion { get; set; } = "";
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
}

// ActivityLog.cs
public class ActivityLog {
    [Key] public long Id { get; set; }
    public string ClientId { get; set; } = "";
    public EventType EventType { get; set; }
    public string Details { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public string Username { get; set; } = "";
}

// CommandHistory.cs
public class CommandHistory {
    [Key] public int Id { get; set; }
    public string ClientId { get; set; } = "";
    public string Command { get; set; } = "";
    public string Output { get; set; } = "";
    public string Error { get; set; } = "";
    public int ExitCode { get; set; }
    public CommandStatus Status { get; set; }
    public DateTime SentAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string SentBy { get; set; } = "";
}

// AiSettings.cs
public class AiSettings {
    [Key] public int Id { get; set; } = 1;
    public string Provider { get; set; } = "none";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public double Temperature { get; set; } = 0.3;
    public int MaxTokens { get; set; } = 4096;
    public string SystemPrompt { get; set; } = "";
}

// AiConversation.cs
public class AiConversation {
    [Key] public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

// AiMessage.cs
public class AiMessage {
    [Key] public long Id { get; set; }
    public string ConversationId { get; set; } = "";
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
    public string? ToolCallsJson { get; set; }
    public string? ToolCallId { get; set; }
    public DateTime Timestamp { get; set; }
}

// DashboardUser.cs
public class DashboardUser {
    [Key] public int Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
```

### `Data/AppDbContext.cs`

```csharp
public class AppDbContext : DbContext
{
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<ActivityLog> ActivityLogs => Set<ActivityLog>();
    public DbSet<CommandHistory> CommandHistory => Set<CommandHistory>();
    public DbSet<AiSettings> AiSettings => Set<AiSettings>();
    public DbSet<AiConversation> AiConversations => Set<AiConversation>();
    public DbSet<AiMessage> AiMessages => Set<AiMessage>();
    public DbSet<DashboardUser> DashboardUsers => Set<DashboardUser>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Indexes for query performance:
        modelBuilder.Entity<ActivityLog>().HasIndex(l => new { l.ClientId, l.Timestamp });
        modelBuilder.Entity<ActivityLog>().HasIndex(l => l.EventType);
        modelBuilder.Entity<ActivityLog>().HasIndex(l => l.Timestamp);
        modelBuilder.Entity<CommandHistory>().HasIndex(c => c.ClientId);
        modelBuilder.Entity<AiMessage>().HasIndex(m => m.ConversationId);

        // Seed default AI settings (provider: none)
        modelBuilder.Entity<AiSettings>().HasData(new AiSettings { Id = 1 });

        // Seed default admin user (password: "admin" hashed with BCrypt)
        modelBuilder.Entity<DashboardUser>().HasData(new DashboardUser {
            Id = 1,
            Username = "admin",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("admin"),
            CreatedAt = DateTime.UtcNow
        });
    }
}
```

After creating the DbContext, run:
```bash
dotnet ef migrations add InitialCreate --project src/LabLock.Server
```

---

## 7. SignalR Protocol Contract

### Hub URL: `/hub/client`

### Server Methods (called BY clients via InvokeAsync)

| Method | Parameter Type | Description |
|---|---|---|
| `RegisterClient` | `ClientRegistrationDto` | Called once on initial connection |
| `Heartbeat` | `HeartbeatDto` | Called every 30 seconds |
| `SendActivityBatch` | `ActivityLogDto[]` | Called every 10 seconds with buffered logs |
| `ReportCommandResult` | `CommandResultDto` | Called when a command finishes execution |

### Client Methods (called ON clients BY server via SendAsync/InvokeAsync)

| Method | Parameter Type | Return Type | Description |
|---|---|---|---|
| `ExecuteCommand` | `int commandId, CommandRequestDto` | `CommandResultDto` | Execute PowerShell command |
| `GetSystemInfo` | — | `SystemInfoDto` | Collect and return system information |
| `RestartAgent` | — | — | Restart the client agent service |
| `UpdateConfig` | `Dictionary<string,string>` | — | Update config and restart |

### Dashboard SignalR Events (pushed TO dashboard)

| Event | Parameter Type | Description |
|---|---|---|
| `ClientConnected` | `ClientState` | A PC came online |
| `ClientDisconnected` | `string clientId` | A PC went offline |
| `ClientHeartbeat` | `HeartbeatDto` | Heartbeat update |
| `CommandResult` | `CommandResultDto` | A command completed |

---

## 8. REST API Contract

### Auth
| Method | Path | Auth | Request | Response |
|---|---|---|---|---|
| POST | `/api/auth/login` | No | `{ username, password }` | `{ token }` |

### Clients
| Method | Path | Auth | Response |
|---|---|---|---|
| GET | `/api/clients` | JWT | `[ClientState]` |
| GET | `/api/clients/{id}` | JWT | `ClientState` |
| GET | `/api/clients/{id}/system-info` | JWT | `SystemInfoDto` |

### Logs
| Method | Path | Auth | Query Params | Response |
|---|---|---|---|---|
| GET | `/api/logs` | JWT | `clientId, eventType, from, to, keyword, page, pageSize` | `{ data, total, page, pageSize }` |
| GET | `/api/logs/stats` | JWT | `clientId, from, to` | `{ eventCounts }` |

### Commands
| Method | Path | Auth | Request | Response |
|---|---|---|---|---|
| POST | `/api/commands` | JWT | `{ clientId, command, timeoutSeconds }` | `{ commandId }` |
| POST | `/api/commands/broadcast` | JWT | `{ command, timeoutSeconds }` | `{ commandIds }` |
| GET | `/api/commands/{id}` | JWT | — | `CommandHistory` |
| GET | `/api/commands/history` | JWT | `?clientId=&limit=` | `[CommandHistory]` |

### AI
| Method | Path | Auth | Request/Params | Response |
|---|---|---|---|---|
| POST | `/api/ai/chat` | JWT | `{ conversationId, message }` | SSE stream |
| GET | `/api/ai/conversations` | JWT | — | `[{ id, title, ... }]` |
| POST | `/api/ai/conversations` | JWT | — | `{ id }` |
| GET | `/api/ai/conversations/{id}` | JWT | — | `{ id, messages }` |
| DELETE | `/api/ai/conversations/{id}` | JWT | — | 204 |
| GET | `/api/ai/settings` | JWT | — | `{ provider, model, ... }` |
| PUT | `/api/ai/settings` | JWT | `{ provider, apiKey, model, ... }` | `{ success }` |
| POST | `/api/ai/test` | JWT | — | `{ success, response/error }` |
| GET | `/api/ai/ollama-models` | JWT | — | `string[]` |

---

## 9. Security

| Layer | Implementation |
|---|---|
| **Client → Server auth** | Pre-shared API key in query string on SignalR connect. Server validates in `OnConnectedAsync`. |
| **Dashboard auth** | JWT bearer token (HS256, 24h expiry). Login with username + BCrypt password. |
| **Transport** | HTTP for LAN. Configure Kestrel HTTPS if exposed externally. |
| **Command audit** | Every command stored in `CommandHistory` with `SentBy` field ("admin" or "ai"). |
| **Client protection** | Runs as `LocalSystem` Windows Service — standard users cannot stop or modify it. |
| **AI safety** | System prompt instructs confirmation for destructive ops. All tool calls logged. |
| **API key handling** | Never return API keys in GET responses. Only store and check `hasApiKey: bool`. |

---

## 10. Deployment

### Server (Linux)

**Publish command** (from dev machine):
```bash
dotnet publish src/LabLock.Server -c Release -r linux-x64 --self-contained -o publish/server
```

**`deploy/server-setup.sh`**:
```bash
#!/bin/bash
set -e

# 1. Install .NET 8 ASP.NET Core runtime (if not self-contained)
# For self-contained publish, this is not needed

# 2. Create app directory
sudo mkdir -p /opt/lablock
sudo cp -r ./publish/server/* /opt/lablock/
sudo chmod +x /opt/lablock/LabLock.Server

# 3. Create data directory
sudo mkdir -p /opt/lablock/data

# 4. Install systemd service
sudo cp ./deploy/lablock-server.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable lablock-server
sudo systemctl start lablock-server

# 5. Open firewall
sudo ufw allow 5000/tcp 2>/dev/null || sudo firewall-cmd --add-port=5000/tcp --permanent 2>/dev/null || true

echo "LabLock Server installed. Access dashboard at http://$(hostname -I | awk '{print $1}'):5000"
```

**`deploy/lablock-server.service`**:
```ini
[Unit]
Description=LabLock Server
After=network.target

[Service]
Type=notify
WorkingDirectory=/opt/lablock
ExecStart=/opt/lablock/LabLock.Server
Restart=always
RestartSec=10
Environment=ASPNETCORE_URLS=http://0.0.0.0:5000
Environment=DOTNET_ENVIRONMENT=Production
User=root
LimitNOFILE=65536

[Install]
WantedBy=multi-user.target
```

### Client (Windows)

**Publish command**:
```bash
dotnet publish src/LabLock.Client -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish/client
```

**`deploy/install-client.ps1`**:
```powershell
param(
    [Parameter(Mandatory=$true)] [string]$ServerUrl,
    [string]$ApiKey = "CHANGE_ME_SHARED_KEY",
    [string]$InstallPath = "C:\LabLock"
)

Write-Host "Installing LabLock Client Agent..." -ForegroundColor Cyan

# Stop existing service if running
$svc = Get-Service -Name "LabLockAgent" -ErrorAction SilentlyContinue
if ($svc) {
    Stop-Service -Name "LabLockAgent" -Force
    sc.exe delete LabLockAgent
    Start-Sleep -Seconds 2
}

# Create install directory
New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null

# Copy files
Copy-Item -Path "$PSScriptRoot\*" -Destination $InstallPath -Recurse -Force

# Write config
$config = @{
    LabLock = @{
        ServerUrl = $ServerUrl
        ApiKey = $ApiKey
        ClientId = ""
        HeartbeatIntervalSeconds = 30
        LogBatchIntervalSeconds = 10
        MaxLocalBufferDays = 7
        EnableKeystrokeLogging = $true
        EnableProcessMonitoring = $true
        EnableWindowMonitoring = $true
        WindowPollIntervalMs = 2000
    }
} | ConvertTo-Json -Depth 3
Set-Content -Path "$InstallPath\appsettings.json" -Value $config

# Register as Windows Service
sc.exe create LabLockAgent binPath="$InstallPath\LabLock.Client.exe" start=auto obj=LocalSystem
sc.exe description LabLockAgent "LabLock PC Management Agent"
sc.exe failure LabLockAgent reset=60 actions=restart/5000/restart/10000/restart/30000

# Start service
sc.exe start LabLockAgent

Write-Host "LabLock Client Agent installed and started." -ForegroundColor Green
```

**`deploy/deploy-clients.ps1`** (mass deploy to all lab PCs):
```powershell
param(
    [Parameter(Mandatory=$true)] [string]$ServerUrl,
    [string]$PcListFile = "$PSScriptRoot\lab-pcs.txt",
    [string]$ApiKey = "CHANGE_ME_SHARED_KEY",
    [string]$PublishPath = "$PSScriptRoot\..\publish\client"
)

$pcs = Get-Content $PcListFile | Where-Object { $_.Trim() -ne "" }
$results = @()

foreach ($pc in $pcs) {
    Write-Host "Deploying to $pc..." -NoNewline
    try {
        # Test connectivity
        if (!(Test-Connection -ComputerName $pc -Count 1 -Quiet)) {
            throw "Unreachable"
        }
        # Copy files
        $dest = "\\$pc\C$\LabLock"
        New-Item -ItemType Directory -Path $dest -Force | Out-Null
        Copy-Item -Path "$PublishPath\*" -Destination $dest -Recurse -Force
        
        # Remote install
        Invoke-Command -ComputerName $pc -ScriptBlock {
            param($ServerUrl, $ApiKey)
            & "C:\LabLock\install-client.ps1" -ServerUrl $ServerUrl -ApiKey $ApiKey -InstallPath "C:\LabLock"
        } -ArgumentList $ServerUrl, $ApiKey
        
        Write-Host " OK" -ForegroundColor Green
        $results += [PSCustomObject]@{ PC = $pc; Status = "Success" }
    } catch {
        Write-Host " FAILED: $_" -ForegroundColor Red
        $results += [PSCustomObject]@{ PC = $pc; Status = "Failed: $_" }
    }
}

Write-Host "`n--- Deployment Summary ---" -ForegroundColor Cyan
$results | Format-Table -AutoSize
Write-Host "Success: $($results | Where-Object Status -eq 'Success' | Measure-Object | Select-Object -Expand Count) / $($pcs.Count)"
```

**`deploy/uninstall-client.ps1`**:
```powershell
param([string]$InstallPath = "C:\LabLock")
Stop-Service -Name "LabLockAgent" -Force -ErrorAction SilentlyContinue
sc.exe delete LabLockAgent
Remove-Item -Path $InstallPath -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "LabLock Client Agent uninstalled." -ForegroundColor Yellow
```

---

## 11. Implementation Order

Implement in this exact sequence. Each phase must work before starting the next.

### Phase 1: Solution Scaffold + Shared Models
1. Create `LabLock.sln`
2. Create `LabLock.Shared` project with all DTOs and Enums
3. Create `LabLock.Server` project with csproj + NuGet packages
4. Create `LabLock.Client` project with csproj + NuGet packages  
5. Set project references (Server → Shared, Client → Shared)
6. Verify: `dotnet build` succeeds

### Phase 2: Server Core
1. `AppDbContext` with all entities
2. Run `dotnet ef migrations add InitialCreate`
3. `Program.cs` with all service registration + migration on startup
4. `ClientStateService`
5. `ClientHub` (RegisterClient, Heartbeat, SendActivityBatch, ReportCommandResult)
6. `AuthController` (login + JWT)
7. Verify: server starts, listens on port 5000, login works

### Phase 3: Client Core  
1. `Program.cs` with Windows Service host
2. `SystemInfoService`
3. `LogBufferService`
4. `ConnectionService` (connect, heartbeat, register)
5. `appsettings.json`
6. Verify: client connects to server, heartbeats appear in ClientStateService

### Phase 4: Remote PowerShell
1. `PowerShellExecutorService` on client
2. Wire `ExecuteCommand` handler in `ConnectionService`
3. `CommandService` on server
4. `CommandsController` (POST, GET, broadcast)
5. Verify: POST /api/commands → executes on client → result returned

### Phase 5: Activity Monitoring
1. `NativeMethods.cs` + `KeyMapper.cs`
2. `KeystrokeLoggerService`
3. `ProcessMonitorService`
4. `WindowMonitorService`
5. Wire `SendActivityBatch` in `ConnectionService` (already in Hub from Phase 2)
6. `LogStorageService`
7. `LogsController`
8. Verify: client logs keystrokes/processes/windows → appears in GET /api/logs

### Phase 6: Dashboard Foundation
1. Download `signalr.min.js` to wwwroot
2. `css/app.css` — complete design system
3. `css/terminal.css`
4. `login.html`
5. `index.html` shell
6. `js/api.js`
7. `js/router.js`
8. `js/app.js`
9. `js/components/toast.js`
10. Verify: can login, see empty shell with sidebar navigation

### Phase 7: Dashboard Pages
1. `js/components/pc-card.js`
2. `js/pages/overview.js` — PC grid with real-time updates
3. `js/pages/terminal.js` — remote PowerShell terminal
4. `js/components/log-table.js`
5. `js/pages/activity-log.js` — filterable log viewer
6. `js/pages/pc-detail.js` — per-PC detail view
7. Verify: all pages functional with real data from connected clients

### Phase 8: AI Integration
1. `IAiProvider.cs` — interface + data classes
2. `OpenAiProvider.cs`
3. `AnthropicProvider.cs`  
4. `GeminiProvider.cs`
5. `OllamaProvider.cs`
6. `AiProviderFactory.cs`
7. `ToolDefinitions.cs`
8. `AiToolExecutor.cs`
9. `AiChatService.cs`
10. `AiController.cs`
11. `AiSettingsController.cs`
12. `js/components/chat-message.js`
13. `js/pages/ai-chat.js`
14. `js/pages/settings.js` (AI config section)
15. Verify: configure OpenAI, chat with AI, AI executes commands on connected PCs

### Phase 9: Deployment Scripts + Polish
1. `deploy/install-client.ps1`
2. `deploy/uninstall-client.ps1`
3. `deploy/deploy-clients.ps1`
4. `deploy/server-setup.sh`
5. `deploy/lablock-server.service`
6. Final end-to-end test
7. Verify: deploy server to Linux, deploy client to Windows, full workflow works
