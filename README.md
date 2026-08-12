# LabLock

**LabLock** is a lightweight, self-hosted computer lab management system for Windows machines. It combines a Windows service agent (client), an ASP.NET Core server with a web dashboard, and a Model Context Protocol (MCP) interface for AI-driven remote control — all over a single SignalR WebSocket connection.

LabLock was built to manage a school computer lab (e.g. SMKN 3 Manado): monitor machines in real time, run commands, show messages, take screenshots, block screens, push silent updates, and hand an AI assistant direct access to every workstation.

---

## Features

### Monitoring
- **Real-time client list** — online/offline status, CPU %, memory %, active window, current user, OS version, agent version, IP, MAC
- **System info** — hardware summary (CPU model/cores, RAM, disk), installed software inventory, running processes
- **Activity logging** — keystrokes, window focus changes, process start/stop, all persisted to the server DB
- **Command history** — every command sent to every client, with output and exit code

### Remote control
- **Execute commands as SYSTEM** (session 0, invisible to the user) — file ops, services, registry, background tasks
- **Run PowerShell in the interactive session** (user-visible windows) — GUI automation, browser control, wallpaper changes
- **Run elevated PowerShell** — full admin rights **without a UAC popup**, via Task Scheduler with `/RL HIGHEST` created by the SYSTEM service
- **Message boxes** — modern borderless dialogs (message + text-reply input) that appear on top of everything
- **Screenshots** — capture the user's desktop remotely
- **Screen lock/block** — black full-screen overlay
- **Keystroke injection** — send SendKeys-style input to the active window
- **Kill processes** — terminate user-session processes by name
- **Active app detection** — get the foreground window title + process

### Fleet management
- **Silent over-the-air updates** — push new agent binaries to any client; the agent downloads, verifies SHA-256, stops the service, replaces itself, and restarts — all as SYSTEM, no user interaction
- **One-off installer** — `LabLock.ClientSetup.exe` bundles the client + config for new machines
- **Delete/disconnect clients** — deactivate a machine from the fleet

### AI integration
- **MCP server** (`/mcp`) — expose every LabLock capability to AI assistants (OpenAI, Anthropic, Gemini, Ollama) as function-calling tools
- **AI chat** — built-in dashboard page that runs an agent with live PC-control tools
- **Multi-provider** — OpenAI / Anthropic / Gemini / Ollama via a provider factory

### Web dashboard
- **Dark-themed single-page app** (vanilla JS, no framework) — hash router, SignalR live updates
- Pages: Dashboard, Clients, Logs, AI Chat, Settings

---

## Architecture

```
┌─────────────┐    SignalR (WebSocket)    ┌──────────────────┐
│  Client     │◄─────────────────────────►│  Server           │
│  Windows    │   heartbeat / commands    │  ASP.NET Core 8   │
│  Service    │                           │  Kestrel :5000    │
└──────┬──────┘                           └────────┬─────────┘
       │  named pipe                              │  EF Core SQLite
┌──────▼──────┐                           ┌────────▼─────────┐
│  Session     │                           │  Dashboard (SPA) │
│  Agent (user)│  interactive commands      │  :5000 (wwwroot)│
└─────────────┘                           └──────────────────┘
                                                       │
                                              ┌────────▼─────────┐
                                              │  MCP (HTTP)      │
                                              │  /mcp            │
                                              └──────────────────┘
```

| Component | Tech | Runs on |
|---|---|---|
| **Client Agent** | .NET 8 Windows Service (`net8.0-windows`, `win-x64`) | each lab PC |
| **Session Agent** | Spawned by the client service via `CreateProcessAsUser` into the interactive session; communicates over a named pipe | each lab PC (interactive session) |
| **Server** | ASP.NET Core 8, Kestrel `0.0.0.0:5000`, static wwwroot dashboard | Linux (Alpine) or Windows |
| **DB** | SQLite via EF Core (auto-migrated on startup) | server |
| **Shared** | Plain DTOs, no dependencies | both |
| **AI** | Multi-provider function-calling loop | server |

### Communication model

