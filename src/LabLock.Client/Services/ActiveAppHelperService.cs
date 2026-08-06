using System.Diagnostics;
using System.Management;
using System.Text;
using System.Text.Json;
using LabLock.Client.Helpers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LabLock.Client.Services;

/// <summary>
/// Spawns and supervises the active-app helper process in the interactive
/// user's session (the session-0 service cannot see the foreground window
/// itself) and exposes the latest foreground app snapshot for heartbeats
/// and window-focus logging.
/// </summary>
public class ActiveAppHelperService : BackgroundService
{
    private static readonly TimeSpan Staleness = TimeSpan.FromSeconds(15);

    private readonly SessionContextService _sessionContext;
    private readonly ILogger<ActiveAppHelperService> _logger;

    private int _lastSessionId = -1;
    private int _spawnedPid;

    public ActiveAppHelperService(
        SessionContextService sessionContext,
        ILogger<ActiveAppHelperService> logger)
    {
        _sessionContext = sessionContext;
        _logger = logger;
    }

    public (string Title, string Process) GetCurrent()
    {
        try
        {
            var fi = new FileInfo(ActiveAppHelper.FilePath);
            if (!fi.Exists || DateTime.UtcNow - fi.LastWriteTimeUtc > Staleness)
                return ("", "");

            using var doc = JsonDocument.Parse(File.ReadAllText(fi.FullName));
            var title = doc.RootElement.GetProperty("title").GetString() ?? "";
            var process = doc.RootElement.GetProperty("process").GetString() ?? "";
            return (title, process);
        }
        catch
        {
            return ("", "");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var sessionId = _sessionContext.GetInteractiveSessionId();

                if (sessionId != _lastSessionId)
                {
                    KillOrphanHelpers();
                    _lastSessionId = sessionId;
                    _spawnedPid = 0;
                }

                if (sessionId > 0 && !IsHelperAlive())
                    _spawnedPid = SpawnHelper((uint)sessionId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Active-app helper supervision error");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    private bool IsHelperAlive()
    {
        if (_spawnedPid <= 0) return false;
        try
        {
            return !Process.GetProcessById(_spawnedPid).HasExited;
        }
        catch
        {
            return false;
        }
    }

    private int SpawnHelper(uint sessionId)
    {
        if (!NativeMethods.WTSQueryUserToken(sessionId, out var token) || token == IntPtr.Zero)
            return 0;

        try
        {
            if (!NativeMethods.DuplicateTokenEx(
                token,
                NativeMethods.MAXIMUM_ALLOWED,
                IntPtr.Zero,
                NativeMethods.SecurityImpersonation,
                NativeMethods.TokenPrimary,
                out var primaryToken))
                return 0;

            try
            {
                var env = IntPtr.Zero;
                NativeMethods.CreateEnvironmentBlock(out env, primaryToken, false);

                try
                {
                    var exePath = Path.Combine(AppContext.BaseDirectory, "LabLock.Client.exe");
                    var cmdLine = new StringBuilder($"\"{exePath}\" --active-app-helper");

                    var si = new NativeMethods.STARTUPINFO
                    {
                        cb = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.STARTUPINFO>(),
                        lpDesktop = "winsta0\\default"
                    };

                    if (NativeMethods.CreateProcessAsUser(
                        primaryToken,
                        null,
                        cmdLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        false,
                        NativeMethods.CREATE_UNICODE_ENVIRONMENT | NativeMethods.CREATE_NO_WINDOW,
                        env,
                        Path.GetDirectoryName(exePath),
                        ref si,
                        out var pi))
                    {
                        var pid = (int)pi.dwProcessId;
                        _logger.LogInformation("Spawned active-app helper in session {SessionId} (pid {Pid})", sessionId, pid);
                        NativeMethods.CloseHandle(pi.hProcess);
                        NativeMethods.CloseHandle(pi.hThread);
                        return pid;
                    }

                    _logger.LogWarning("CreateProcessAsUser failed for active-app helper: {Error}",
                        System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                    return 0;
                }
                finally
                {
                    if (env != IntPtr.Zero)
                        NativeMethods.DestroyEnvironmentBlock(env);
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

    /// <summary>Kill leftover helper processes from a previous service run (e.g. after an update).</summary>
    private static void KillOrphanHelpers()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("LabLock.Client"))
            {
                try
                {
                    if (p.Id == Environment.ProcessId) continue;

                    using var mo = new ManagementObject($"win32_process.handle='{p.Id}'");
                    mo.Get();
                    var cmd = mo["CommandLine"]?.ToString() ?? "";
                    if (cmd.Contains("--active-app-helper"))
                    {
                        p.Kill();
                        p.WaitForExit(5000);
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }
}
