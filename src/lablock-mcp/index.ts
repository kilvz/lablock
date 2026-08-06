#!/usr/bin/env node
import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import {
  CallToolRequestSchema,
  ListToolsRequestSchema,
  ListResourcesRequestSchema,
  ReadResourceRequestSchema,
} from "@modelcontextprotocol/sdk/types.js";

const SERVER_URL = process.env.LABLOCK_SERVER_URL || "http://localhost:5000";
const USERNAME = process.env.LABLOCK_USERNAME || "admin";
const PASSWORD = process.env.LABLOCK_PASSWORD || "admin";

let authToken = "";

async function login(): Promise<string> {
  const res = await fetch(`${SERVER_URL}/api/auth/login`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ username: USERNAME, password: PASSWORD }),
  });
  if (!res.ok) throw new Error(`Login failed: ${res.status} ${await res.text()}`);
  const data: any = await res.json();
  authToken = data.token;
  return authToken;
}

async function apiFetch(path: string, options?: RequestInit): Promise<any> {
  if (!authToken) await login();
  const res = await fetch(`${SERVER_URL}${path}`, {
    ...options,
    headers: {
      "Content-Type": "application/json",
      Authorization: `Bearer ${authToken}`,
      ...(options?.headers || {}),
    },
  });
  if (res.status === 401) {
    await login();
    const retry = await fetch(`${SERVER_URL}${path}`, {
      ...options,
      headers: {
        "Content-Type": "application/json",
        Authorization: `Bearer ${authToken}`,
        ...(options?.headers || {}),
      },
    });
    if (!retry.ok) throw new Error(`${path} failed: ${retry.status} ${await retry.text()}`);
    return retry.json();
  }
  if (!res.ok) {
    const text = await res.text();
    throw new Error(`${path} failed: ${res.status} ${text}`);
  }
  if (res.status === 204) return null;
  return res.json();
}

const server = new Server(
  { name: "lablock-mcp", version: "1.0.0" },
  { capabilities: { tools: {}, resources: {} } }
);

