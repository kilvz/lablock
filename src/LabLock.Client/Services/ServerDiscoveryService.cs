using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LabLock.Client.Services;

public class ServerDiscoveryService
{
    private readonly IConfiguration _config;
    private readonly ILogger<ServerDiscoveryService> _logger;

    public ServerDiscoveryService(IConfiguration config, ILogger<ServerDiscoveryService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public Task<string> DiscoverAsync(CancellationToken ct)
    {
        var configured = _config["LabLock:ServerUrl"] ?? "";
        if (string.IsNullOrEmpty(configured) || configured.Contains("SERVER_IP") || configured.Contains("CHANGE_ME"))
        {
            _logger.LogWarning("LabLock:ServerUrl missing or placeholder; using baked-in default {Url}", LabLockDefaults.ServerUrl);
            return Task.FromResult(LabLockDefaults.ServerUrl);
        }

        _logger.LogInformation("Using configured server URL: {Url}", configured);
        return Task.FromResult(configured);
    }
}
