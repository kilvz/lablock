# LabLock — AGENTS.md

## Server
- **Host**: Alpine at `192.168.1.58`, port 5000, OpenRC service `lablock-server`
- **Binary**: `/opt/lablock/LabLock.Server` (linux-musl-x64 self-contained)
- **DB**: `/opt/lablock/data/lablock.db` (SQLite WAL)
- **Log**: `/var/log/lablock-server.log`
- **Run config**: `/opt/lablock/run-web.sh` sets `DOTNET_gcServer=0` (Workstation GC) + `DOTNET_GCHeapHardLimit=0x40000000` (1 GB heap limit)
- **Stop/Start**: `rc-service lablock-server start|stop|restart`
- **Init**: `/etc/init.d/lablock-server` (OpenRC, exports env vars)
- **MCP**: HTTP `"type": "remote"` at `http://192.168.1.58:5000/mcp` — NO plink, NO SSH
- **MCP public**: `https://lablock.smkn3manado.sch.id/mcp` (Basic Auth: `lablock` / `a305fffd6d0b1c252804d296492cb34c`)
- **MCP endpoint**: `POST /mcp` (before auth middleware, AllowAnonymous)

## Client update flow

### How it works
1. **Build client**: update version in `src/LabLock.Client/LabLock.Client.csproj` `<Version>` tag
2. **Publish**: `dotnet publish src/LabLock.Client -c Release -r win-x64 --self-contained -o publish/client`
3. **Upload**: `pscp publish/client/LabLock.Client.exe root@192.168.1.58:/opt/lablock/data/client-update/LabLock.Client.exe`
4. **Register manifest**: write `version.json` to `/opt/lablock/data/client-update/version.json`:
   ```json
   {"Version":"X.Y.Z","Size":NNN,"Sha256":"xxx","UploadedAt":"2026-08-05T00:00:00Z"}
   ```
   Compute sha256 on Alpine: `sha256sum /opt/lablock/data/client-update/LabLock.Client.exe`
   Compute size: `stat -c%s /opt/lablock/data/client-update/LabLock.Client.exe`
5. **Restart server**: `rc-service lablock-server restart` (reloads manifest from disk)
6. **Push**: `lablock_lablock_push_update` to each client via MCP
7. **Auto**: client receives SignalR "UpdateAgent" message → downloads from `http://192.168.1.58:5000/api/update/package?apiKey=...` → checks sha256 → stops service → replaces binary → starts service

### Common bugs
- **Client version not updating**: you forgot to bump `<Version>` in csproj. The client compares `Assembly.GetExecutingAssembly().GetName().Version` against the pushed version.
- **Server not serving new binary**: the server caches `version.json` in memory — restart the server after changing it.
- **Push says "No client update package published"**: `version.json` is missing or malformed (must use PascalCase: `Version`, `Size`, `Sha256`, `UploadedAt`).
- **Never touch the auth middleware**: MCP `/mcp` endpoint must stay BEFORE `UseAuthentication()`. Don't add `[Authorize]` to MCP endpoint.
- **Server OOM**: uses `DOTNET_gcServer=0` (Workstation GC, not Server GC — prevents 273 GB VmSize explosion) + `DOTNET_GCHeapHardLimit=0x20000000` (512 MB heap cap for Alpine).
- **MCP is HTTP-only**: stdio MCP code was REMOVED from Program.cs. HTTP POST `/mcp` is the ONLY MCP path. Server emits "[INFO] Server starting — HTTP-only MCP mode." on startup. If you see "listening on stdio" in logs, the old binary is still deployed.
- **Server crashes — root causes (fixed v1.4.3)**:
  1. No global exception handler → added `AppDomain.UnhandledException` + `TaskScheduler.UnobservedTaskException` handlers (Program.cs)
  2. Memory leak: `ClientStateService._clients` never pruned → added `PruneStaleClientStates()` periodic cleanup (24h TTL)
  3. Heartbeat missing DB retry wrapper → changed `SaveChangesAsync()` to `SaveChangesWithRetryAsync()` (ClientHub.cs:179)
  4. ALTER TABLE migration noise on every startup → `EnsureSchemaUpgrade` now checks `PRAGMA table_info` before ALTER (no more "Failed executing DbCommand" spam)
- **Server auto-restart**: `/opt/lablock/run-web.sh` wrapper loops forever — server exits → 5s sleep → restart. Init script `command="/opt/lablock/run-web.sh"` (not the binary directly). `/etc/init.d/lablock-server` updated.
- **ALWAYS update AGENTS.md when a bug is fixed or a problem is solved.** This file is the team's runbook.