server.setRequestHandler(ListToolsRequestSchema, async () => ({
  tools: [
    {
      name: "lablock_list_clients",
      description: "List all registered LabLock clients with online status, CPU, memory, OS, and current user",
      inputSchema: { type: "object", properties: {}, required: [] },
    },
    {
      name: "lablock_get_client",
      description: "Get detailed information about a specific LabLock client",
      inputSchema: {
        type: "object",
        properties: { clientId: { type: "string", description: "Client ID" } },
        required: ["clientId"],
      },
    },
    {
      name: "lablock_execute",
      description: "Execute a PowerShell command on the LabLock SERVER (not a client). Returns stdout/stderr and exit code. Use for server-side operations.",
      inputSchema: {
        type: "object",
        properties: {
          command: { type: "string", description: "PowerShell command to execute" },
          timeoutSeconds: { type: "number", description: "Timeout in seconds (default 60)", default: 60 },
        },
        required: ["command"],
      },
    },
    {
      name: "lablock_send_command",
      description: "Send a command to a specific LabLock client for remote execution. Runs as NT AUTHORITY\\SYSTEM in session 0 (INVISIBLE to user — they will NOT see any windows, UI, or desktop changes). Use ONLY for headless system tasks: file ops, services, registry, background processes. For user-visible operations use lablock_run_user_powershell instead.",
      inputSchema: {
        type: "object",
        properties: {
          clientId: { type: "string", description: "Target client ID" },
          command: { type: "string", description: "Command to execute on the client (PowerShell)" },
          timeoutSeconds: { type: "number", description: "Timeout in seconds (default 60)", default: 60 },
        },
        required: ["clientId", "command"],
      },
    },
    {
      name: "lablock_get_system_info",
      description: "Get real-time system info from a specific client (CPU, memory, disk, processes, OS)",
      inputSchema: {
        type: "object",
        properties: { clientId: { type: "string", description: "Client ID" } },
        required: ["clientId"],
      },
    },
    {
      name: "lablock_get_activity_logs",
      description: "Query activity logs (keystrokes, window focus, processes, USB, clipboard events) with filtering",
      inputSchema: {
        type: "object",
        properties: {
          clientId: { type: "string", description: "Filter by client ID (default: all)" },
          eventType: { type: "string", description: "Filter by event type: Keystroke, ProcessStart, ProcessStop, WindowFocus, Login, Logoff, Idle, UsbInsert, UsbRemove, Clipboard" },
          search: { type: "string", description: "Search in log details" },
          from: { type: "string", description: "Start time (ISO 8601)" },
          to: { type: "string", description: "End time (ISO 8601)" },
          page: { type: "number", description: "Page number (default 1)" },
          pageSize: { type: "number", description: "Page size (default 50, max 500)" },
        },
        required: [],
      },
    },
    {
      name: "lablock_get_log_stats",
      description: "Get activity log statistics (total logs, event counts, active clients) for a time range",
      inputSchema: {
        type: "object",
        properties: {
          clientId: { type: "string", description: "Filter by client ID (default: all)" },
          hoursBack: { type: "number", description: "Hours to look back (default 24)" },
        },
        required: [],
      },
    },
    {
      name: "lablock_get_command_history",
      description: "View command execution history for a client or all clients",
      inputSchema: {
        type: "object",
        properties: {
          clientId: { type: "string", description: "Filter by client ID (default: all)" },
          limit: { type: "number", description: "Max records (default 50)" },
        },
        required: [],
      },
    },
    {
      name: "lablock_delete_client",
      description: "Deactivate and disconnect a LabLock client",
      inputSchema: {
        type: "object",
        properties: { clientId: { type: "string", description: "Client ID to delete" } },
        required: ["clientId"],
      },
    },
    {
      name: "lablock_get_update_status",
      description: "Check the latest published client agent update (version, size, sha256, uploaded time) on the server",
      inputSchema: { type: "object", properties: {}, required: [] },
    },
    {
      name: "lablock_push_update",
      description: "Push the latest published agent update to a specific online client. The client downloads it, verifies the checksum, and restarts itself with the new version.",
      inputSchema: {
        type: "object",
        properties: { clientId: { type: "string", description: "Target client ID (must be online)" } },
        required: ["clientId"],
      },
    },
    {
      name: "lablock_screenshot",
      description: "Take a screenshot of a client's screen (captures the USER'S interactive desktop, not session 0). Returns base64 JPEG image data.",
      inputSchema: {
        type: "object",
        properties: { clientId: { type: "string", description: "Client ID" } },
        required: ["clientId"],
      },
    },
    {
      name: "lablock_get_active_app",
      description: "Get the foreground window title and process name on a client (reads from the USER'S interactive session)",
      inputSchema: {
        type: "object",
        properties: { clientId: { type: "string", description: "Client ID" } },
        required: ["clientId"],
      },
    },
    {
      name: "lablock_message_box",
      description: "Show a message box on a client's screen (VISIBLE in the USER'S interactive session — the user sees it immediately). Buttons: OK, OKCancel, YesNo, YesNoCancel, AbortRetryIgnore, RetryCancel. Icon: Information, Warning, Error, Question.",
      inputSchema: {
        type: "object",
        properties: {
          clientId: { type: "string", description: "Client ID" },
          title: { type: "string", description: "Dialog title" },
          message: { type: "string", description: "Message text" },
          buttons: { type: "string", description: "Button set: OK, OKCancel, YesNo, YesNoCancel, AbortRetryIgnore, RetryCancel" },
          icon: { type: "string", description: "Icon: Information, Warning, Error, Question" },
        },
        required: ["clientId", "message"],
      },
    },
    {
      name: "lablock_interactive_message",
      description: "Show an interactive message box on a client's USER session. The user CAN see it, type a reply, and send it back. Visible UI element.",
      inputSchema: {
        type: "object",
        properties: {
          clientId: { type: "string", description: "Client ID" },
          title: { type: "string", description: "Dialog title" },
          message: { type: "string", description: "Message text" },
          placeholder: { type: "string", description: "Placeholder text in the reply field" },
          allowEmpty: { type: "boolean", description: "Allow empty replies (default false)" },
          topMost: { type: "boolean", description: "Keep dialog on top (default true)" },
          timeoutMs: { type: "number", description: "Timeout in milliseconds (default 60000)" },
        },
        required: ["clientId", "message"],
      },
    },
    {
      name: "lablock_block_screen",
      description: "Block or unblock a client's screen in the USER'S interactive session (shows a black full-screen overlay the user CAN see)",
      inputSchema: {
        type: "object",
        properties: {
          clientId: { type: "string", description: "Client ID" },
          enable: { type: "boolean", description: "true=block, false=unblock" },
        },
        required: ["clientId", "enable"],
      },
    },
    {
      name: "lablock_kill_tasks",
      description: "Kill processes by name on a client (runs in the USER'S interactive session, can kill visible/interactive apps like browsers, games, etc.)",
      inputSchema: {
        type: "object",
        properties: {
          clientId: { type: "string", description: "Client ID" },
          processNames: { type: "array", items: { type: "string" }, description: "Process names to kill (e.g. ['notepad','chrome'])" },
        },
        required: ["clientId", "processNames"],
      },
    },
    {
      name: "lablock_send_keystrokes",
      description: "Send keystrokes to a client's USER interactive session. Keystrokes are injected into the user's visible desktop. Supports SendKeys format: 'ctrl+c', 'hello world', '{ENTER}', '^(c)' (ctrl+c).",
      inputSchema: {
        type: "object",
        properties: {
          clientId: { type: "string", description: "Client ID" },
          keys: { type: "string", description: "Keystrokes to send (SendKeys format)" },
        },
        required: ["clientId", "keys"],
      },
    },
    {
      name: "lablock_run_user_powershell",
      description: "Run a PowerShell command in the USER'S INTERACTIVE SESSION (session 1/2/3). The user CAN see windows, dialogs, and UI changes. Use for: showing messages, opening browsers, changing wallpaper, launching GUI apps, or any operation the user should see. Output is fully captured. Runs with user-level privileges (non-elevated).",
      inputSchema: {
        type: "object",
        properties: {
          clientId: { type: "string", description: "Client ID" },
          command: { type: "string", description: "PowerShell command to execute (user-visible)" },
          timeoutSeconds: { type: "number", description: "Timeout in seconds (default 60)" },
        },
        required: ["clientId", "command"],
      },
    },
    {
      name: "lablock_run_elevated_powershell",
      description: "Run an elevated PowerShell command on a client via UAC. User WILL see a UAC consent prompt. Runs with ADMIN privileges in the user's session. Output is NOT captured (UAC elevation prevents stdout redirection). Use for admin-only tasks: driver changes, system config, registry under HKLM. For user-visible non-admin tasks, use lablock_run_user_powershell instead.",
      inputSchema: {
        type: "object",
        properties: {
          clientId: { type: "string", description: "Client ID" },
          command: { type: "string", description: "PowerShell command to execute as admin" },
          timeoutSeconds: { type: "number", description: "Timeout in seconds (default 60)" },
        },
        required: ["clientId", "command"],
      },
    },
  ],
}));

