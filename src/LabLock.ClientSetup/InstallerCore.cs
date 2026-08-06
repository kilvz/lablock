using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;

namespace LabLock.ClientSetup;

public static class InstallerCore
{
    public const string AppName = "LabLock";
    public const string BinaryName = "LabLock.Client.exe";
    public const string ServiceName = "LabLockAgent";

    public const string DefaultServerUrl = "http://192.168.1.58:5000";
    public const string DefaultApiKey = "LabLockKey-2026";

    public sealed record ClientOptions(string? ServerUrl = null, string? ApiKey = null, string? ClientId = null);

    public static string DefaultInstallDir => @"C:\LabLock";

    /// <summary>Default permissive service DACL (SYSTEM + Administrators full).</summary>
    private const string DefaultServiceSddl =
        "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)" +
        "(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)" +
        "(A;;CCLCSWLOCRRC;;;IU)" +
        "(A;;CCLCSWLOCRRC;;;SU)" +
        "(A;;RPWPCR;;;BU)";

    /// <summary>
    /// Hardened service DACL: SYSTEM full; everyone else kept, but DELETE and
    /// STOP are denied to Administrators and Users so sc delete / net stop fail.
    /// </summary>
    private const string HardenedServiceSddl =
        "D:" +
        "(D;;WPDC;;;BA)" +
        "(D;;WPDC;;;BU)" +
        "(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)" +
        "(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)" +
        "(A;;CCLCSWLOCRRC;;;IU)" +
        "(A;;CCLCSWLOCRRC;;;SU)" +
        "(A;;RPWPCR;;;BU)";

    public static bool IsInstalled(string installDir) =>
        File.Exists(Path.Combine(installDir, BinaryName));

    public static void Install(string installDir, Action<int>? progress = null, Action<string>? log = null, ClientOptions? options = null)
    {
        progress?.Invoke(5);

        log?.Invoke("Stopping existing service...");
        Run("sc.exe", $"stop {ServiceName}");
        Run("sc.exe", $"delete {ServiceName}");

        Directory.CreateDirectory(installDir);

        log?.Invoke("Extracting binaries...");
        foreach (var name in new[] { BinaryName, "appsettings.json" })
        {
            var dest = Path.Combine(installDir, name);
            ExtractResource(name, dest);
        }
        progress?.Invoke(45);

        log?.Invoke("Applying server configuration...");
        ApplyClientConfig(Path.Combine(installDir, "appsettings.json"), options ?? new ClientOptions());

        log?.Invoke("Registering service (auto-start, LocalSystem)...");
        var binPath = Path.Combine(installDir, BinaryName);
        var create = Run("sc.exe",
            $"create {ServiceName} type= own start= auto error= ignore " +
            $"binPath= \"\\\"{binPath}\\\"\" DisplayName= \"{AppName} Client Agent\" obj= LocalSystem");
        if (create.ExitCode != 0)
            throw new InvalidOperationException($"sc create failed (code {create.ExitCode}): {create.Stderr}");
        progress?.Invoke(60);

        log?.Invoke("Configuring auto-restart on kill/crash...");
        Run("sc.exe", $"failure {ServiceName} reset= 0 actions= restart/5000/restart/10000/restart/30000/restart/60000");
        Run("sc.exe", $"failure {ServiceName} flag= 1");

        log?.Invoke("Hardening service (delete/stop denied to users & admins)...");
        var sdset = Run("sc.exe", $"sdset {ServiceName} {HardenedServiceSddl}");
        if (sdset.ExitCode != 0)
            throw new InvalidOperationException($"sc sdset failed (code {sdset.ExitCode}): {sdset.Stderr}");
        progress?.Invoke(75);

        log?.Invoke("Hardening file permissions (delete/overwrite denied)...");
        HardenFolderAcl(installDir);
        progress?.Invoke(90);

        log?.Invoke("Starting service...");
        Run("sc.exe", $"start {ServiceName}");
        progress?.Invoke(100);
    }

    public static void Uninstall(string installDir, Action<int>? progress = null, Action<string>? log = null)
    {
        progress?.Invoke(10);
        log?.Invoke("Resetting service permissions...");
        Run("sc.exe", $"sdset {ServiceName} {DefaultServiceSddl}");

        log?.Invoke("Stopping and removing service...");
        Run("sc.exe", $"stop {ServiceName}");
        Run("sc.exe", $"delete {ServiceName}");
        Run("taskkill.exe", $"/f /im {BinaryName}");
        progress?.Invoke(50);

        log?.Invoke("Removing files...");
        if (Directory.Exists(installDir))
        {
            Run("takeown.exe", $"/f \"{installDir}\" /r /d y");
            Run("icacls.exe", $"\"{installDir}\" /grant:r \"{Environment.UserName}\":F /t /c");
            Run("cmd.exe", $"/c rmdir /s /q \"{installDir}\"");
        }
        progress?.Invoke(90);

        log?.Invoke("Removing Start Menu entries...");
        RemoveStartMenuEntries();

        progress?.Invoke(100);
    }

    private static void ExtractResource(string resourceName, string destPath)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {resourceName}");
        using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write);
        stream.CopyTo(fs);
    }

    private static void ApplyClientConfig(string path, ClientOptions options)
    {
        var node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidOperationException("appsettings.json is not a JSON object");

        var section = node["LabLock"] as JsonObject
            ?? throw new InvalidOperationException("appsettings.json has no LabLock section");

        section["ServerUrl"] = options.ServerUrl ?? DefaultServerUrl;
        section["ApiKey"] = options.ApiKey ?? DefaultApiKey;
        if (options.ClientId != null)
            section["ClientId"] = options.ClientId;

        using var fs = File.Create(path);
        using var writer = new System.Text.Json.Utf8JsonWriter(fs, new System.Text.Json.JsonWriterOptions { Indented = true });
        node.WriteTo(writer);
        writer.Flush();
        fs.Flush();
    }

    private static void HardenFolderAcl(string dir)
    {
        // SYSTEM full; Administrators + Users read/execute only; deny delete/write to both.
        Run("icacls.exe", $"\"{dir}\" /inheritance:r");
        Run("icacls.exe", $"\"{dir}\" /grant:r \"*S-1-5-18\":(OI)(CI)F \"Administrators\":(OI)(CI)RX \"Users\":(OI)(CI)RX");
        Run("icacls.exe", $"\"{dir}\" /deny \"Administrators\":(OI)(CI)(DE)(DC)(WD)(AD)");
        Run("icacls.exe", $"\"{dir}\" /deny \"Users\":(OI)(CI)(DE)(DC)(WD)(AD)");
    }

    private static void RemoveStartMenuEntries()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName);
        if (Directory.Exists(folder))
        {
            try { Directory.Delete(folder, recursive: true); }
            catch { }
        }
    }

    public static void CreateStartMenuUninstallerShortcut(string setupExe)
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName);
        Directory.CreateDirectory(folder);

        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null)
            return;

        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic lnk = shell.CreateShortcut(Path.Combine(folder, "Uninstall LabLock Client.lnk"));
            lnk.TargetPath = setupExe;
            lnk.Arguments = "/uninstall";
            lnk.Description = "Uninstall LabLock Client agent (admin required)";
            lnk.Save();
        }
        finally
        {
            try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
            catch { }
        }
    }

    private static (int ExitCode, string Stderr) Run(string fileName, string arguments)
    {
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        p.Start();
        var stderrTask = p.StandardError.ReadToEndAsync();
        p.WaitForExit(60000);
        return (p.ExitCode, stderrTask.Result);
    }
}
