using System.Security.Cryptography;
using System.Text.Json;
using LabLock.Server.Hubs;
using LabLock.Shared.Models;
using Microsoft.AspNetCore.SignalR;

namespace LabLock.Server.Services;

public class ClientUpdateService
{
    public const string PackageFileName = "LabLock.Client.exe";
    public const string ManifestFileName = "version.json";

    private readonly ClientStateService _clientState;
    private readonly IHubContext<ClientHub> _hubContext;
    private readonly string _rootDir;

    public ClientUpdateService(ClientStateService clientState, IHubContext<ClientHub> hubContext, IConfiguration config)
    {
        _clientState = clientState;
        _hubContext = hubContext;
        _rootDir = config["LabLock:UpdateDir"] ?? Path.Combine(AppContext.BaseDirectory, "data", "client-update");
        Directory.CreateDirectory(_rootDir);
    }

    public string PackagePath => Path.Combine(_rootDir, PackageFileName);
    public string ManifestPath => Path.Combine(_rootDir, ManifestFileName);
    public bool HasPackage => File.Exists(PackagePath);

    public UpdateManifest? GetManifest()
    {
        if (!File.Exists(ManifestPath)) return null;

        try
        {
            return JsonSerializer.Deserialize<UpdateManifest>(File.ReadAllText(ManifestPath));
        }
        catch
        {
            return null;
        }
    }

    public async Task<UpdateManifest> PublishAsync(Stream fileStream, string version)
    {
        Directory.CreateDirectory(_rootDir);

        var temp = PackagePath + ".tmp";
        using (var fs = File.Create(temp))
            await fileStream.CopyToAsync(fs);

        File.Move(temp, PackagePath, overwrite: true);

        var manifest = new UpdateManifest
        {
            Version = version,
            Size = new FileInfo(PackagePath).Length,
            Sha256 = ComputeSha256(PackagePath),
            UploadedAt = DateTime.UtcNow
        };

        File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        return manifest;
    }

    public async Task<string> PushUpdateAsync(string clientId, string? versionOverride = null)
    {
        var manifest = GetManifest();
        var version = versionOverride ?? manifest?.Version;
        if (string.IsNullOrWhiteSpace(version))
            throw new InvalidOperationException("No client update package has been published");

        var connId = _clientState.GetConnectionId(clientId);
        if (connId == null)
            throw new InvalidOperationException("Client is not online");

        var dto = new ClientUpdateDto
        {
            Version = version,
            Checksum = manifest?.Sha256 ?? "",
            Filename = PackageFileName
        };

        await _hubContext.Clients.Client(connId).SendAsync("UpdateAgent", dto);
        return version;
    }

    private static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }
}

public class UpdateManifest
{
    public string Version { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public DateTime UploadedAt { get; set; }
}