function formatClients(data: any): string {
  const clients = data.clients || [];
  if (clients.length === 0) return "No clients registered.";
  let out = `Total: ${data.total} | Online: ${data.online}\n\n`;
  for (const c of clients) {
    out += `[${c.isOnline ? "ONLINE" : "OFFLINE"}] ${c.clientId}\n`;
    out += `  Hostname: ${c.hostname || "-"}\n`;
    out += `  User: ${c.currentUser || "-"}\n`;
    out += `  OS: ${c.osVersion || "-"}\n`;
    out += `  IP: ${c.ipAddresses || "-"}\n`;
    out += `  CPU: ${c.cpuName || "-"} (${c.cpuPercent ?? "-"}%)\n`;
    out += `  Memory: ${c.memoryPercent ?? "-"}%\n`;
    if (c.activeProcess) out += `  Active: ${c.activeProcess}\n`;
    out += `  Last Seen: ${c.lastSeen || "-"}\n\n`;
  }
  return out;
}

function formatClient(c: any): string {
  return [
    `Client: ${c.clientId}`,
    `Status: ${c.isOnline ? "ONLINE" : "OFFLINE"}`,
    `Hostname: ${c.hostname || "-"}`,
    `Current User: ${c.currentUser || "-"}`,
    `OS: ${c.osVersion || "-"}`,
    `IP: ${c.ipAddresses || "-"}`,
    `CPU: ${c.cpuName || "-"} (${c.cpuPercent ?? "-"}%)`,
    `Memory: ${c.memoryPercent ?? "-"}%`,
    `Total RAM: ${c.totalMemoryMb ?? "-"} MB`,
    `Active Process: ${c.activeProcess || "-"}`,
    `First Seen: ${c.firstSeen || "-"}`,
    `Last Seen: ${c.lastSeen || "-"}`,
  ].join("\n");
}