### Server deploy-to-restart flow
  1. `dotnet publish src/LabLock.Server -c Release -r linux-musl-x64 --self-contained -o publish/server-vX`
  2. `tar czf /tmp/lablock/server-vX.tar.gz -C publish/server-vX .`
  3. `pscp -hostkey "ed25519@22 SHA256:aSWXxyWlR/DY4aqP5XikhTaKOd2Jml8FxePlVSyFAyQ" -pw hermes1234 /tmp/lablock/server-vX.tar.gz root@192.168.1.58:/opt/lablock/`
  4. On Alpine: `rc-service lablock-server stop && kill $(pgrep -f LabLock.Server) 2>/dev/null && sleep 2 && sqlite3 /opt/lablock/data/lablock.db 'DELETE FROM ActivityLogs; DELETE FROM CommandHistories;' && > /var/log/lablock-server.log && rm -f /opt/lablock/LabLock.Server /opt/lablock/*.dll && tar xzf /opt/lablock/server-vX.tar.gz -C /opt/lablock && chmod +x /opt/lablock/LabLock.Server && rc-service lablock-server start`
  5. Verify: `curl -s -m 10 -X POST -H 'Content-Type: application/json' -d '{"jsonrpc":"2.0","method":"tools/list","id":1}' http://localhost:5000/mcp`

### Installer build
```powershell
# Build client first (Version must be bumped!):
dotnet publish src/LabLock.Client -c Release -r win-x64 --self-contained -o publish/client

# Build installer (WinForms, bundles client + config):
powershell -ExecutionPolicy Bypass -File deploy/build-client-setup.ps1 -PublishDir publish/client -OutputDir publish/client-setup

# Output: publish/client-setup/LabLock.ClientSetup.exe (~98 MB single-file)
# Run as admin to install. /uninstall to remove.
```

## Project state

**Code exists.** Full .NET 8 solution:

## What to build

| Layer | Tech | Notes |
|---|---|---|
| **Client Agent** | .NET 8 Windows Service (`net8.0-windows`, `win-x64`) | Worker SDK, self-contained single-file publish |
| **Server** | ASP.NET Core 8 (Linux) | Kestrel on `0.0.0.0:5000`, serves static wwwroot dashboard |
| **Dashboard** | Vanilla JS (no framework) | Hash-based SPA, SignalR for real-time, served as static files |
| **Shared** | .NET 8 class library (plain DTOs, no deps) | Referenced by Client + Server |
| **DB** | SQLite via EF Core | Auto-migrated on server startup |
| **AI** | Multi-provider (OpenAI, Anthropic, Gemini, Ollama) | Function-calling tools for PC control |
| **Client-Server** | SignalR WebSocket | Hub at `/hub/client`, auth via query-string API key |

## Project structure (as planned)

```
lck/
├── LabLock.sln
├── src/
│   ├── LabLock.Shared/      # DTOs (no dependencies)
│   ├── LabLock.Client/      # Windows Service agent
│   └── LabLock.Server/      # ASP.NET Core + Dashboard
└── deploy/                  # install/uninstall/deploy scripts
```

## Key implementation notes

- **SignalR auth**: Client passes `?clientId=&apiKey=` on hub connect; dashboard passes `?role=dashboard&token=`. Both validated in `OnConnectedAsync`.
- **Keystroke logging**: Requires a dedicated STA thread for the Windows low-level keyboard hook (`SetWindowsHookEx`). The hook delegate must be stored in a field to prevent GC.
- **Log buffer**: `ConcurrentQueue<ActivityLogDto>` + local SQLite spillover (`buffer.db`). Flushed every 10s to server.
- **AI tool loop**: `AiChatService.ChatAsync` runs up to 10 tool-call iterations per user message (AI → tool → AI → tool...). The system prompt is dynamically appended with live PC status.
- **Dashboard SPA**: `router.js` reads `window.location.hash`, renders pages into `#content`. Each page module has `render(container, ...params)`.
- **Dashboard auth check**: `app.js` redirects to `login.html` if no `lablock_token` in localStorage.

## Commands to scaffold

Before writing any implementation code, create the solution structure:
```
dotnet new sln -n LabLock
dotnet new classlib -n LabLock.Shared -o src/LabLock.Shared -f net8.0
dotnet new worker -n LabLock.Client -o src/LabLock.Client -f net8.0-windows
dotnet new web -n LabLock.Server -o src/LabLock.Server -f net8.0
dotnet sln add src/LabLock.Shared src/LabLock.Client src/LabLock.Server
```

