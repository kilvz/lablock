using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LabLock.Server.Data;
using LabLock.Server.Models;
using LabLock.Server.Services;
using LabLock.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LabLock.Server.Mcp;

public partial class McpServer : IDisposable
{
    private readonly IServiceProvider _services;
    private readonly TextReader _stdin;
    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;
    private readonly McpApiClient _api = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public McpServer(IServiceProvider services, TextReader? stdin = null, TextWriter? stdout = null, TextWriter? stderr = null)
    {
        _services = services;
        _stdin = stdin ?? Console.In;
        _stdout = stdout ?? Console.Out;
        _stderr = stderr ?? Console.Error;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        string? line;
        while ((line = await _stdin.ReadLineAsync(ct)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var response = await ProcessRequestAsync(line);
            if (response != null)
            {
                await _stdout.WriteLineAsync(response);
                await _stdout.FlushAsync();
            }
        }
    }

    public async Task<string?> ProcessRequestAsync(string json)
    {
        object? id = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var method = root.TryGetProperty("method", out var m) ? m.GetString() : null;

            if (method == "notifications/initialized" || method == "notifications/cancelled")
                return null;

            if (root.TryGetProperty("id", out var idEl))
            {
                id = idEl.ValueKind == JsonValueKind.String ? idEl.GetString() :
                     idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64() : null;
            }

            if (method == "initialize")
            {
                return BuildResponse(id, new
                {
                    protocolVersion = "2024-11-05",
                    capabilities = new { tools = new { }, resources = new { } },
                    serverInfo = new { name = "lablock-server-mcp", version = "1.0.0" }
                });
            }

            var @params = root.TryGetProperty("params", out var p) ? p : default;

            object result = await (method switch
            {
                "tools/list" => HandleListTools(),
                "tools/call" => HandleCallTool(@params),
                "resources/list" => HandleListResources(),
                "resources/read" => HandleReadResource(@params),
                _ => throw new McpException(-32601, $"Method not found: {method}")
            });

            return BuildResponse(id, result);
        }
        catch (McpException mex)
        {
            return BuildError(id, mex.Code, mex.Message);
        }
        catch (Exception ex)
        {
            return BuildError(id, -32603, ex.Message);
        }
    }