server.setRequestHandler(CallToolRequestSchema, async (req) => {
  const { name, arguments: args } = req.params;

  try {
    switch (name) {
      case "lablock_list_clients": {
        const data = await apiFetch("/api/clients");
        return { content: [{ type: "text", text: formatClients(data) }] };
      }

      case "lablock_get_client": {
        const { clientId } = args as { clientId: string };
        const data = await apiFetch(`/api/clients/${encodeURIComponent(clientId)}`);
        return { content: [{ type: "text", text: formatClient(data) }] };
      }

      case "lablock_execute": {
        const { command, timeoutSeconds } = args as { command: string; timeoutSeconds?: number };
        const data = await apiFetch("/api/commands/execute", {
          method: "POST",
          body: JSON.stringify({ command, timeoutSeconds: timeoutSeconds || 60 }),
        });
        return {
          content: [
            { type: "text", text: data.output || "(no output)" },
            ...(data.exitCode != null ? [{ type: "text", text: `Exit Code: ${data.exitCode}` }] : []),
          ],
        };
      }

      case "lablock_send_command": {
        const { clientId, command, timeoutSeconds } = args as {
          clientId: string;
          command: string;
          timeoutSeconds?: number;
        };
        const data = await apiFetch(`/api/clients/${encodeURIComponent(clientId)}/command/wait`, {
          method: "POST",
          body: JSON.stringify({ command, timeoutSeconds: timeoutSeconds || 60 }),
        });
        return { content: [{ type: "text", text: data.output || "(no output)" }] };
      }

      case "lablock_get_system_info": {
        const { clientId } = args as { clientId: string };
        const data = await apiFetch(`/api/clients/${encodeURIComponent(clientId)}/system-info`);
        return { content: [{ type: "text", text: JSON.stringify(data, null, 2) }] };
      }

      case "lablock_get_activity_logs": {
        const params = args as Record<string, any>;
        const qs = new URLSearchParams();
        if (params.clientId && params.clientId !== "") qs.set("clientId", params.clientId);
        if (params.eventType) qs.set("eventType", params.eventType);
        if (params.search) qs.set("search", params.search);
        if (params.from) qs.set("from", params.from);
        if (params.to) qs.set("to", params.to);
        if (params.page) qs.set("page", String(params.page));
        if (params.pageSize) qs.set("pageSize", String(params.pageSize));
        const data = await apiFetch(`/api/logs?${qs.toString()}`);
        const logs = data.logs || [];
        let out = `Total: ${data.total} | Page: ${data.page}/${Math.ceil(data.total / (data.pageSize || 50))}\n\n`;
        for (const log of logs) {
          out += `[${log.timestamp}] ${log.eventType} | ${log.clientId}`;
          if (log.processName) out += ` | ${log.processName}`;
          if (log.windowTitle) out += ` | "${log.windowTitle}"`;
          if (log.details) out += `\n  ${log.details}`;
          out += "\n";
        }
        return { content: [{ type: "text", text: out || "No logs found." }] };
      }

      case "lablock_get_log_stats": {
        const params = args as Record<string, any>;
        const qs = new URLSearchParams();
        if (params.clientId) qs.set("clientId", params.clientId);
        if (params.hoursBack) qs.set("hoursBack", String(params.hoursBack));
        const data = await apiFetch(`/api/logs/stats?${qs.toString()}`);
        return { content: [{ type: "text", text: JSON.stringify(data, null, 2) }] };
      }

      case "lablock_get_command_history": {
        const params = args as Record<string, any>;
        const qs = new URLSearchParams();
        if (params.clientId) qs.set("clientId", params.clientId);
        if (params.limit) qs.set("limit", String(params.limit));
        const data = await apiFetch(`/api/commands?${qs.toString()}`);
        const history = data.history || [];
        let out = `Total: ${data.total}\n\n`;
        for (const cmd of history) {
          out += `[${cmd.sentAt}] ${cmd.clientId} | ${cmd.status}\n`;
          out += `  Command: ${cmd.command}\n`;
          if (cmd.output) out += `  Output: ${cmd.output.substring(0, 500)}\n`;
          if (cmd.exitCode != null) out += `  Exit Code: ${cmd.exitCode}\n`;
          out += "\n";
        }
        return { content: [{ type: "text", text: out || "No command history." }] };
      }

      case "lablock_delete_client": {
        const { clientId } = args as { clientId: string };
        await apiFetch(`/api/clients/${encodeURIComponent(clientId)}`, { method: "DELETE" });
        return { content: [{ type: "text", text: `Client ${clientId} deactivated and disconnected.` }] };
      }

      case "lablock_get_update_status": {
        const data = await apiFetch("/api/update/version");
        return {
          content: [
            {
              type: "text",
              text: [
                `Published Agent Update:`,
                `  Version: ${data.version || "-"}`,
                `  Size: ${data.size ?? "-"} bytes`,
                `  SHA256: ${data.sha256 || "-"}`,
                `  Uploaded: ${data.uploadedAt || "-"}`,
              ].join("\n"),
            },
          ],
        };
      }

      case "lablock_push_update": {
        const { clientId } = args as { clientId: string };
        const data = await apiFetch(`/api/clients/${encodeURIComponent(clientId)}/update`, { method: "POST" });
        return {
          content: [{ type: "text", text: `Update pushed to ${clientId} (version ${data.version || "?"}). Agent will download and restart.` }],
        };
      }

      case "lablock_screenshot": {
        const { clientId } = args as { clientId: string };
        const data = await apiFetch(`/api/clients/${encodeURIComponent(clientId)}/interactive`, {
          method: "POST",
          body: JSON.stringify({ action: "screenshot", timeoutMs: 30000 }),
        });
        return { content: [{ type: "text", text: data.output || "(no output)" }] };
      }

      case "lablock_get_active_app": {
        const { clientId } = args as { clientId: string };
        const data = await apiFetch(`/api/clients/${encodeURIComponent(clientId)}/interactive`, {
          method: "POST",
          body: JSON.stringify({ action: "get_active_app", timeoutMs: 10000 }),
        });
        return { content: [{ type: "text", text: data.output || "(no output)" }] };
      }

      case "lablock_message_box": {
        const { clientId, title, message, buttons, icon } = args as Record<string, any>;
        const data = await apiFetch(`/api/clients/${encodeURIComponent(clientId)}/interactive`, {
          method: "POST",
          body: JSON.stringify({
            action: "message_box",
            parameters: JSON.stringify({ title: title || "LabLock", message, buttons: buttons || "OK", icon: icon || "Information" }),
            timeoutMs: 30000,
          }),
        });
        return { content: [{ type: "text", text: data.output || "(no output)" }] };
      }

      case "lablock_interactive_message": {
        const params = args as Record<string, any>;
        const data = await apiFetch(`/api/clients/${encodeURIComponent(params.clientId)}/interactive`, {
          method: "POST",
          body: JSON.stringify({
            action: "interactive_message",
            parameters: JSON.stringify({
              title: params.title || "LabLock",
              message: params.message,
              placeholder: params.placeholder || "Type your reply...",
              allowEmpty: params.allowEmpty || false,
              topMost: params.topMost !== false,
            }),
            timeoutMs: params.timeoutMs || 60000,
          }),
        });
        return { content: [{ type: "text", text: data.output || "(no output)" }] };
      }

      case "lablock_block_screen": {
        const { clientId, enable } = args as { clientId: string; enable: boolean };
        const data = await apiFetch(`/api/clients/${encodeURIComponent(clientId)}/interactive`, {
          method: "POST",
          body: JSON.stringify({
            action: "block_screen",
            parameters: JSON.stringify({ enable }),
            timeoutMs: 15000,
          }),
        });
        return { content: [{ type: "text", text: data.output || "(no output)" }] };
      }

      case "lablock_kill_tasks": {
        const { clientId, processNames } = args as { clientId: string; processNames: string[] };
        const data = await apiFetch(`/api/clients/${encodeURIComponent(clientId)}/interactive`, {
          method: "POST",
          body: JSON.stringify({
            action: "kill_tasks",
            parameters: JSON.stringify({ processNames }),
            timeoutMs: 30000,
          }),
        });
        return { content: [{ type: "text", text: data.output || "(no output)" }] };
      }

      case "lablock_send_keystrokes": {
        const { clientId, keys } = args as { clientId: string; keys: string };
        const data = await apiFetch(`/api/clients/${encodeURIComponent(clientId)}/interactive`, {
          method: "POST",
          body: JSON.stringify({
            action: "send_keystrokes",
            parameters: JSON.stringify({ keys }),
            timeoutMs: 15000,
          }),
        });
        return { content: [{ type: "text", text: data.output || "(no output)" }] };
      }

      case "lablock_run_user_powershell": {
        const { clientId, command, timeoutSeconds } = args as Record<string, any>;
        const data = await apiFetch(`/api/clients/${encodeURIComponent(clientId)}/interactive`, {
          method: "POST",
          body: JSON.stringify({
            action: "run_powershell",
            parameters: JSON.stringify({ command, elevated: false, timeoutSeconds: timeoutSeconds || 60 }),
            timeoutMs: (timeoutSeconds || 60) * 1000 + 5000,
          }),
        });
        return { content: [{ type: "text", text: data.output || "(no output)" }] };
      }

      case "lablock_run_elevated_powershell": {
        const { clientId, command, timeoutSeconds } = args as Record<string, any>;
        const data = await apiFetch(`/api/clients/${encodeURIComponent(clientId)}/interactive`, {
          method: "POST",
          body: JSON.stringify({
            action: "run_powershell",
            parameters: JSON.stringify({ command, elevated: true, timeoutSeconds: timeoutSeconds || 60 }),
            timeoutMs: (timeoutSeconds || 60) * 1000 + 5000,
          }),
        });
        return { content: [{ type: "text", text: data.output || "(no output)" }] };
      }

      default:
        throw new Error(`Unknown tool: ${name}`);
    }
  } catch (err: any) {
    return {
      isError: true,
      content: [{ type: "text", text: `Error: ${err.message}` }],
    };
  }
});

