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
    private CancellationTokenSource? _cts;
    private int _lastSessionId = -1;
    private int _spawnedPid;
    private bool _agentReady; // true when current agent has sent its "ready" handshake
    private string _pipeName = "";
    private readonly object _ioLock = new();
    private readonly byte[] _readBuf = new byte[2 * 1024 * 1024]; // 2MB buffer for large screenshots

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static void SvcLog(string msg)
    {
        var entry = $"[{DateTime.UtcNow:O}] SVC {msg}\n";
        foreach (var dir in new[] { @"C:\LabLock\logs", Environment.GetEnvironmentVariable("TEMP") ?? "", Environment.GetEnvironmentVariable("TMP") ?? "", Path.GetTempPath(), @"C:\Windows\Temp" })
        {
            if (string.IsNullOrEmpty(dir)) continue;
            try { Directory.CreateDirectory(dir); File.AppendAllText(Path.Combine(dir, "lablock-service.log"), entry); break; }
            catch { }
        }
    }

    public SessionAgentService(SessionContextService sessionContext, LogBufferService logBuffer)
    {
        _sessionContext = sessionContext;
        _logBuffer = logBuffer;
        _clientId = Environment.MachineName;
    }

    public void Start()
    {
        if (_pipeThread != null && _pipeThread.IsAlive) return;
        _cts?.Cancel();
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
                    SvcLog($"Session changed: {_lastSessionId} -> {sessionId}, killing agent and closing pipe");
                    _agentReady = false;
                    KillAgent();
                    if (_pipeHandle != IntPtr.Zero) { NativeMethods.CloseHandle(_pipeHandle); _pipeHandle = IntPtr.Zero; }
                    _lastSessionId = sessionId;
                }

                if (sessionId <= 0)
                {
                    SvcLog($"sessionId={sessionId} <= 0, sleeping");
                    Thread.Sleep(2000);
                    continue;
                }

                if (_pipeHandle == IntPtr.Zero)
                {
                    _pipeName = $"\\\\.\\pipe\\LabLockSessionAgent-S{sessionId}";
                    SvcLog($"Creating pipe {_pipeName}");

                    var sa = new NativeMethods.SECURITY_ATTRIBUTES();
                    IntPtr sdPtr = IntPtr.Zero, saPtr = IntPtr.Zero;
                    try
                    {
                        if (NativeMethods.ConvertStringSecurityDescriptorToSecurityDescriptor(
                            "D:(A;;GA;;;WD)", 1, out sdPtr, out var sdSize))
                        {
                            sa.nLength = (uint)Marshal.SizeOf<NativeMethods.SECURITY_ATTRIBUTES>();
                            sa.lpSecurityDescriptor = sdPtr;
                            sa.bInheritHandle = false;
                            saPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.SECURITY_ATTRIBUTES>());
                            Marshal.StructureToPtr(sa, saPtr, false);
                        }
                    }
                    catch { }

                    try
                    {
                        // 2MB buffer, non-overlapped (synchronous) — all I/O is sequential via _ioLock
                        _pipeHandle = NativeMethods.CreateNamedPipe(_pipeName,
                            0x00000003, // FILE_READ_DATA | FILE_WRITE_DATA (non-overlapped)
                            NativeMethods.PIPE_TYPE_MESSAGE | NativeMethods.PIPE_READMODE_MESSAGE,
                            1, 2 * 1024 * 1024, 2 * 1024 * 1024, 5000, saPtr);
                    }
                    finally
                    {
                        if (saPtr != IntPtr.Zero) Marshal.FreeHGlobal(saPtr);
                        if (sdPtr != IntPtr.Zero) NativeMethods.LocalFree(sdPtr);
                    }

                    if (_pipeHandle == IntPtr.Zero || _pipeHandle.ToInt64() == -1)
                    {
                        SvcLog($"CreateNamedPipe failed for {_pipeName}, err={Marshal.GetLastWin32Error()}");
                        _pipeHandle = IntPtr.Zero;
                        Thread.Sleep(1000);
                        continue;
                    }
                    SvcLog($"Pipe created: {_pipeName} handle=0x{_pipeHandle.ToInt64():X}");
                }

                if (!IsAgentAlive())
                {
                    _spawnedPid = SpawnAgent((uint)sessionId);
                    SvcLog($"Spawned session agent pid={_spawnedPid}");
                }

                var connected = NativeMethods.ConnectNamedPipe(_pipeHandle, IntPtr.Zero);
                if (connected)
                {
                    SvcLog("ConnectNamedPipe succeeded");
                }
                else
                {
                    var err = Marshal.GetLastWin32Error();
                    SvcLog($"ConnectNamedPipe failed, err={err}");
                    if (err != 535) // ERROR_PIPE_CONNECTED
                    {
                        NativeMethods.DisconnectNamedPipe(_pipeHandle);
                        Thread.Sleep(1000);
                        continue;
                    }
                    SvcLog(_agentReady ? "Pipe already connected, reusing" : "Pipe already connected, reading ready");
                }

                if (!_agentReady)
                {
                    var readyMsg = ReadOneMessage();
                    SvcLog($"Got ready: {readyMsg ?? "(null)"}, agent pid={_spawnedPid}");
                    _agentReady = true;
                }

                while (!_cts.Token.IsCancellationRequested && IsAgentAlive())
                    Thread.Sleep(2000);

                SvcLog("Agent died, disconnecting");
                _agentReady = false;
                NativeMethods.DisconnectNamedPipe(_pipeHandle);
                NativeMethods.CloseHandle(_pipeHandle);
                _pipeHandle = IntPtr.Zero;
                KillAgent();
            }
            catch (Exception ex)
            {
                SvcLog($"PipeLoop error: {ex.Message}");
                Thread.Sleep(2000);
            }
        }
    }

    private string? ReadOneMessage()
    {
        if (!NativeMethods.ReadFile(_pipeHandle, _readBuf, (uint)_readBuf.Length, out var bytesRead, IntPtr.Zero))
        {
            var err = Marshal.GetLastWin32Error();
            if (err == 234) // ERROR_MORE_DATA — message too large, read what we can
            {
                SvcLog($"ReadOneMessage got partial ({bytesRead} bytes), ERR_MORE_DATA");
            }
            else
            {
                SvcLog($"ReadOneMessage failed, err={err}");
                return null;
            }
        }
        if (bytesRead == 0) return null;
        var json = Encoding.UTF8.GetString(_readBuf, 0, (int)bytesRead);
        return json.TrimEnd('\n', '\r');
    }

    public Task<string> SendAsync(string action, Dictionary<string, object?>? parameters = null, int timeoutMs = 30000)
    {
        SvcLog($"SendAsync: action={action} timeout={timeoutMs}");
        if (_pipeHandle == IntPtr.Zero || _pipeHandle.ToInt64() == -1)
            throw new InvalidOperationException("Session agent pipe is not available");
        if (!IsAgentAlive())
            throw new InvalidOperationException("Session agent is not running");

        var id = Guid.NewGuid().ToString("N");
        var request = new Dictionary<string, object>
        {
            ["type"] = "request",
            ["id"] = id,
            ["action"] = action,
            ["params"] = parameters ?? new Dictionary<string, object?>()
        };

        var payload = JsonSerializer.Serialize(request, JsonOpts) + "\n";
        var bytes = Encoding.UTF8.GetBytes(payload);

        // Sequential I/O: write request, then read until we get the matching response
        lock (_ioLock)
        {
                if (!NativeMethods.WriteFile(_pipeHandle, bytes, (uint)bytes.Length, out _, IntPtr.Zero))
                {
                    var err = Marshal.GetLastWin32Error();
                    SvcLog($"SendAsync WriteFile failed, err={err}");
                    KillAgent();
                    throw new InvalidOperationException($"Cannot communicate with session agent: WriteFile failed err={err}");
                }

                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (DateTime.UtcNow < deadline)
                {
                    if (!IsAgentAlive()) throw new InvalidOperationException("Session agent died during request");

                    if (!NativeMethods.ReadFile(_pipeHandle, _readBuf, (uint)_readBuf.Length, out var bytesRead, IntPtr.Zero))
                    {
                        var err = Marshal.GetLastWin32Error();
                        if (err == 234) // ERROR_MORE_DATA — partial read, try to use what we got
                        {
                            SvcLog($"SendAsync read partial ({bytesRead} bytes)");
                        }
                        else
                        {
                            SvcLog($"SendAsync ReadFile failed, err={err}");
                            KillAgent();
                            throw new InvalidOperationException($"Pipe read failed err={err}");
                        }
                    }

                    if (bytesRead == 0) continue;

                    var json = Encoding.UTF8.GetString(_readBuf, 0, (int)bytesRead);
                    json = json.TrimEnd('\n', '\r');

                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

                        if (type == "response")
                        {
                            var respId = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                            if (respId == id)
                            {
                                var success = root.TryGetProperty("success", out var s) && s.GetBoolean();
                                var data = root.TryGetProperty("data", out var d) ? d.GetRawText() : "{}";
                                var resultJson = success ? data : "{}";
                                SvcLog($"SendAsync got matching response id={respId}");
                                return Task.FromResult(resultJson);
                            }
                            SvcLog($"SendAsync got response for different id={respId}, ignoring");
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
                            // ignore — just a keep-alive
                        }
                    }
                    catch (JsonException)
                    {
                        // skip garbled messages
                    }
                }

                throw new TimeoutException($"Session agent did not respond within {timeoutMs}ms");
            }
    }

    public string RunElevatedPowerShell(string command, int timeoutSec)
    {
        SvcLog($"RunElevatedPowerShell: timeout={timeoutSec}s");
        var user = _sessionContext.GetInteractiveUserAccount();
        if (string.IsNullOrEmpty(user))
            throw new InvalidOperationException("No interactive user available");

        var sessionId = _sessionContext.GetInteractiveSessionId();
        if (sessionId <= 0)
            throw new InvalidOperationException("No interactive session");

        var taskName = $"LabLockElevated-{Guid.NewGuid():N}";
        var tmpDir = @"C:\Users\Public\lablock-elev";
        try { Directory.CreateDirectory(tmpDir); } catch { }
        var scriptPath = Path.Combine(tmpDir, $"elev-{Guid.NewGuid():N}.ps1");
        var innerPath = Path.Combine(tmpDir, $"inner-{Guid.NewGuid():N}.ps1");
        var outPath = Path.Combine(tmpDir, $"out-{Guid.NewGuid():N}.out");

        try
        {
            File.WriteAllText(innerPath, command, new System.Text.UTF8Encoding(false));
            SvcLog($"Elevated inner script: {innerPath}, out: {outPath}");

            // Wrapper: run the user command and capture ALL streams to outPath.
            // Task Scheduler /tr cannot do shell redirection, so it lives inside the script.
            var wrapper = $"$ErrorActionPreference = 'Continue'\r\n" +
                          $"try {{ & '{innerPath}' *> '{outPath}' 2>&1 }} " +
                          $"catch {{ $_ | Out-File -Encoding utf8 '{outPath}' }}\r\n";
            File.WriteAllText(scriptPath, wrapper, new System.Text.UTF8Encoding(false));

            var tr = $"powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"";
            var startTime = DateTime.Now.AddMinutes(1).ToString("HH:mm");

            SvcLog($"Creating elevated task {taskName} for user {user}");
            var create = RunProc("schtasks.exe",
                $"/create /tn \"{taskName}\" /tr \"{tr}\" /sc once /st {startTime} /rl HIGHEST /ru \"{user}\" /it /f");
            SvcLog($"schtasks create: exit={create.ExitCode} {create.Output}");
            if (create.ExitCode != 0)
                throw new InvalidOperationException($"schtasks create failed: {create.Output}");

            SvcLog($"Running elevated task {taskName}");
            var run = RunProc("schtasks.exe", $"/run /tn \"{taskName}\"");
            SvcLog($"schtasks run: exit={run.ExitCode} {run.Output}");
            if (run.ExitCode != 0)
                throw new InvalidOperationException($"schtasks run failed: {run.Output}");

            // Wait for the output file to appear AND be readable (task may take time)
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
            while (DateTime.UtcNow < deadline && !File.Exists(outPath))
            {
                Thread.Sleep(500);
                SvcLog($"Elevated waiting for output... exists={File.Exists(outPath)}");
            }

            string output = "", error = "";
            if (File.Exists(outPath))
            {
                // Wait until the elevated process has fully closed the file (it may still be writing)
                while (DateTime.UtcNow < deadline)
                {
                    try
                    {
                        using (var fs = new FileStream(outPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var reader = new StreamReader(fs, Encoding.UTF8))
                        {
                            output = reader.ReadToEnd().Trim();
                        }
                        break;
                    }
                    catch (IOException)
                    {
                        Thread.Sleep(300);
                    }
                }
                SvcLog($"Elevated output captured ({output.Length} chars)");
            }
            else
            {
                SvcLog($"Elevated output NOT found at {outPath} after {timeoutSec}s");
            }

            // Get exit code via task query
            var q = RunProc("schtasks.exe", $"/query /tn \"{taskName}\" /fo list /v");
            var exitCode = 0;
            foreach (var line in q.Output.Split('\n'))
            {
                if (line.Contains("Last Result"))
                {
                    var val = line.Split(':', 2)[^1].Trim();
                    int.TryParse(val, out exitCode);
                }
            }

            SvcLog($"Elevated task finished exit={exitCode}");

            return System.Text.Json.JsonSerializer.Serialize(new
            {
                output,
                error,
                exitCode,
                elevated = true,
                note = "Elevated via Task Scheduler (no UAC prompt)"
            });
        }
        finally
        {
            RunProc("schtasks.exe", $"/delete /tn \"{taskName}\" /f");
            try { File.Delete(scriptPath); } catch { }
            try { File.Delete(innerPath); } catch { }
            try { File.Delete(outPath); } catch { }
        }
    }

    private static (int ExitCode, string Output) RunProc(string file, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return (-1, "Failed to start process");
            p.WaitForExit(60000);
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            return (p.ExitCode, (stdout + stderr).Trim());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
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
                    var cmdLine = new StringBuilder($"\"{exePath}\" --session-agent \"{_pipeName}\"");

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
