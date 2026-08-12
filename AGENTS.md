# LabLock — AGENTS.md

## This machine IS client 99
- **Hostname**: ZNC22A82W11-SDK, IP `192.168.5.13`
- **User**: `Digitalisasi` (session 1, NOT admin — sc stop requires UAC)
- **LabLock Agent**: installed to `C:\LabLock\LabLock.Client.exe`, Windows Service `LabLockAgent`
- **MCP tools (lablock_*)** route through the server → interactive pipe → worker. When the pipe is broken, MCP tools time out. Debug the pipe/code DIRECTLY with local bash/PowerShell instead.
- **Service restart**: needs elevation (`Start-Process -Verb RunAs` for `sc stop/start`)
- **Service logs** are at `C:\Windows\Temp\lablock-service.log` (readable by SYSTEM only; check via `Start-Process -Verb RunAs` or debug build with user-accessible log path)
- **Debug log path**: `C:\LabLock\logs\lablock-service.log` and `C:\LabLock\logs\lablock-svc2.log` (user-readable)

## Interactive Pipe Bugs Fixed (Aug 2026)
1. **Pipe name mismatch**: `GetInteractiveUser()` appended email → `Digitalisasi (slbdorkas@outlook.com)`. Worker used `Environment.UserName` = `Digitalisasi`. Also contained `\` (invalid). Fixed: session-ID based pipe name + explicit argument passing.
2. **Pipe DACL**: SYSTEM-created pipe had default DACL (SYSTEM+Admins only). Non-admin user worker couldn't open. Fixed: Everyone DACL via `ConvertStringSecurityDescriptorToSecurityDescriptor("D:(A;;GA;;;WD)")`.
3. **SignalR type mismatch**: Server sent `JsonSerializer.Serialize(payload)` as a STRING. Client handler expected JsonElement/Dictionary but received a string → `GetProperty` threw. Fixed: client handler uses `On<Dictionary<string, JsonElement>>`, server sends `(object)payload` directly.
4. **Synchronous pipe deadlock**: ReadLoop thread blocked on `ReadFile` while `SendAsync` tried `WriteFile` on the same non-overlapped handle → deadlock. Fixed: eliminated ReadLoop entirely, moved to sequential I/O in `SendAsync` (write request, read responses on same lock).
5. **JsonElement lifetime**: `JsonDocument` disposed before caller accessed returned `JsonElement` → `ObjectDisposedException`. Fixed: return raw JSON string from `SendAsync`.
6. **Pipe buffer too small**: Screenshot base64 >64KB truncated in 64K buffer → garbled partial data. Fixed: 2MB pipe buffer + 2MB read buffer.

## Timezone Display Fix (Aug 2026)
- **Problem**: MCP text output (`lablock_list_clients`, `lablock_get_client`, logs, command history) formatted `LastHeartbeat`/timestamps raw as UTC (e.g. `04:08`), while the lab is on WITA (Asia/Manado, UTC+8) → all times appeared 8h behind the client machines.
- **Fix**: `McpServer.cs` now has a static `DisplayTimeZone` (env `LABLOCK_DISPLAY_TIMEZONE`, default `Asia/Manado`, fallback custom +08:00 if tzdata missing) + `Fmt(DateTime utc)` helper. All `:yyyy-MM-dd HH:mm:ss` interpolations were replaced with `{Fmt(x)}`.
- **Dashboard unaffected**: `wwwroot/js/pages/logs.js` already uses `new Date(...).toLocaleTimeString()` (browser-local).
- **Deploy gotcha**: `pkill -f LabLock.Server`/`pgrep -f LabLock.Server` in a remote command ALSO matches the plink shell's own command line → kills the deploy script mid-chain. Use an anchored pattern `pkill -f '^/opt/lablock/LabLock.Server'` instead.

## Pipe Loop Concurrency Bug (v1.4.26, Aug 2026)
- **Problem**: Message box (and other interactive tools) timed out — the MCP tool aborted. Log showed `ConnectNamedPipe failed, err=535` followed by `Pipe already connected, reading ready` with no `Got ready`. Worker process was alive but pipe was stuck. After killing worker, the service spawned multiple agents simultaneously (log showed 4 spawns in 3 seconds).
- **Root cause #1**: `Start()` was not idempotent — calling it while a thread was already running created a second `PipeLoop` thread. Both threads raced on `ConnectNamedPipe`, with one getting `err=535` (already connected) and blocking in `ReadOneMessage()` waiting for a "ready" that was already consumed.
- **Root cause #2**: `err=535` handling blindly called `ReadOneMessage()` regardless of whether a ready was already received.
- **Fix**: `Start()` guards against re-entry (`_pipeThread.IsAlive`). Added `_agentReady` boolean to track handshake state; when true, the ready read is skipped. Agent death now resets `_agentReady`, closes pipe handle, and recreates it for clean state.
- **Deployed**: v1.4.26, uploaded via pscp, manifest registered, server restarted, pushed to client 99. Restart via MCP push works because the update flow runs as SYSTEM.
- **Non-admin restart**: `Digitalisasi` IS a member of the Administrators group (verified via `net localgroup Administrators`). But the `LabLockAgent` service has a restrictive security descriptor — its SDDL starts with `(D;;DCWP;;;BA)(D;;DCWP;;;BU)` = **Deny SERVICE_CHANGE_CONFIG + SERVICE_STOP to Administrators and Users**. So even an elevated admin CANNOT stop/start the service (`sc stop` → "Access is denied", error 5). Only SYSTEM (SY) can. Use the LabLock update push flow (runs as SYSTEM) to restart, or kill the worker process (watchdog respawns it).

## Elevated Commands Without UAC (v1.4.30, Aug 2026)
- **Goal**: `lablock_run_elevated_powershell` should run as admin in the user's session WITHOUT a UAC popup.
- **Problem**: The old worker-side path used `ProcessStartInfo { Verb = "runas", RedirectStandardOutput = true }` — but `Verb=runas` + redirected streams **conflict** in .NET (`Process.Start` throws `InvalidOperationException`), so elevated commands always failed with an empty `{}`. Even if fixed, `runas` pops UAC.
- **Solution**: The LabLock **service** (runs as SYSTEM) creates a one-off **scheduled task** via `schtasks`:
  - `/create /tn LabLockElevated-<guid> /tr "powershell ... -File <wrapper.ps1>" /sc once /rl HIGHEST /ru "<DOMAIN\User>" /it /f`
  - `/rl HIGHEST` = elevated, `/it` = interactive-only (no stored password needed), `/ru <user>` = runs AS the interactive user in their session
  - Trigger with `/run`, poll for output file, then `/delete`.
  - Only SYSTEM can create a `/RL HIGHEST` task (verified: non-elevated user gets "Access is denied"); the service IS SYSTEM, so it works.
- **Output capture**: Task Scheduler `/tr` cannot do shell redirection, so redirection lives INSIDE a wrapper `.ps1`: `try { & '<inner.ps1>' *> '<out>' 2>&1 } catch { $_ | Out-File '<out>' }`. Files go in `C:\Users\Public\lablock-elev\` (world-readable by SYSTEM + user). The wait loop must open the out file with `FileShare.ReadWrite` + retry, because the elevated powershell still holds the handle briefly (else `IOException: being used by another process`).
- **Where**: `SessionAgentService.RunElevatedPowerShell()` (SYSTEM side); `ConnectionService.ExecuteInteractive` intercepts `run_powershell`+`elevated=true` before it reaches the pipe. `SessionContextService.GetInteractiveUserAccount()` returns plain `DOMAIN\User` (no email suffix).
- **Verified**: output `"STDOUT-CHECK: elevated-command-ran"`, exit 0, `IsInRole(Administrator)=True`, `net session` success, NO UAC prompt.
- **Admin note**: `IsInRole(Administrator)` from a non-elevated shell returns **False** even for an admin account (UAC-filtered token) — that is NOT proof of non-admin. Use `net localgroup Administrators` to check membership.

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

## MCP Tool Session Routing (CRITICAL — read before executing commands)

| Tool | Session | User sees it? | Use for |
|---|---|---|---|
| `lablock_send_command` | **Session 0 (SYSTEM)** | **NO** — invisible to user | File ops, services, registry, system-level tasks |
| `lablock_run_user_powershell` | **Interactive session** (session 1/2/3) | **YES** — user sees the window | Client-visible operations: wallpaper, GUI apps, browser, message to user |
| `lablock_run_elevated_powershell` | Elevated (UAC prompt) | **YES** — UAC popup visible | Admin tasks: driver changes, system config |
| `lablock_screenshot` | Interactive session | N/A | Screenshots always run in user session |
| `lablock_message_box` | Interactive session | **YES** | Always visible to user |
| `lablock_block_screen` | Interactive session | **YES** | Black overlay on user's screen |
| `lablock_get_active_app` | Interactive session | N/A | Foreground window detection |
| `lablock_send_keystrokes` | Interactive session | **YES** | Keystroke injection |
| `lablock_kill_tasks` | Interactive session | **YES** | Kill interactive user processes |

**Rule**: If the user should SEE or INTERACT with the result, use `run_user_powershell`, NEVER `send_command`. `send_command` runs headless in session 0 — the user won't see windows, message boxes, or wallpaper changes. When in doubt, prefer `run_user_powershell`.

### Session routing also baked into MCP tool descriptions
The .NET server's `McpServer.cs` and the TypeScript bridge `lablock-mcp/index.ts` both have explicit session-routing annotations in every tool description. If the AI still routes commands to the wrong session, the tool descriptions are definitive.

## 1.4.6 fix — multithreaded session agent worker

### Problem
Commands timed out intermittently (screenshot, block_screen, message_box, etc.). Root cause: `SessionAgentWorker` read thread processed commands **synchronously inline** — a `run_powershell` command blocked the read thread for its entire duration (up to 60s). All subsequent commands queued in the pipe buffer until the first command completed, causing server-side timeouts.

### Fix (SessionAgentWorker.cs)
1. **ThreadPool dispatch** — read thread now does `ThreadPool.QueueUserWorkItem(_ => { var response = Dispatch(json); SendPipe(response); })` and immediately loops to read the next command. Commands execute concurrently on separate threads.
2. **Thread-safe SendPipe** — `lock (PipeWriteLock)` protects concurrent `WriteFile` calls from multiple response threads.
3. **TakeScreenshot 20s timeout** — `CopyFromScreen` can hang indefinitely on locked/inaccessible desktops; now has a hard 20s cap via `ManualResetEventSlim.Wait()`.

### Deployed
- Client v1.4.6 built, uploaded to Alpine, pushed to all 20 clients
- Server rebuilt with updated MCP tool descriptions (session routing per tool)
- TypeScript MCP bridge (lablock-mcp) updated with 9 missing interactive tools + fixed descriptions

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