server.setRequestHandler(ListResourcesRequestSchema, async () => ({
  resources: [
    {
      uri: "lablock://clients",
      name: "All LabLock Clients",
      description: "List of all registered clients with online status",
    },
    {
      uri: "lablock://stats",
      name: "LabLock Statistics",
      description: "Overall system statistics (client count, log counts, etc.)",
    },
  ],
}));

server.setRequestHandler(ReadResourceRequestSchema, async (req) => {
  const uri = req.params.uri;

  if (uri === "lablock://clients") {
    const data = await apiFetch("/api/clients");
    return {
      contents: [{ uri, mimeType: "text/plain", text: formatClients(data) }],
    };
  }
  if (uri === "lablock://stats") {
    const clients = await apiFetch("/api/clients");
    const logs = await apiFetch("/api/logs/stats?hoursBack=24");
    return {
      contents: [
        {
          uri,
          mimeType: "text/plain",
          text: [
            `Connected Clients: ${clients.total}`,
            `Online Clients: ${clients.online}`,
            `Activity Logs (24h): ${logs.totalLogs ?? "N/A"}`,
            `Active Clients (24h): ${logs.activeClients ?? "N/A"}`,
            `Event Breakdown:`,
            ...Object.entries(logs.eventCounts || {}).map(
              ([k, v]) => `  ${k}: ${v}`
            ),
          ].join("\n"),
        },
      ],
    };
  }

  // Dynamic client resource
  const clientMatch = uri.match(/^lablock:\/\/clients\/([^/]+)$/);
  if (clientMatch) {
    const data = await apiFetch(`/api/clients/${encodeURIComponent(clientMatch[1])}`);
    return {
      contents: [{ uri, mimeType: "text/plain", text: formatClient(data) }],
    };
  }

  throw new Error(`Unknown resource: ${uri}`);
});

async function main() {
  await login();
  const transport = new StdioServerTransport();
  await server.connect(transport);
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