- **Server ↔ client**: SignalR hub at `/hub/client`, authenticated by `clientId` + API key in the query string.
- **Service ↔ interactive user**: a named pipe (`\\.\pipe\LabLockSessionAgent-S{n}`) with an **Everyone DACL**. The service writes request JSON; the worker executes in the user's session and replies on the same pipe (sequential I/O under a lock). Large payloads (screenshots) are supported by a 2 MB pipe buffer.
- **Interactive session** = the console session (`WTSGetActiveConsoleSessionId`). Commands that should *show* on screen run there; headless system commands run as SYSTEM in session 0.

---

## Repository layout

```
LabLock.sln
├── src/
│   ├── LabLock.Shared/        # DTOs (no dependencies)
│   ├── LabLock.Client/        # Windows service agent + session agent worker
│   ├── LabLock.ClientSetup/   # WinForms one-off installer
│   ├── LabLock.Installer/     # (legacy) installer
│   └── LabLock.Server/        # ASP.NET Core server + dashboard + MCP
└── deploy/                    # build / install / deploy scripts
```

---

## Quick start

### Prerequisites
- .NET 8 SDK (for building)
- Windows 10/11 target machines (`win-x64`)

### Build

```powershell
# Build everything into publish/
powershell -ExecutionPolicy Bypass -File deploy/build-all.ps1
```

Or individually:

```powershell
# Client (Windows service, self-contained single-file)
dotnet publish src/LabLock.Client -c Release -r win-x64 --self-contained -o publish/client

# Server (Linux musl for Alpine, or linux-x64/win-x64)
dotnet publish src/LabLock.Server -c Release -r linux-musl-x64 --self-contained -o publish/server

# Installer (bundles the client + config)
powershell -ExecutionPolicy Bypass -File deploy/build-client-setup.ps1 -PublishDir publish/client -OutputDir publish/client-setup
```

> **Note**: the production server runs on **Alpine (musl)**. Always publish `-r linux-musl-x64`; a glibc `linux-x64` publish fails at runtime on Alpine with `start-stop-daemon: failed to exec` (missing `/lib/ld-musl-x86_64.so.1`).

### Deploy the server

```bash
# On the Alpine host (OpenRC)
scp publish/server/LabLock.Server root@SERVER:/opt/lablock/
chmod +x /opt/lablock/LabLock.Server
# init script exports env vars; the run wrapper sets:
#   DOTNET_gcServer=0              (Workstation GC — prevents a 273 GB VmSize explosion)
#   DOTNET_GCHeapHardLimit=0x40000000  (1 GB heap cap)
rc-service lablock-server start
```

### Install the client

Run `LabLock.ClientSetup.exe` as an admin on each machine. It installs a Windows service named **`LabLockAgent`** with the binary at `C:\LabLock\LabLock.Client.exe`.

> The `LabLockAgent` service has a hardened security descriptor that **denies STOP/CHANGE_CONFIG even to Administrators** (`(D;;DCWP;;;BA)(D;;DCWP;;;BU)`) — only SYSTEM can stop/start it. Use the LabLock **update push** flow (which runs as SYSTEM) to restart clients remotely.

### First login

The dashboard boots with a default user:

- **URL**: `http://SERVER:5000`
- **Username**: `admin`
- **Password**: `admin`

---

## Client update flow (silent OTA)

1. Bump `<Version>` in `src/LabLock.Client/LabLock.Client.csproj`
2. Publish the client to a temp dir
3. Upload `LabLock.Client.exe` to the server's update dir (`/opt/lablock/data/client-update/`)
4. Write `version.json` (PascalCase keys):
   ```json
   {"Version":"X.Y.Z","Size":NNN,"Sha256":"xxx","UploadedAt":"2026-08-05T00:00:00Z"}
   ```
5. Restart the server (it caches the manifest in memory)
6. Call `lablock_push_update` for each client (or use the dashboard)

The client receives the SignalR `UpdateAgent` message, downloads from `/api/update/package`, verifies SHA-256, stops the service, replaces the binary, and restarts.

---

## MCP tools

The server exposes an MCP endpoint at `POST /mcp` (before auth middleware; typically protected by a reverse proxy). Every client capability is a callable tool:

