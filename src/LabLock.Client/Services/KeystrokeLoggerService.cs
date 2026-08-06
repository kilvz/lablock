using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using LabLock.Client.Helpers;
using LabLock.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LabLock.Client.Services;

public class KeystrokeLoggerService : BackgroundService
{
    private readonly LogBufferService _logBuffer;
    private readonly IConfiguration _config;
    private readonly ILogger<KeystrokeLoggerService> _logger;

    private IntPtr _hookId = IntPtr.Zero;
    private NativeMethods.LowLevelKeyboardProc _hookProc = null!;
    private readonly StringBuilder _keyBuffer = new();
    private string _currentWindow = "";
    private string _currentProcess = "";
    private DateTime _lastKeyTime;
    private Thread? _hookThread;

    public KeystrokeLoggerService(
        LogBufferService logBuffer,
        IConfiguration config,
        ILogger<KeystrokeLoggerService> logger)
    {
        _logBuffer = logBuffer;
        _config = config;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = _config["LabLock:EnableKeystrokeLogging"];
        if (enabled != null && !bool.Parse(enabled))
        {
            _logger.LogInformation("Keystroke logging is disabled");
            return Task.CompletedTask;
        }

        _hookThread = new Thread(() =>
        {
            _hookProc = HookCallback;
            _hookId = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_KEYBOARD_LL,
                _hookProc,
                NativeMethods.GetModuleHandle(Process.GetCurrentProcess().MainModule!.ModuleName),
                0
            );

            while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0))
            {
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessage(ref msg);
                if (stoppingToken.IsCancellationRequested) break;
            }

            if (_hookId != IntPtr.Zero)
                NativeMethods.UnhookWindowsHookEx(_hookId);
        });

        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.IsBackground = true;
        _hookThread.Start();

        var flushTimer = new System.Threading.Timer(_ => FlushKeyBuffer(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

        stoppingToken.Register(() =>
        {
            flushTimer.Dispose();
            if (_hookId != IntPtr.Zero)
                NativeMethods.UnhookWindowsHookEx(_hookId);
        });

        return Task.CompletedTask;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)NativeMethods.WM_KEYDOWN || wParam == (IntPtr)NativeMethods.WM_SYSKEYDOWN))
        {
            var hookStruct = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            var key = KeyMapper.Map(hookStruct.vkCode);
            var window = GetForegroundWindowInfo(out var process);

            if (window != _currentWindow)
                FlushKeyBuffer();

            _currentWindow = window;
            _currentProcess = process;
            _keyBuffer.Append(key);
            _lastKeyTime = DateTime.UtcNow;

            if (_keyBuffer.Length > 200)
                FlushKeyBuffer();
        }

        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private void FlushKeyBuffer()
    {
        if (_keyBuffer.Length == 0) return;

        var log = new ActivityLogDto
        {
            ClientId = _config["LabLock:ClientId"] ?? Environment.MachineName,
            EventType = EventType.Keystroke,
            Details = System.Text.Json.JsonSerializer.Serialize(new
            {
                keys = _keyBuffer.ToString(),
                window = _currentWindow,
                process = _currentProcess
            }),
            Timestamp = DateTime.UtcNow,
            Username = Environment.UserName
        };

        _logBuffer.Add(log);
        _keyBuffer.Clear();
    }

    private static string GetForegroundWindowInfo(out string process)
    {
        process = "";
        try
        {
            var hwnd = NativeMethods.GetForegroundWindow();
            var sb = new StringBuilder(256);
            NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            process = Process.GetProcessById((int)pid).ProcessName;
            return sb.ToString();
        }
        catch
        {
            return "";
        }
    }
}
