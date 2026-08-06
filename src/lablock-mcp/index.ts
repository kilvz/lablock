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
      description: "Send a command to a specific LabLock client for remote execution. The client executes it on its machine. Use for client-side operations like lock, shutdown, restart, or custom PowerShell.",
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
