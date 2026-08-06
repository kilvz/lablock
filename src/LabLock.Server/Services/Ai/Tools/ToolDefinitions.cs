namespace LabLock.Server.Services.Ai.Tools;

public static class ToolDefinitions
{
    public static List<AiToolDefinition> GetAll()
    {
        return new List<AiToolDefinition>
        {
            new()
            {
                Name = "execute_command",
                Description = "Execute a PowerShell command on a specific lab PC. Returns the command output (stdout and stderr).",
                Parameters = new Dictionary<string, AiToolParameter>
                {
                    ["client_id"] = new() { Type = "string", Description = "The ID of the target PC (e.g., PC-LAB-001). Use list_clients first if unsure." },
                    ["command"] = new() { Type = "string", Description = "The PowerShell command to execute on the target PC." },
                    ["timeout_seconds"] = new() { Type = "integer", Description = "Command timeout in seconds. Default 60. Increase for long-running commands." }
                },
                Required = new List<string> { "client_id", "command" }
            },
            new()
            {
                Name = "execute_command_all",
                Description = "Execute a PowerShell command on ALL online lab PCs simultaneously. Returns aggregated results from each PC. Use when the user says 'all PCs' or 'every PC'.",
                Parameters = new Dictionary<string, AiToolParameter>
                {
                    ["command"] = new() { Type = "string", Description = "The PowerShell command to execute on all online PCs." },
                    ["timeout_seconds"] = new() { Type = "integer", Description = "Command timeout per PC in seconds. Default 60." }
                },
                Required = new List<string> { "command" }
            },
            new()
            {
                Name = "list_clients",
                Description = "List all registered lab PCs with their current status (online/offline), current user, active application, CPU and memory usage. Use this to find specific PCs before executing commands.",
                Parameters = new Dictionary<string, AiToolParameter>
                {
                    ["status_filter"] = new()
                    {
                        Type = "string",
                        Description = "Filter by status. 'idle' means CPU < 5%. Default: 'all'.",
                        EnumValues = new[] { "all", "online", "offline", "idle" }
                    }
                },
                Required = new List<string>()
            },
            new()
            {
                Name = "get_system_info",
                Description = "Get detailed system information for a specific PC including OS version, CPU, memory, disk space, installed software, and running processes.",
                Parameters = new Dictionary<string, AiToolParameter>
                {
                    ["client_id"] = new() { Type = "string", Description = "The ID of the target PC." }
                },
                Required = new List<string> { "client_id" }
            },
            new()
            {
                Name = "get_activity_logs",
                Description = "Retrieve activity logs for a PC. Includes keystrokes typed, processes started/stopped, and window focus changes.",
                Parameters = new Dictionary<string, AiToolParameter>
                {
                    ["client_id"] = new() { Type = "string", Description = "The ID of the target PC." },
                    ["event_type"] = new()
                    {
                        Type = "string",
                        Description = "Filter by event type. Default: 'all'.",
                        EnumValues = new[] { "all", "keystroke", "process_start", "process_stop", "window_focus", "login", "logoff" }
                    },
                    ["hours_back"] = new() { Type = "integer", Description = "How many hours back to search. Default: 1." },
                    ["limit"] = new() { Type = "integer", Description = "Maximum number of results to return. Default: 50." }
                },
                Required = new List<string> { "client_id" }
            },
            new()
            {
                Name = "get_command_history",
                Description = "Get the history of previously executed commands on a specific PC, including command text, output, status, and who sent it.",
                Parameters = new Dictionary<string, AiToolParameter>
                {
                    ["client_id"] = new() { Type = "string", Description = "The ID of the target PC." },
                    ["limit"] = new() { Type = "integer", Description = "Maximum number of results. Default: 20." }
                },
                Required = new List<string> { "client_id" }
            }
        };
    }
}