    private static string BuildResponse(object? idObj, object? result)
    {
        var resp = new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = idObj, ["result"] = result };
        return JsonSerializer.Serialize(resp, JsonOpts);
    }

    private static string BuildError(object? idObj, int code, string message)
    {
        var resp = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = idObj,
            ["error"] = new Dictionary<string, object> { ["code"] = code, ["message"] = message }
        };
        return JsonSerializer.Serialize(resp, JsonOpts);
    }

    private Task<object> HandleListTools() => Task.FromResult<object>(new
    {
        tools = new object[]
        {
            new
            {
                name = "lablock_list_clients",
                description = "List all registered LabLock clients with online status, CPU, memory, OS, and current user",
                inputSchema = new { type = "object", properties = new { }, required = Array.Empty<string>() }
            },
            new
            {
                name = "lablock_get_client",
                description = "Get detailed information about a specific LabLock client",
                inputSchema = new { type = "object", properties = new { clientId = new { type = "string", description = "Client ID" } }, required = new[] { "clientId" } }
            },
            new
            {
                name = "lablock_execute",
                description = "Execute a PowerShell command on the LabLock SERVER (not a client). Returns stdout/stderr and exit code.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        command = new { type = "string", description = "PowerShell command to execute" },
                        timeoutSeconds = new { type = "number", description = "Timeout in seconds (default 60)" }
                    },
                    required = new[] { "command" }
                }
            },
            new
            {
                name = "lablock_send_command",
                description = "Send a command to a specific LabLock client for remote execution. Runs as NT AUTHORITY\\SYSTEM in session 0 (INVISIBLE to user — the user will NOT see any windows, message boxes, or UI). Use ONLY for headless system tasks: file ops, services, registry, background processes. For user-visible operations use lablock_run_user_powershell instead.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        clientId = new { type = "string", description = "Target client ID" },
                        command = new { type = "string", description = "Command to execute on the client" },
                        timeoutSeconds = new { type = "number", description = "Timeout in seconds (default 60)" }
                    },
                    required = new[] { "clientId", "command" }
                }
            },
            new
            {
                name = "lablock_get_system_info",
                description = "Get real-time system info from a specific client (CPU, memory, disk, processes, OS)",
                inputSchema = new { type = "object", properties = new { clientId = new { type = "string", description = "Client ID" } }, required = new[] { "clientId" } }
            },
            new
            {
                name = "lablock_get_activity_logs",
                description = "Query activity logs (keystrokes, window focus, processes, USB, clipboard events) with filtering",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        clientId = new { type = "string", description = "Filter by client ID (default: all)" },
                        eventType = new { type = "string", description = "Filter by event type: Keystroke, ProcessStart, ProcessStop, WindowFocus, Login, Logoff, Idle, UsbInsert, UsbRemove, Clipboard" },
                        search = new { type = "string", description = "Search in log details" },
                        from = new { type = "string", description = "Start time (ISO 8601)" },
                        to = new { type = "string", description = "End time (ISO 8601)" },
                        page = new { type = "number", description = "Page number (default 1)" },
                        pageSize = new { type = "number", description = "Page size (default 50, max 500)" }
                    },
                    required = Array.Empty<string>()
                }
            },
            new
            {
                name = "lablock_get_log_stats",
                description = "Get activity log statistics (total logs, event counts, active clients) for a time range",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        clientId = new { type = "string", description = "Filter by client ID (default: all)" },
                        hoursBack = new { type = "number", description = "Hours to look back (default 24)" }
                    },
                    required = Array.Empty<string>()
                }
            },
            new
            {
                name = "lablock_get_command_history",
                description = "View command execution history for a client or all clients",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        clientId = new { type = "string", description = "Filter by client ID (default: all)" },
                        limit = new { type = "number", description = "Max records (default 50)" }
                    },
                    required = Array.Empty<string>()
                }
            },
            new
            {
                name = "lablock_delete_client",
                description = "Deactivate and disconnect a LabLock client",
                inputSchema = new { type = "object", properties = new { clientId = new { type = "string", description = "Client ID to delete" } }, required = new[] { "clientId" } }
            },
            new
            {
                name = "lablock_get_update_status",
                description = "Get the published agent update package status (version, size, sha256, uploadedAt)",
                inputSchema = new { type = "object", properties = new { }, required = Array.Empty<string>() }
            },
            new
            {
                name = "lablock_push_update",
                description = "Push the published update to a specific client. Agent will download and restart.",
                inputSchema = new { type = "object", properties = new { clientId = new { type = "string", description = "Target client ID" } }, required = new[] { "clientId" } }
            },
            new
            {
                name = "lablock_screenshot",
                description = "Take a screenshot of a client's screen (captures the USER'S interactive desktop, not session 0). Returns base64 JPEG image data.",
                inputSchema = new { type = "object", properties = new { clientId = new { type = "string", description = "Client ID" } }, required = new[] { "clientId" } }
            },
            new
            {
                name = "lablock_get_active_app",
                description = "Get the foreground window title and process name on a client (reads from the USER'S interactive session)",
                inputSchema = new { type = "object", properties = new { clientId = new { type = "string", description = "Client ID" } }, required = new[] { "clientId" } }
            },
            new
            {
                name = "lablock_message_box",
                description = "Show a message box on a client's screen (VISIBLE in the USER'S interactive session — the user sees it immediately). Buttons: OK, OKCancel, YesNo, YesNoCancel, AbortRetryIgnore, RetryCancel. Icon: Information, Warning, Error, Question.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        clientId = new { type = "string", description = "Client ID" },
                        title = new { type = "string", description = "Dialog title" },
                        message = new { type = "string", description = "Message text" },
                        buttons = new { type = "string", description = "Button set: OK, OKCancel, YesNo, YesNoCancel, AbortRetryIgnore, RetryCancel" },
                        icon = new { type = "string", description = "Icon: Information, Warning, Error, Question" }
                    },
                    required = new[] { "clientId", "message" }
                }
            },
            new
            {
                name = "lablock_interactive_message",
                description = "Show an interactive message box on a client's USER session. The user CAN see it, type a reply, and send it back. Visible UI element.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        clientId = new { type = "string", description = "Client ID" },
                        title = new { type = "string", description = "Dialog title" },
                        message = new { type = "string", description = "Message text" },
                        placeholder = new { type = "string", description = "Placeholder text in the reply field" },
                        allowEmpty = new { type = "boolean", description = "Allow empty replies (default false)" },
                        topMost = new { type = "boolean", description = "Keep dialog on top (default true)" },
                        timeoutMs = new { type = "number", description = "Timeout in milliseconds (default 60000)" }
                    },
                    required = new[] { "clientId", "message" }
                }
            },
            new
            {
                name = "lablock_block_screen",
                description = "Block or unblock a client's screen in the USER'S interactive session (shows a black full-screen overlay the user CAN see)",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        clientId = new { type = "string", description = "Client ID" },
                        enable = new { type = "boolean", description = "true=block, false=unblock" }
                    },
                    required = new[] { "clientId", "enable" }
                }
            },
            new
            {
                name = "lablock_kill_tasks",
                description = "Kill processes by name on a client (runs in the USER'S interactive session, can kill visible/interactive apps like browsers, games, etc.)",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        clientId = new { type = "string", description = "Client ID" },
                        processNames = new { type = "array", items = new { type = "string" }, description = "Process names to kill (e.g. ['notepad','chrome'])" }
                    },
                    required = new[] { "clientId", "processNames" }
                }
            },
            new
            {
                name = "lablock_send_keystrokes",
                description = "Send keystrokes to a client's USER interactive session. Keystrokes are injected into the user's visible desktop. Supports SendKeys format: 'ctrl+c', 'hello world', '{ENTER}', '^(c)' (ctrl+c).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        clientId = new { type = "string", description = "Client ID" },
                        keys = new { type = "string", description = "Keystrokes to send (SendKeys format)" }
                    },
                    required = new[] { "clientId", "keys" }
                }
            },
            new
            {
                name = "lablock_run_user_powershell",
                description = "Run a PowerShell command in the USER'S INTERACTIVE SESSION (session 1/2/3). The user CAN see windows, dialogs, and UI changes. Use for: showing messages, opening browsers, changing wallpaper, launching GUI apps, or any operation the user should see. Output is fully captured. Runs with user-level privileges (non-elevated).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        clientId = new { type = "string", description = "Client ID" },
                        command = new { type = "string", description = "PowerShell command to execute" },
                        timeoutSeconds = new { type = "number", description = "Timeout in seconds (default 60)" }
                    },
                    required = new[] { "clientId", "command" }
                }
            },
            new
            {
                name = "lablock_run_elevated_powershell",
                description = "Run an elevated PowerShell command on a client via UAC. User WILL see a UAC consent prompt. Runs with ADMIN privileges in the user's session. Output is NOT captured (UAC elevation prevents stdout redirection). Use for admin-only tasks: driver changes, system config, registry under HKLM. For user-visible non-admin tasks, use lablock_run_user_powershell instead.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        clientId = new { type = "string", description = "Client ID" },
                        command = new { type = "string", description = "PowerShell command to execute as admin" },
                        timeoutSeconds = new { type = "number", description = "Timeout in seconds (default 60)" }
                    },
                    required = new[] { "clientId", "command" }
                }
            }
        }
    });

    private Task<object> HandleListResources() => Task.FromResult<object>(new
    {
        resources = new object[]
        {
            new { uri = "lablock://clients", name = "All LabLock Clients", description = "List of all registered clients with online status" },
            new { uri = "lablock://stats", name = "LabLock Statistics", description = "Overall system statistics (client count, log counts, etc.)" }
        }
    });

    private async Task<object> HandleCallTool(JsonElement @params)
    {
        var name = @params.GetProperty("name").GetString()!;
        var args = @params.TryGetProperty("arguments", out var a) ? a : default;

        return name switch
        {
            "lablock_list_clients" => await ListClients(),
            "lablock_get_client" => await GetClient(GetString(args, "clientId")),
            "lablock_execute" => await ExecuteServerCommand(GetString(args, "command"), GetInt(args, "timeoutSeconds") ?? 60),
            "lablock_send_command" => await SendClientCommand(GetString(args, "clientId"), GetString(args, "command"), GetInt(args, "timeoutSeconds") ?? 60),
            "lablock_get_system_info" => await GetSystemInfo(GetString(args, "clientId")),
            "lablock_get_activity_logs" => await GetActivityLogs(args),
            "lablock_get_log_stats" => await GetLogStats(GetString(args, "clientId"), GetInt(args, "hoursBack") ?? 24),
            "lablock_get_command_history" => await GetCommandHistory(GetString(args, "clientId"), GetInt(args, "limit") ?? 50),
            "lablock_delete_client" => await DeleteClient(GetString(args, "clientId")),
            "lablock_get_update_status" => await GetUpdateStatus(),
            "lablock_push_update" => await PushUpdate(GetString(args, "clientId")),
            "lablock_screenshot" => await SendInteractive(GetString(args, "clientId"), "screenshot", new Dictionary<string, object?>()),
            "lablock_get_active_app" => await SendInteractive(GetString(args, "clientId"), "get_active_app", new Dictionary<string, object?>()),
            "lablock_message_box" => await InteractiveFromArgs(GetString(args, "clientId"), "message_box", args),
            "lablock_interactive_message" => await InteractiveFromArgs(GetString(args, "clientId"), "interactive_message", args, GetInt(args, "timeoutMs") ?? 60000),
            "lablock_block_screen" => await InteractiveFromArgs(GetString(args, "clientId"), "block_screen", args),
            "lablock_kill_tasks" => await InteractiveFromArgs(GetString(args, "clientId"), "kill_tasks", args),
            "lablock_send_keystrokes" => await InteractiveFromArgs(GetString(args, "clientId"), "send_keystrokes", args),
            "lablock_run_user_powershell" => await SendInteractive(GetString(args, "clientId"), "run_powershell", BuildUserPsParams(args)),
            "lablock_run_elevated_powershell" => await SendInteractive(GetString(args, "clientId"), "run_powershell", BuildElevatedPsParams(args)),
            _ => throw new McpException(-32601, $"Unknown tool: {name}")
        };
    }

    private async Task<object> HandleReadResource(JsonElement @params)
    {
        var uri = @params.GetProperty("uri").GetString()!;

        if (uri == "lablock://clients")
            return new { contents = new[] { new { uri, mimeType = "text/plain", text = await FormatClients() } } };

        if (uri == "lablock://stats")
            return new { contents = new[] { new { uri, mimeType = "text/plain", text = await FormatStats() } } };

        var match = ClientResourceRegex().Match(uri);
        if (match.Success)
        {
            var client = await GetDbClientAsync(match.Groups[1].Value);
            return new { contents = new[] { new { uri, mimeType = "text/plain", text = FormatClient(client) } } };
        }

        throw new McpException(-32602, $"Unknown resource: {uri}");
    }

    private async Task<object> ListClients()
    {
        var all = await GetDbClientsAsync();
        var text = FormatClientList(all, all.Count(c => c.IsOnline), all.Count);
        return McpText(text);
    }

    private async Task<object> GetClient(string? clientId)
    {
        if (string.IsNullOrEmpty(clientId)) throw new McpException(-32602, "clientId is required");
        var client = await GetDbClientAsync(clientId);
        if (client == null) return McpText($"Client {clientId} not found.");
        return McpText(FormatClient(client));
    }

    private async Task<object> ExecuteServerCommand(string? command, int timeoutSec)
    {
        if (string.IsNullOrEmpty(command)) throw new McpException(-32602, "command is required");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
            var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe", $"-NoProfile -Command \"{command.Replace("\"", "\\\"")}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return McpText("ERROR: Failed to start process");
            var stdout = await proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = await proc.StandardError.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            var output = string.IsNullOrEmpty(stderr) ? stdout : $"{stdout}\nSTDERR:\n{stderr}";
            return new { content = new object[] { new { type = "text", text = output }, new { type = "text", text = $"Exit Code: {proc.ExitCode}" } } };
        }
        catch (OperationCanceledException)
        {
            return McpText("ERROR: Command timed out");
        }
        catch (Exception ex)
        {
            return McpText($"ERROR: {ex.Message}");
        }
    }

    private async Task<object> SendClientCommand(string? clientId, string? command, int timeoutSec)
    {
        if (string.IsNullOrEmpty(clientId)) throw new McpException(-32602, "clientId is required");
        if (string.IsNullOrEmpty(command)) throw new McpException(-32602, "command is required");
        try
        {
            var output = await _api.SendCommandAsync(clientId, command, timeoutSec);
            return McpText(output ?? "(no output)");
        }
        catch (Exception ex)
        {
            return McpText($"ERROR: {ex.Message}");
        }
    }

    private async Task<object> GetSystemInfo(string? clientId)
    {
        if (string.IsNullOrEmpty(clientId)) throw new McpException(-32602, "clientId is required");
        try
        {
            var json = await _api.GetSystemInfoAsync(clientId);
            return McpText(json);
        }
        catch (Exception ex)
        {
            return McpText($"ERROR: {ex.Message}");
        }
    }

    private async Task<object> GetActivityLogs(JsonElement args)
    {
        var logSvc = GetLogService();
        var clientId = GetString(args, "clientId");
        var eventTypeStr = GetString(args, "eventType");
        EventType? evt = null;
        if (!string.IsNullOrEmpty(eventTypeStr) && Enum.TryParse<EventType>(eventTypeStr, true, out var parsed))
            evt = parsed;
        var keyword = GetString(args, "search");
        var from = TryParseDateTime(GetString(args, "from"));
        var to = TryParseDateTime(GetString(args, "to"));
        var page = GetInt(args, "page") ?? 1;
        var pageSize = Math.Min(GetInt(args, "pageSize") ?? 50, 500);

        var (logs, total) = await logSvc.QueryAsync(clientId, evt, from, to, keyword, page, pageSize);
        var text = FormatLogList(logs, total, page, pageSize);
        return McpText(text);
    }

    private async Task<object> GetLogStats(string? clientId, int hoursBack)
    {
        var logSvc = GetLogService();
        var from = DateTime.UtcNow.AddHours(-hoursBack);
        var stats = await logSvc.GetStatsAsync(clientId, from);
        var text = JsonSerializer.Serialize(stats, JsonOpts);
        return McpText(text);
    }

    private async Task<object> GetCommandHistory(string? clientId, int limit)
    {
        var cmdSvc = GetCommandService();
        var history = await cmdSvc.GetHistoryAsync(clientId, limit);
        var text = FormatCommandHistory(history);
        return McpText(text);
    }

    private async Task<object> DeleteClient(string? clientId)
    {
        if (string.IsNullOrEmpty(clientId)) throw new McpException(-32602, "clientId is required");
        try
        {
            await _api.DeleteClientAsync(clientId);
            return McpText($"Client {clientId} deactivated and disconnected.");
        }
        catch (Exception ex)
        {
            return McpText($"ERROR: {ex.Message}");
        }
    }

    private async Task<object> GetUpdateStatus()
    {
        try
        {
            var json = await _api.GetUpdateStatusAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var lines = new List<string>
            {
                "Published Agent Update:",
                $"  Version: {GetProp(root, "version") ?? "-"}",
                $"  Size: {GetProp(root, "size") ?? "-"} bytes",
                $"  SHA256: {GetProp(root, "sha256") ?? "-"}",
                $"  Uploaded: {GetProp(root, "uploadedAt") ?? "-"}"
            };
            return McpText(string.Join("\n", lines));
        }
        catch (Exception ex)
        {
            return McpText($"ERROR: {ex.Message}");
        }
    }

    private async Task<object> SendInteractive(string? clientId, string action, Dictionary<string, object?> parameters, int timeoutMs = 30000)
    {
        if (string.IsNullOrEmpty(clientId)) throw new McpException(-32602, "clientId is required");

        try
        {
            var output = await _api.SendInteractiveAsync(clientId, action, parameters, timeoutMs);
            return McpText(output);
        }
        catch (Exception ex)
        {
            return McpText($"ERROR: {ex.Message}");
        }
    }

    private async Task<object> InteractiveFromArgs(string? clientId, string action, JsonElement args, int timeoutMs = 30000)
    {
        if (string.IsNullOrEmpty(clientId)) throw new McpException(-32602, "clientId is required");

        var parameters = new Dictionary<string, object?>();
        if (args.ValueKind == JsonValueKind.Object)
        {
            foreach (var kvp in args.EnumerateObject())
            {
                if (kvp.Name == "clientId") continue;
                parameters[kvp.Name] = CloneJsonElement(kvp.Value);
            }
        }

        return await SendInteractive(clientId, action, parameters, timeoutMs);
    }

    private static object CloneJsonElement(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString()!,
        JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null!,
        JsonValueKind.Array => el.EnumerateArray().Select(CloneJsonElement).ToList(),
        JsonValueKind.Object => el.EnumerateObject().ToDictionary(k => k.Name, k => CloneJsonElement(k.Value)),
        _ => el.ToString()
    };

    private static string? StaticGetStr(JsonElement el, string key) =>
        el.ValueKind == JsonValueKind.Undefined ? null :
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? StaticGetInt(JsonElement el, string key) =>
        el.ValueKind == JsonValueKind.Undefined ? null :
        el.TryGetProperty(key, out var v) && (v.ValueKind == JsonValueKind.Number || v.ValueKind == JsonValueKind.String) ?
            v.TryGetInt32(out var i) ? i : null : null;

    private static Dictionary<string, object?> BuildUserPsParams(JsonElement args)
    {
        return new Dictionary<string, object?>
        {
            ["command"] = StaticGetStr(args, "command") ?? "",
            ["elevated"] = false,
            ["timeoutSeconds"] = StaticGetInt(args, "timeoutSeconds") ?? 60
        };
    }

    private static Dictionary<string, object?> BuildElevatedPsParams(JsonElement args)
    {
        return new Dictionary<string, object?>
        {
            ["command"] = StaticGetStr(args, "command") ?? "",
            ["elevated"] = true,
            ["timeoutSeconds"] = StaticGetInt(args, "timeoutSeconds") ?? 60
        };
    }

    private async Task<object> PushUpdate(string? clientId)
    {
        if (string.IsNullOrEmpty(clientId)) throw new McpException(-32602, "clientId is required");
        try
        {
            var json = await _api.PushUpdateAsync(clientId);
            using var doc = JsonDocument.Parse(json);
            var version = doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : "?";
            return McpText($"Update pushed to {clientId} (version {version}). Agent will download and restart.");
        }
        catch (Exception ex)
        {
            return McpText($"ERROR: {ex.Message}");
        }
    }

    private static string? GetProp(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : null;

    private async Task<string> FormatClients()
    {
        var all = await GetDbClientsAsync();
        return FormatClientList(all, all.Count(c => c.IsOnline), all.Count);
    }

    private async Task<string> FormatStats()
    {
        var all = await GetDbClientsAsync();
        var logSvc = GetLogService();
        var logStats = await logSvc.GetStatsAsync(null, DateTime.UtcNow.AddHours(-24));
        var lines = new List<string>
        {
            $"Connected Clients: {all.Count}",
            $"Online Clients: {all.Count(c => c.IsOnline)}",
            $"Activity Logs (24h): {logStats.GetValueOrDefault("totalLogs", "N/A")}",
            $"Active Clients (24h): {logStats.GetValueOrDefault("activeClients", "N/A")}",
            "Event Breakdown:"
        };
        if (logStats.TryGetValue("eventCounts", out var evtCounts) && evtCounts is Dictionary<string, object> ec)
            lines.AddRange(ec.Select(kv => $"  {kv.Key}: {kv.Value}"));
        return string.Join("\n", lines);
    }

    private static string FormatClientList(List<ClientStateService.ClientState> clients, int online, int total)
    {
        if (clients.Count == 0) return "No clients registered.";
        var lines = new List<string> { $"Total: {total} | Online: {online}\n" };
        foreach (var c in clients)
        {
            lines.Add($"[{GetStatus(c)}] {c.ClientId}");
            lines.Add($"  Hostname: {c.Hostname ?? "-"}");
            lines.Add($"  User: {c.CurrentUser ?? "-"}");
            if (!string.IsNullOrEmpty(c.InteractiveUser))
                lines.Add($"  Interactive: {c.InteractiveUser} (session {c.InteractiveSessionId})");
            if (c.AgentSessionId != 0)
                lines.Add($"  Agent session: {c.AgentSessionId}");
            lines.Add($"  OS: {c.OsVersion ?? "-"}");
            if (!string.IsNullOrEmpty(c.AgentVersion))
                lines.Add($"  Agent: v{c.AgentVersion}");
            lines.Add($"  IP: {c.IpAddress ?? "-"}");
            lines.Add($"  CPU: {c.CpuPercent:F1}%");
            lines.Add($"  Memory: {c.MemoryPercent:F1}%");
            lines.Add($"  Active: {c.ActiveProcess ?? "-"}");
            lines.Add($"  Last Seen: {c.LastHeartbeat:yyyy-MM-dd HH:mm:ss}\n");
        }
        return string.Join("\n", lines);
    }

    private static string FormatClient(ClientStateService.ClientState? c)
    {
        if (c == null) return "Client not found.";
        return string.Join("\n",
            $"Client: {c.ClientId}",
            $"Status: {GetStatus(c)}",
            $"Hostname: {c.Hostname ?? "-"}",
            $"Current User: {c.CurrentUser ?? "-"}",
            $"Interactive: {c.InteractiveUser ?? "-"} (session {c.InteractiveSessionId})",
            $"Agent session: {c.AgentSessionId}",
            $"OS: {c.OsVersion ?? "-"}",
            $"Agent: v{c.AgentVersion}",
            $"IP: {c.IpAddress ?? "-"}",
            $"CPU: {c.CpuPercent:F1}%",
            $"Memory: {c.MemoryPercent:F1}%",
            $"Active Process: {c.ActiveProcess ?? "-"}",
            $"First Seen: {c.FirstSeen:yyyy-MM-dd HH:mm:ss}",
            $"Last Seen: {c.LastHeartbeat:yyyy-MM-dd HH:mm:ss}");
    }

    private static string FormatLogList(List<ActivityLog> logs, int total, int page, int pageSize)
    {
        if (logs.Count == 0) return "No logs found.";
        var totalPages = total > 0 ? (int)Math.Ceiling((double)total / pageSize) : 0;
        var lines = new List<string> { $"Total: {total} | Page: {page}/{totalPages}\n" };
        foreach (var log in logs)
        {
            lines.Add($"[{log.Timestamp:yyyy-MM-dd HH:mm:ss}] {log.EventType} | {log.ClientId}" +
                (string.IsNullOrEmpty(log.ProcessName) ? "" : $" | {log.ProcessName}") +
                (string.IsNullOrEmpty(log.WindowTitle) ? "" : $" | \"{log.WindowTitle}\""));
            if (!string.IsNullOrEmpty(log.Details))
                lines.Add($"  {log.Details}");
        }
        return string.Join("\n", lines);
    }

    private static string FormatCommandHistory(List<CommandHistory> history)
    {
        if (history.Count == 0) return "No command history.";
        var lines = new List<string> { $"Total: {history.Count}\n" };
        foreach (var cmd in history)
        {
            lines.Add($"[{cmd.SentAt:yyyy-MM-dd HH:mm:ss}] {cmd.ClientId} | {cmd.Status}");
            lines.Add($"  Command: {cmd.Command}");
            if (!string.IsNullOrEmpty(cmd.RunAsUser) || cmd.InteractiveSessionId != 0)
                lines.Add($"  Context: {cmd.RunAsUser}@session{cmd.SessionId} | interactive session{cmd.InteractiveSessionId} ({cmd.InteractiveUser}) | {cmd.DurationMs} ms");
            if (!string.IsNullOrEmpty(cmd.Output))
                lines.Add($"  Output: {cmd.Output[..Math.Min(cmd.Output.Length, 500)]}");
            if (cmd.ExitCode != 0 || !string.IsNullOrEmpty(cmd.Output))
                lines.Add($"  Exit Code: {cmd.ExitCode}");
            lines.Add("");
        }
        return string.Join("\n", lines);
    }

    private static string GetStatus(ClientStateService.ClientState c) => c.IsOnline ? "ONLINE" : "OFFLINE";

    private static object McpText(string text) => new { content = new[] { new { type = "text", text } } };

    private static readonly TimeSpan OnlineThreshold = ClientStateService.OnlineThreshold;

    private async Task<List<ClientStateService.ClientState>> GetDbClientsAsync()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.Clients.Where(c => c.IsActive).OrderBy(c => c.Hostname).ToListAsync();
        var now = DateTime.UtcNow;
        return rows.Select(c => new ClientStateService.ClientState
        {
            ClientId = c.ClientId,
            Hostname = c.Hostname,
            IpAddress = c.IpAddress,
            OsVersion = c.OsVersion,
            AgentVersion = c.AgentVersion,
            CurrentUser = c.CurrentUser,
            InteractiveUser = c.InteractiveUser ?? "",
            InteractiveSessionId = c.InteractiveSessionId ?? 0,
            AgentSessionId = c.AgentSessionId ?? 0,
            ActiveProcess = c.ActiveProcess,
            CpuPercent = c.CpuPercent ?? 0,
            MemoryPercent = c.MemoryPercent ?? 0,
            IsOnline = now - c.LastSeen <= OnlineThreshold,
            LastHeartbeat = c.LastSeen,
            FirstSeen = c.FirstSeen
        }).ToList();
    }

    private async Task<ClientStateService.ClientState?> GetDbClientAsync(string clientId)
    {
        var all = await GetDbClientsAsync();
        return all.FirstOrDefault(c => c.ClientId == clientId);
    }

    private ClientStateService GetClientService() => _services.GetRequiredService<ClientStateService>();
    private CommandService GetCommandService() => _services.GetRequiredService<CommandService>();
    private LogStorageService GetLogService() => _services.GetRequiredService<LogStorageService>();

    private static string? GetString(JsonElement el, string key) =>
        el.ValueKind == JsonValueKind.Undefined ? null :
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? GetInt(JsonElement el, string key) =>
        el.ValueKind == JsonValueKind.Undefined ? null :
        el.TryGetProperty(key, out var v) && (v.ValueKind == JsonValueKind.Number || v.ValueKind == JsonValueKind.String) ?
            v.TryGetInt32(out var i) ? i : null : null;

    private static DateTime? TryParseDateTime(string? s) =>
        !string.IsNullOrEmpty(s) && DateTime.TryParse(s, out var dt) ? dt : null;

    public void Dispose() { }

    private class McpException(int code, string message) : Exception(message)
    {
        public int Code => code;
    }

    [GeneratedRegex(@"^lablock://clients/([^/]+)$")]
    private static partial Regex ClientResourceRegex();
}