Client CSProj needs `<RuntimeIdentifier>win-x64</RuntimeIdentifier>`, `<PublishSingleFile>true</PublishSingleFile>`, `<SelfContained>true</SelfContained>`.

## NuGet dependencies

| Project | Key packages |
|---|---|
| **Client** | `Microsoft.AspNetCore.SignalR.Client`, `Microsoft.Extensions.Hosting.WindowsServices`, `System.Management`, `Microsoft.PowerShell.SDK`, `Microsoft.Data.Sqlite` |
| **Server** | `Microsoft.EntityFrameworkCore.Sqlite`, `Microsoft.EntityFrameworkCore.Design`, `Microsoft.AspNetCore.Authentication.JwtBearer`, `BCrypt.Net-Next` |
| **Shared** | none (plain DTOs) |

## Design system

Dashboard uses exact CSS custom properties defined in `IMPLEMENTATION_PLAN.md` §4. Dark theme (`--bg-primary: #0a0a0f`). Google Fonts: Inter + JetBrains Mono. Do not deviate from the CSS variables — they are used throughout all dashboard components.

## Deployment

- **Target machines**: The client is installed on the client PCs (e.g. PPLGDT-AL001), **not on this dev machine**. The server is installed on the Alpine host (`root@192.168.1.58`, OpenRC — not systemd), **not on this dev machine**.
- Client: `install-client.ps1` → Windows Service named `LabLockAgent`, binary at `C:\LabLock\`
- Server: deployed via pscp/plink to Alpine → binary at `/opt/lablock/`, service `rc-service lablock-server`
- **Server RID: MUST publish `-r linux-musl-x64` — musl, NOT libc/glibc.** The Alpine host uses musl; a `linux-x64` (glibc) self-contained publish fails at runtime with `start-stop-daemon: failed to exec ... No such file or directory` (the file exists but the dynamic loader `/lib/ld-musl-x86_64.so.1` is missing). Always: `dotnet publish src/LabLock.Server -c Release -r linux-musl-x64 --self-contained -o publish/server-fix-musl`.
- Default bootstrap credentials in `appsettings.json`: `admin`/`admin` (first login creates DB user with BCrypt hash)

## MCP Public Access

The LabLock MCP is exposed publicly at:
- **URL**: `https://lablock.smkn3manado.sch.id/mcp`
- **Auth**: HTTP Basic Auth (required before the request reaches LabLock)
- **Username**: `lablock`
- **Password**: `a305fffd6d0b1c252804d296492cb34c`
- **SSL**: Let's Encrypt (NPM cert id 12, renews automatically)

### Architecture
```
Internet → router (IPv6) → LXC 100 (nginx-proxy-manager, 192.168.1.10)
  → TLS termination + Basic Auth check
  → proxy_pass http://192.168.1.58:5000/mcp
  → LabLock.Server (Alpine, 192.168.1.58)
```

### Components involved
- **DNS**: `ddns-pro` (LXC 100, `/opt/ddns-pro/`) — syncs A/AAAA records to Cloudflare
- **Reverse proxy**: Nginx Proxy Manager (LXC 100 Docker container `nginx-proxy-manager-app-1`, port 81 admin)
- **Htpasswd file**: `/opt/nginx-proxy-manager/data/nginx/access/lablock.htpasswd` (bcrypt hash)
- **NPM DB**: `/opt/nginx-proxy-manager/data/database.sqlite` (proxy host id=5)

### IPv4 caveat
The router does NOT forward port 443 IPv4 to LXC 100; external access works via **IPv6 only** (the NPM container has a public GUA). To enable IPv4 access, port-forward 443 TCP to `192.168.1.10` on the router.

### Adding a new domain
1. Add domain entry to `/opt/ddns-pro/config.json` `domains[]` array
2. Restart ddns-pro: `kill $(pgrep -f ddns-pro); cd /opt/ddns-pro && ./ddns-pro &`
3. Add NPM proxy host via API or admin panel (port 81)
4. Request SSL cert via NPM API

### MCP endpoint security
The `/mcp` endpoint in LabLock.Server is **before auth middleware** (AllowAnonymous). The NPM proxy provides the only authentication layer. Never add `[Authorize]` to `/mcp` — it runs before `UseAuthentication()`. If you need additional auth, add it at the NPM layer (IP allowlist, additional headers, etc.).
