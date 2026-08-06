using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using LabLock.Client.Helpers;

namespace LabLock.Client.Services;

public class SessionAgentService : IDisposable
{
    private readonly SessionContextService _sessionContext;
    private readonly LogBufferService _logBuffer;
    private readonly string _clientId;

    private IntPtr _pipeHandle;
    private Thread? _pipeThread;
    private Thread? _readThread;
    private CancellationTokenSource? _cts;
    private int _lastSessionId = -1;
    private int _spawnedPid;
    private string _pipeName = "";
    private readonly object _sendLock = new();
    private readonly byte[] _readBuf = new byte[65536];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private record PendingRequest(TaskCompletionSource<JsonElement> Tcs, DateTime Created);

    private readonly Dictionary<string, PendingRequest> _pending = new();
    private readonly object _pendingLock = new();

    public SessionAgentService(SessionContextService sessionContext, LogBufferService logBuffer)
    {
        _sessionContext = sessionContext;
        _logBuffer = logBuffer;
        _clientId = Environment.MachineName;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _pipeThread = new Thread(PipeLoop) { IsBackground = true };
        _pipeThread.Start();
    }

    public void Stop()
    {
        _cts?.Cancel();
        KillAgent();
        if (_pipeHandle != IntPtr.Zero) { NativeMethods.CloseHandle(_pipeHandle); _pipeHandle = IntPtr.Zero; }
    }

