using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LabLock.Client.Helpers;

namespace LabLock.Client.Services;

/// <summary>
/// Runs inside the interactive user's session (spawned by ActiveAppHelperService
/// via CreateProcessAsUser). Polls GetForegroundWindow — which only works in an
/// interactive session, not from the session-0 service — and publishes the
/// current foreground window to a well-known file the service reads.
/// </summary>
public static class ActiveAppHelper
{
    public const string FilePath = @"C:\ProgramData\LabLock\activeapp.json";

    public static void Run()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        }
        catch
        {
        }

        while (true)
        {
            try
            {
                string title = "";
                string process = "";

                var hwnd = NativeMethods.GetForegroundWindow();
                if (hwnd != IntPtr.Zero)
                {
                    var sb = new StringBuilder(256);
                    NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
                    title = sb.ToString();

                    if (NativeMethods.GetWindowThreadProcessId(hwnd, out var pid) != 0)
                    {
                        try
                        {
                            process = Process.GetProcessById((int)pid).ProcessName;
                        }
                        catch
                        {
                        }
                    }
                }

                var payload = JsonSerializer.Serialize(new
                {
                    title,
                    process,
                    ts = DateTime.UtcNow
                });

                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, payload);
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch
            {
            }

            Thread.Sleep(750);
        }
    }
}