| Tool | Session | Purpose |
|---|---|---|
| `lablock_list_clients` | – | List fleet with status/CPU/mem/version |
| `lablock_get_client` | – | Detail one client |
| `lablock_delete_client` | – | Deactivate + disconnect a client |
| `lablock_send_command` | **session 0 (SYSTEM)** | Headless commands (files, services, registry) — invisible to user |
| `lablock_run_user_powershell` | **interactive** | PowerShell in the user's session — visible windows |
| `lablock_run_elevated_powershell` | **interactive + elevated** | Admin PowerShell, **no UAC prompt** (Task Scheduler `/RL HIGHEST`) |
| `lablock_screenshot` | interactive | Desktop capture |
| `lablock_message_box` | interactive | Modern message dialog |
| `lablock_interactive_message` | interactive | Message + text reply |
| `lablock_block_screen` | interactive | Full-screen black overlay on/off |
| `lablock_get_active_app` | interactive | Foreground window |
| `lablock_send_keystrokes` | interactive | SendKeys injection |
| `lablock_kill_tasks` | interactive | Kill user-session processes |
| `lablock_get_system_info` | – | Hardware/software/process inventory |
| `lablock_get_activity_logs` | – | Query keystrokes/focus/process logs |
| `lablock_get_command_history` | – | Past command executions |
| `lablock_get_log_stats` | – | Log event counts |
| `lablock_get_update_status` | – | Published update package info |
| `lablock_push_update` | – | Push OTA update to a client |
| `lablock_execute` | server | Run a command on the **server** (uses `sh` on Linux, `powershell.exe` on Windows) |

**Session routing rule**: if the user should **see** or **interact** with the result, use an *interactive* tool — never `send_command` (session 0 is invisible to the user).

---

## AI integration

The server's `/mcp` endpoint lets any MCP-capable assistant drive the whole lab. The dashboard also ships an **AI Chat** page with a built-in tool loop:

- Up to 10 tool-call iterations per user message
- System prompt dynamically appended with live PC status
- Providers: **OpenAI, Anthropic, Gemini, Ollama** (configured in Settings)

---

## Configuration

| Setting | Where | Default |
|---|---|---|
| Server URL | `LabLockDefaults.cs` (`ServerUrl`) | `http://192.168.1.58:5000` |
| DB path | `appsettings.json` (`ConnectionStrings:DefaultConnection`) | `data/lablock.db` |
| Default dashboard login | bootstrap in `Program.cs` | `admin` / `admin` |
| Display timezone | env `LABLOCK_DISPLAY_TIMEZONE` | `Asia/Manado` (UTC+8) |
| Server API envs | `LABLOCK_API_URL/USERNAME/PASSWORD` | `http://127.0.0.1:5000` / `admin` / `admin` |

---

## Deploy scripts

| Script | Purpose |
|---|---|
| `deploy/build-all.ps1` | Build server + client, copy into `publish/` |
| `deploy/build-client-setup.ps1` | Build the one-off Windows installer |
| `deploy/install-client.ps1` | Install client as a service |
| `deploy/uninstall-client.ps1` | Remove client service |
| `deploy/server-deploy.sh` | Publish + deploy server to Alpine |
| `deploy/server-setup.sh` | Provision the Alpine host (OpenRC init) |

---

## Known behaviors & troubleshooting

- **Service restart requires SYSTEM**: the agent service denies admins STOP/CHANGE_CONFIG. Restart via the update-push flow, or kill the worker (the service watchdog respawns it).
- **`lablock_execute`** runs on the *server*, not a client. On the Alpine server it uses `/bin/sh`; PowerShell is only used on a Windows server host.
- **Elevated commands**: implemented by having the SYSTEM service create a one-off Task Scheduler task (`/rl HIGHEST /ru <user> /it`), trigger it, and capture output through a wrapper script — no UAC prompt, correct user/session.
- **Timezone**: MCP text output is formatted in `LABLOCK_DISPLAY_TIMEZONE` (default Asia/Manado); the web dashboard uses the browser's local time.

---

## License

[Apache License 2.0](LICENSE)

Copyright 2026 kilvz

Licensed under the Apache License, Version 2.0 (the "License"); you may not use this software except in compliance with the License. You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software distributed under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the License for the specific language governing permissions and limitations under the License.
