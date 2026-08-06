using System.Diagnostics;
using System.Reflection;

namespace LabLock.Installer;

public static class InstallerCore
{
    public const string AppName = "LabLock";
    public const string BinaryName = "LabLock.Server.exe";

    public static string DefaultInstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);

    public static bool IsInstalled(string installDir) =>
        File.Exists(Path.Combine(installDir, BinaryName));

    public static void Install(string installDir, Action<int>? progress = null, Action<string>? log = null)
    {
        Directory.CreateDirectory(installDir);
        Directory.CreateDirectory(Path.Combine(installDir, "data"));

        StopRunningInstances();

        log?.Invoke("Extracting binaries...");
        foreach (var name in new[] { BinaryName, "appsettings.json" })
        {
            var dest = Path.Combine(installDir, name);
            ExtractResource(name, dest);
            log?.Invoke($"Extracted {name} ({new FileInfo(dest).Length / 1024 / 1024} MB)");
        }
        progress?.Invoke(70);

        log?.Invoke("Granting write permissions...");
        GrantUsersWriteAccess(installDir);
        progress?.Invoke(85);

        log?.Invoke("Creating shortcuts...");
        CreateStartMenuShortcut(installDir);
        CreateDesktopShortcut(installDir);
        progress?.Invoke(100);
    }

    private static void ExtractResource(string resourceName, string destPath)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {resourceName}");
        using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write);
        stream.CopyTo(fs);
    }

    private static void GrantUsersWriteAccess(string dir)
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = "icacls",
            Arguments = $"\"{dir}\" /grant \"Users\":(OI)(CI)M /T /Q",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        p?.WaitForExit(30000);
    }

    private static void StopRunningInstances()
    {
        foreach (var proc in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(BinaryName)))
        {
            try { proc.Kill(entireProcessTree: true); }
            catch { }
            proc.Dispose();
        }
    }

    private static void CreateStartMenuShortcut(string installDir)
    {
        var startMenu = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        var folder = Path.Combine(startMenu, AppName);
        Directory.CreateDirectory(folder);
        CreateLnk(Path.Combine(folder, "LabLock Server.lnk"), Path.Combine(installDir, BinaryName), installDir);
    }

    private static void CreateDesktopShortcut(string installDir)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        CreateLnk(Path.Combine(desktop, "LabLock Server.lnk"), Path.Combine(installDir, BinaryName), installDir);
    }

    private static void CreateLnk(string lnkPath, string targetPath, string workingDir)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null)
            return;

        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic lnk = shell.CreateShortcut(lnkPath);
            lnk.TargetPath = targetPath;
            lnk.WorkingDirectory = workingDir;
            lnk.Description = "LabLock Server - HTTP dashboard + MCP agent";
            lnk.IconLocation = targetPath + ",0";
            lnk.Save();
        }
        finally
        {
            try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
            catch { }
        }
    }
}
