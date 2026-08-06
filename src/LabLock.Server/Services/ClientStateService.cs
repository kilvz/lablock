using System.Collections.Concurrent;
using LabLock.Shared.Models;

namespace LabLock.Server.Services;

public class ClientStateService
{
    public class ClientState
    {
        public string ClientId { get; set; } = "";
        public string Hostname { get; set; } = "";
        public string IpAddress { get; set; } = "";
        public string OsVersion { get; set; } = "";
        public string AgentVersion { get; set; } = "";
        public string CurrentUser { get; set; } = "";
        public string InteractiveUser { get; set; } = "";
        public int InteractiveSessionId { get; set; }
        public int AgentSessionId { get; set; }
        public string ActiveWindow { get; set; } = "";
        public string ActiveProcess { get; set; } = "";
        public double CpuPercent { get; set; }
        public double MemoryPercent { get; set; }
        public bool IsOnline { get; set; }
        public DateTime LastHeartbeat { get; set; }
        public DateTime FirstSeen { get; set; }
        public string ConnectionId { get; set; } = "";
    }

    private readonly ConcurrentDictionary<string, ClientState> _clients = new();
    private readonly ConcurrentDictionary<string, string> _connectionToClient = new();

    public static readonly TimeSpan OnlineThreshold = TimeSpan.FromSeconds(120);

    public static bool IsOnline(DateTime lastSeen, DateTime now) =>
        lastSeen != default && now - lastSeen <= OnlineThreshold;

    public void Register(ClientRegistrationDto dto, string connectionId)
    {
        _clients.AddOrUpdate(dto.ClientId,
            new ClientState
            {
                ClientId = dto.ClientId,
                Hostname = dto.Hostname,
                IpAddress = dto.IpAddress,
                OsVersion = dto.OsVersion,
                AgentVersion = dto.AgentVersion,
                InteractiveUser = dto.InteractiveUser,
                InteractiveSessionId = dto.InteractiveSessionId,
                AgentSessionId = dto.AgentSessionId,
                IsOnline = true,
                ConnectionId = connectionId,
                FirstSeen = DateTime.UtcNow,
                LastHeartbeat = DateTime.UtcNow
            },
            (key, existing) =>
            {
                existing.IsOnline = true;
                existing.ConnectionId = connectionId;
                existing.InteractiveUser = dto.InteractiveUser;
                existing.InteractiveSessionId = dto.InteractiveSessionId;
                existing.AgentSessionId = dto.AgentSessionId;
                existing.LastHeartbeat = DateTime.UtcNow;
                return existing;
            });
        _connectionToClient[connectionId] = dto.ClientId;
    }

    public void RegisterClient(string clientId, string connectionId)
    {
        _clients.AddOrUpdate(clientId,
            new ClientState
            {
                ClientId = clientId,
                IsOnline = true,
                ConnectionId = connectionId,
                FirstSeen = DateTime.UtcNow,
                LastHeartbeat = DateTime.UtcNow
            },
            (key, existing) =>
            {
                existing.IsOnline = true;
                existing.ConnectionId = connectionId;
                existing.LastHeartbeat = DateTime.UtcNow;
                return existing;
            });
        _connectionToClient[connectionId] = clientId;
    }

    public void UnregisterClient(string connectionId)
    {
        if (_connectionToClient.TryRemove(connectionId, out var clientId))
        {
            if (_clients.TryGetValue(clientId, out var state))
            {
                state.IsOnline = false;
                state.ConnectionId = "";
            }
        }
    }

    public string? GetClientId(string connectionId) =>
        _connectionToClient.GetValueOrDefault(connectionId);

    public void UpdateHeartbeat(HeartbeatDto dto)
    {
        if (_clients.TryGetValue(dto.ClientId, out var state))
        {
            state.CurrentUser = dto.CurrentUser;
            state.InteractiveUser = dto.InteractiveUser;
            state.InteractiveSessionId = dto.InteractiveSessionId;
            state.AgentSessionId = dto.AgentSessionId;
            state.ActiveWindow = dto.ActiveWindow;
            state.ActiveProcess = dto.ActiveProcess;
            state.CpuPercent = dto.CpuPercent;
            state.MemoryPercent = dto.MemoryPercent;
            state.LastHeartbeat = DateTime.UtcNow;
            state.IsOnline = true;
            if (!string.IsNullOrEmpty(dto.AgentVersion))
                state.AgentVersion = dto.AgentVersion;
        }
    }

    public void UpdateHeartbeat(string clientId, SystemInfoDto sysInfo)
    {
        if (_clients.TryGetValue(clientId, out var state))
        {
            state.Hostname = sysInfo.Hostname ?? state.Hostname;
            state.IpAddress = sysInfo.IpAddress ?? state.IpAddress;
            state.OsVersion = sysInfo.OsVersion ?? state.OsVersion;
            state.CurrentUser = sysInfo.CurrentUser ?? state.CurrentUser;
            state.InteractiveUser = sysInfo.InteractiveUser ?? state.InteractiveUser;
            state.InteractiveSessionId = sysInfo.InteractiveSessionId;
            state.AgentSessionId = sysInfo.AgentSessionId;
            state.ActiveProcess = sysInfo.ActiveProcess ?? state.ActiveProcess;
            state.CpuPercent = sysInfo.CpuPercent;
            state.MemoryPercent = sysInfo.MemoryPercent;
            state.LastHeartbeat = DateTime.UtcNow;
            state.IsOnline = true;
        }
    }

    public void UpdateActiveProcess(string clientId, string processName, string windowTitle)
    {
        if (_clients.TryGetValue(clientId, out var state))
        {
            state.ActiveProcess = processName;
            state.ActiveWindow = windowTitle;
            state.LastHeartbeat = DateTime.UtcNow;
        }
    }

    public void SetOffline(string clientId)
    {
        if (_clients.TryGetValue(clientId, out var state))
            state.IsOnline = false;
    }

    public ClientState? GetClient(string clientId) =>
        _clients.GetValueOrDefault(clientId);

    public List<ClientState> GetAllClients() =>
        _clients.Values.ToList();

    public string? GetConnectionId(string clientId) =>
        GetClient(clientId)?.ConnectionId;

    public ClientState? GetClientState(string clientId) =>
        GetClient(clientId);

    public int OnlineCount => _clients.Values.Count(c => c.IsOnline);

    public int TotalCount => _clients.Count;

    public void PruneStaleClientStates(TimeSpan maxAge, DateTime now)
    {
        foreach (var (clientId, state) in _clients)
        {
            if (!state.IsOnline && state.LastHeartbeat != default && now - state.LastHeartbeat > maxAge)
                _clients.TryRemove(clientId, out _);
        }
    }
}