    private void PipeLoop()
    {
        if (_cts == null) return;

        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                var sessionId = _sessionContext.GetInteractiveSessionId();
                if (sessionId != _lastSessionId)
                {
                    KillAgent();
                    if (_pipeHandle != IntPtr.Zero) { NativeMethods.CloseHandle(_pipeHandle); _pipeHandle = IntPtr.Zero; }
                    _lastSessionId = sessionId;
                }

                if (sessionId <= 0)
                {
                    Thread.Sleep(2000);
                    continue;
                }

                if (_pipeHandle == IntPtr.Zero)
                {
                    _pipeName = $"\\\\.\\pipe\\LabLockSessionAgent-{_sessionContext.GetInteractiveUser()}";
                    _pipeHandle = NativeMethods.CreateNamedPipe(_pipeName,
                        NativeMethods.PIPE_ACCESS_DUPLEX, NativeMethods.PIPE_TYPE_MESSAGE | NativeMethods.PIPE_READMODE_MESSAGE,
                        1, 65536, 65536, 5000, IntPtr.Zero);

                    if (_pipeHandle == IntPtr.Zero || _pipeHandle.ToInt64() == -1)
                    {
                        _pipeHandle = IntPtr.Zero;
                        Thread.Sleep(1000);
                        continue;
                    }
                }

                if (!IsAgentAlive())
                    _spawnedPid = SpawnAgent((uint)sessionId);

                if (NativeMethods.ConnectNamedPipe(_pipeHandle, IntPtr.Zero))
                {
                    _readThread = new Thread(ReadLoop) { IsBackground = true };
                    _readThread.Start();
                }
                else
                {
                    var err = Marshal.GetLastWin32Error();
                    if (err != 535) // ERROR_PIPE_CONNECTED
                    {
                        NativeMethods.DisconnectNamedPipe(_pipeHandle);
                        Thread.Sleep(1000);
                        continue;
                    }
                    _readThread = new Thread(ReadLoop) { IsBackground = true };
                    _readThread.Start();
                }

                while (!_cts.Token.IsCancellationRequested && IsAgentAlive())
                    Thread.Sleep(2000);

                NativeMethods.DisconnectNamedPipe(_pipeHandle);
                KillAgent();
            }
            catch
            {
                Thread.Sleep(2000);
            }
        }
    }

    private void ReadLoop()
    {
        while (_cts is { IsCancellationRequested: false })
        {
            try
            {
                if (!NativeMethods.ReadFile(_pipeHandle, _readBuf, (uint)_readBuf.Length, out var bytesRead, IntPtr.Zero))
                    break;

                if (bytesRead == 0) continue;

                var json = Encoding.UTF8.GetString(_readBuf, 0, (int)bytesRead);
                json = json.TrimEnd('\n', '\r');

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

                if (type == "response")
                {
                    var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                    lock (_pendingLock)
                    {
                        if (_pending.TryGetValue(id, out var pr))
                        {
                            _pending.Remove(id);
                            var data = root.TryGetProperty("success", out var s) && s.GetBoolean()
                                ? root.TryGetProperty("data", out var d) ? d : default
                                : default;
                            pr.Tcs.TrySetResult(data);
                        }
                    }
                }
                else if (type == "keystrokes")
                {
                    if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var entry in data.EnumerateArray())
                        {
                            _logBuffer.Add(new Shared.Models.ActivityLogDto
                            {
                                ClientId = _clientId,
                                EventType = Shared.Models.EventType.Keystroke,
                                Details = JsonSerializer.Serialize(entry, JsonOpts),
                                Timestamp = DateTime.UtcNow,
                                Username = _sessionContext.GetInteractiveUser()
                            });
                        }
                    }
                }
                else if (type == "ready")
                {
                    // agent connected, ready for commands
                }
            }
            catch (Exception)
            {
                break;
            }
        }
    }

    public async Task<JsonElement> SendAsync(string action, Dictionary<string, object?>? parameters = null, int timeoutMs = 30000)
    {
        var id = Guid.NewGuid().ToString("N");
        var request = new Dictionary<string, object>
        {
            ["type"] = "request",
            ["id"] = id,
            ["action"] = action,
            ["params"] = parameters ?? new Dictionary<string, object?>()
        };

        var tcs = new TaskCompletionSource<JsonElement>();
        lock (_pendingLock) { _pending[id] = new PendingRequest(tcs, DateTime.UtcNow); }

        try
        {
            lock (_sendLock)
            {
                var payload = JsonSerializer.Serialize(request, JsonOpts) + "\n";
                var bytes = Encoding.UTF8.GetBytes(payload);
                if (!NativeMethods.WriteFile(_pipeHandle, bytes, (uint)bytes.Length, out _, IntPtr.Zero))
                    throw new Exception($"Pipe write failed: {Marshal.GetLastWin32Error()}");
            }

            var winner = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            if (winner != tcs.Task)
            {
                lock (_pendingLock) { _pending.Remove(id); }
                throw new TimeoutException($"Session agent did not respond within {timeoutMs}ms");
            }

            return await tcs.Task;
        }
        finally
        {
            lock (_pendingLock) { _pending.Remove(id); }
        }
    }

    private bool IsAgentAlive()
    {
        if (_spawnedPid <= 0) return false;
        try { return !Process.GetProcessById(_spawnedPid).HasExited; }
        catch { return false; }
    }

    private void KillAgent()
    {
        if (_spawnedPid <= 0) return;
        try { Process.GetProcessById(_spawnedPid).Kill(); }
        catch { }
        _spawnedPid = 0;
    }

    private int SpawnAgent(uint sessionId)
    {
        if (!NativeMethods.WTSQueryUserToken(sessionId, out var token) || token == IntPtr.Zero)
            return 0;

        try
        {
            if (!NativeMethods.DuplicateTokenEx(token, NativeMethods.MAXIMUM_ALLOWED, IntPtr.Zero,
                NativeMethods.SecurityImpersonation, NativeMethods.TokenPrimary, out var primaryToken))
                return 0;

            try
            {
                var env = IntPtr.Zero;
                NativeMethods.CreateEnvironmentBlock(out env, primaryToken, false);

                try
                {
                    var exePath = Path.Combine(AppContext.BaseDirectory, "LabLock.Client.exe");
                    var cmdLine = new StringBuilder($"\"{exePath}\" --session-agent");

                    var si = new NativeMethods.STARTUPINFO
                    {
                        cb = Marshal.SizeOf<NativeMethods.STARTUPINFO>(),
                        lpDesktop = "winsta0\\default"
                    };

                    if (NativeMethods.CreateProcessAsUser(primaryToken, null, cmdLine, IntPtr.Zero, IntPtr.Zero,
                        false, NativeMethods.CREATE_UNICODE_ENVIRONMENT | NativeMethods.CREATE_NO_WINDOW, env,
                        Path.GetDirectoryName(exePath), ref si, out var pi))
                    {
                        var pid = (int)pi.dwProcessId;
                        NativeMethods.CloseHandle(pi.hProcess);
                        NativeMethods.CloseHandle(pi.hThread);
                        return pid;
                    }

                    return 0;
                }
                finally
                {
                    if (env != IntPtr.Zero) NativeMethods.DestroyEnvironmentBlock(env);
                }
            }
            finally
            {
                NativeMethods.CloseHandle(primaryToken);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(token);
        }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
