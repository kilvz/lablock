using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using LabLock.Shared.Models;
using Microsoft.Extensions.Logging;

namespace LabLock.Client.Services;

public class ClientUpdateService
{
    private readonly ILogger<ClientUpdateService> _logger;
    private readonly HttpClient _http;

    public ClientUpdateService(ILogger<ClientUpdateService> logger)
    {
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    public async Task ApplyAsync(ClientUpdateDto update, string serverUrl, string apiKey)
    {
        try
        {
            var installDir = AppContext.BaseDirectory;
            var currentExe = Path.Combine(installDir, update.Filename);
            if (!File.Exists(currentExe))
            {
                _logger.LogWarning("Current agent file not found at {Path}; cannot apply update", currentExe);
                return;
            }

            var currentVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";
            if (!ShouldApply(update.Version, currentVersion))
            {
                _logger.LogInformation("Update {UpdateVersion} is not newer than current {CurrentVersion}; skipping", update.Version, currentVersion);
                return;
            }

            var stageDir = Path.Combine(installDir, "update");
            Directory.CreateDirectory(stageDir);
            var newFile = Path.Combine(stageDir, update.Filename + ".new");

            var url = $"{serverUrl.TrimEnd('/')}/api/update/package?apiKey={Uri.EscapeDataString(apiKey)}";
            _logger.LogInformation("Downloading agent update {Version} from {Url}", update.Version, url);

            using (var response = await _http.GetAsync(url))
            {
                response.EnsureSuccessStatusCode();
                using var fs = File.Create(newFile);
                await response.Content.CopyToAsync(fs);
            }

            if (!string.IsNullOrWhiteSpace(update.Checksum))
            {
                var actual = ComputeSha256(newFile);
                if (!string.Equals(actual, update.Checksum, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogError("Checksum mismatch for update (expected {Expected}, got {Actual}); aborting", update.Checksum, actual);
                    File.Delete(newFile);
                    return;
                }
                _logger.LogInformation("Update checksum verified");
            }

            var scriptPath = Path.Combine(stageDir, "apply-update.cmd");
            File.WriteAllText(scriptPath, BuildApplyScript(currentExe, newFile), Encoding.ASCII);

            _logger.LogInformation("Launching apply-update script for version {Version}", update.Version);
            var psi = new ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/c \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);

            _logger.LogInformation("Update {Version} staged; apply script will stop, swap and restart the service", update.Version);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply update {Version}", update.Version);
        }
    }

    private static string BuildApplyScript(string currentExe, string newFile)
    {
        return $"""
            @echo off
            sc stop LabLockAgent >nul 2>&1
            taskkill /f /im LabLock.Client.exe >nul 2>&1

            set tries=0
            :waitkill
            tasklist /fi "imagename eq LabLock.Client.exe" 2>nul | findstr /i "LabLock.Client.exe" >nul 2>&1
            if errorlevel 1 goto copystep
            taskkill /f /im LabLock.Client.exe >nul 2>&1
            ping 127.0.0.1 -n 2 >nul
            set /a tries+=1
            if %tries% lss 30 goto waitkill

            :copystep
            set ctry=0
            :copy
            copy /y "{newFile}" "{currentExe}" >nul 2>&1
            if not errorlevel 1 goto ok
            taskkill /f /im LabLock.Client.exe >nul 2>&1
            ping 127.0.0.1 -n 3 >nul
            set /a ctry+=1
            if %ctry% lss 10 goto copy

            :ok
            del /q "{newFile}" >nul 2>&1
            sc start LabLockAgent >nul 2>&1
            exit /b 0
            """;
    }

    private static bool ShouldApply(string incoming, string current)
    {
        if (Version.TryParse(incoming, out var i) && Version.TryParse(current, out var c))
            return i > c;

        return true;
    }

    private static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }
}
