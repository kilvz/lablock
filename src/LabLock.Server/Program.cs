using System.Text;
using System.Text.RegularExpressions;
using LabLock.Server.Data;
using LabLock.Server.Hubs;
using LabLock.Server.Mcp;
using LabLock.Server.Services;
using LabLock.Server.Services.Ai;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

await RunServer(args);

static string ResolveDbConnectionString(IConfiguration config)
{
    var env = Environment.GetEnvironmentVariable("LABLOCK_DB_PATH");
    if (!string.IsNullOrWhiteSpace(env))
        return $"Data Source={Path.GetFullPath(env)}";

    var cs = config.GetConnectionString("DefaultConnection") ?? "Data Source=data/lablock.db";
    var match = Regex.Match(cs, @"Data Source=(?<path>[^;]*)", RegexOptions.IgnoreCase);
    if (match.Success)
    {
        var p = match.Groups["path"].Value.Trim();
        if (!string.IsNullOrEmpty(p) && !Path.IsPathRooted(p))
        {
            var abs = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, p));
            cs = cs[..match.Groups["path"].Index] + abs + cs[(match.Groups["path"].Index + match.Groups["path"].Length)..];
        }
    }
    return cs;
}

static void EnsureDbDirectory(string connectionString)
{
    var match = Regex.Match(connectionString, @"Data Source=(?<path>[^;]*)", RegexOptions.IgnoreCase);
    if (match.Success)
    {
        var p = match.Groups["path"].Value.Trim();
        if (!string.IsNullOrEmpty(p))
        {
            var dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
        }
    }
}

static void ConfigureSqlitePragmas(string connectionString)
{
    try
    {
        using var conn = new SqliteConnection(connectionString);
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            cmd.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA synchronous=NORMAL;";
            cmd.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA busy_timeout=30000;";
            cmd.ExecuteNonQuery();
        }
    }
    catch
    {
        // Non-fatal: fall back to default journal mode.
    }
}

static void EnsureSchemaUpgrade(AppDbContext db)
{
    var columns = new Dictionary<string, HashSet<string>>();
    try
    {
        using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
        db.Database.GetDbConnection().Open();
        using var tablesReader = cmd.ExecuteReader();
        var tableNames = new List<string>();
        while (tablesReader.Read())
            tableNames.Add(tablesReader.GetString(0));
        tablesReader.Close();

        foreach (var table in tableNames)
        {
            cmd.CommandText = $"PRAGMA table_info('{table}')";
            using var colReader = cmd.ExecuteReader();
            var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (colReader.Read())
                cols.Add(colReader.GetString(1));
            columns[table] = cols;
        }
    }
    catch
    {
        // Fall back to silent try/catch below
    }

    var alterStatements = new[]
    {
        ("Clients", "InteractiveUser", "ALTER TABLE Clients ADD COLUMN InteractiveUser TEXT"),
        ("Clients", "InteractiveSessionId", "ALTER TABLE Clients ADD COLUMN InteractiveSessionId INTEGER"),
        ("Clients", "AgentSessionId", "ALTER TABLE Clients ADD COLUMN AgentSessionId INTEGER"),
        ("CommandHistories", "SessionId", "ALTER TABLE CommandHistories ADD COLUMN SessionId INTEGER"),
        ("CommandHistories", "InteractiveSessionId", "ALTER TABLE CommandHistories ADD COLUMN InteractiveSessionId INTEGER"),
        ("CommandHistories", "InteractiveUser", "ALTER TABLE CommandHistories ADD COLUMN InteractiveUser TEXT"),
        ("CommandHistories", "RunAsUser", "ALTER TABLE CommandHistories ADD COLUMN RunAsUser TEXT"),
        ("CommandHistories", "OsVersion", "ALTER TABLE CommandHistories ADD COLUMN OsVersion TEXT"),
        ("CommandHistories", "WorkingDirectory", "ALTER TABLE CommandHistories ADD COLUMN WorkingDirectory TEXT"),
        ("CommandHistories", "DurationMs", "ALTER TABLE CommandHistories ADD COLUMN DurationMs INTEGER")
    };

    foreach (var (table, column, sql) in alterStatements)
    {
        if (columns.TryGetValue(table, out var cols) && cols.Contains(column))
            continue;

        try
        {
            db.Database.ExecuteSqlRaw(sql);
        }
        catch
        {
        }
    }

    var backfillStatements = new[]
    {
        "UPDATE Clients SET InteractiveUser = '' WHERE InteractiveUser IS NULL",
        "UPDATE Clients SET InteractiveSessionId = 0 WHERE InteractiveSessionId IS NULL",
        "UPDATE Clients SET AgentSessionId = 0 WHERE AgentSessionId IS NULL",
        "UPDATE CommandHistories SET SessionId = 0 WHERE SessionId IS NULL",
        "UPDATE CommandHistories SET InteractiveSessionId = 0 WHERE InteractiveSessionId IS NULL",
        "UPDATE CommandHistories SET InteractiveUser = '' WHERE InteractiveUser IS NULL",
        "UPDATE CommandHistories SET RunAsUser = '' WHERE RunAsUser IS NULL",
        "UPDATE CommandHistories SET OsVersion = '' WHERE OsVersion IS NULL",
        "UPDATE CommandHistories SET WorkingDirectory = '' WHERE WorkingDirectory IS NULL",
        "UPDATE CommandHistories SET DurationMs = 0 WHERE DurationMs IS NULL"
    };

    foreach (var sql in backfillStatements)
    {
        try
        {
            db.Database.ExecuteSqlRaw(sql);
        }
        catch
        {
        }
    }
}

static async Task RunServer(string[] args)
{
    var builder = WebApplication.CreateBuilder(args);

    builder.WebHost.UseUrls("http://0.0.0.0:5000");

    var services = builder.Services;

    services.AddDbContext<AppDbContext>(options =>
        options.UseSqlite(ResolveDbConnectionString(builder.Configuration)));

    services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            var jwtKey = builder.Configuration["Jwt:Key"] ?? "LabLockSuperSecretKey2026!@#$%^&*()";
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = "LabLock",
                ValidAudience = "LabLock",
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
            };

            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    var accessToken = context.Request.Query["access_token"];
                    if (!string.IsNullOrEmpty(accessToken))
                        context.Token = accessToken;

                    return Task.CompletedTask;
                }
            };
        });

    services.AddAuthorization();

    services.AddSignalR(options =>
        {
            options.MaximumReceiveMessageSize = 8 * 1024 * 1024;
        })
        .AddJsonProtocol(options =>
        {
            options.PayloadSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        });

    services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            policy.AllowAnyOrigin()
                .AllowAnyMethod()
                .AllowAnyHeader();
        });

        options.AddPolicy("SignalR", policy =>
        {
            policy.SetIsOriginAllowed(_ => true)
                .AllowAnyMethod()
                .AllowAnyHeader()
                .AllowCredentials();
        });
    });

    services.AddHttpClient("AiClient", client =>
    {
        client.Timeout = TimeSpan.FromMinutes(5);
    });

    services.AddSingleton<ClientStateService>();
    services.AddSingleton<ClientUpdateService>();
    services.AddSingleton<McpServer>();

    services.AddScoped<LogStorageService>();
    services.AddScoped<CommandService>();
    services.AddScoped<AiProviderFactory>();
    services.AddScoped<AiToolExecutor>();
    services.AddScoped<AiChatService>();

    services.AddControllers();
    services.AddEndpointsApiExplorer();

    var app = builder.Build();

    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        EnsureDbDirectory(ResolveDbConnectionString(builder.Configuration));
        ConfigureSqlitePragmas(ResolveDbConnectionString(builder.Configuration));
        db.Database.EnsureCreated();
        EnsureSchemaUpgrade(db);

        if (!db.DashboardUsers.Any())
        {
            db.DashboardUsers.Add(new LabLock.Server.Models.DashboardUser
            {
                Username = "admin",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("admin"),
                DisplayName = "Administrator",
                Role = "admin",
                IsActive = true
            });
            db.SaveChanges();
        }
    }

    app.UseCors();

    app.UseStaticFiles();

    app.UseRouting();

    // HTTP-based MCP endpoint — before auth, no JWT needed
    app.MapPost("/mcp", async (HttpContext ctx, McpServer mcp) =>
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var body = await reader.ReadToEndAsync();
        var response = await mcp.ProcessRequestAsync(body);
        if (response == null)
        {
            ctx.Response.StatusCode = 204;
            return;
        }
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(response);
    });

    app.UseAuthentication();
    app.UseAuthorization();

    app.UseCors("SignalR");

    app.MapHub<ClientHub>("/hub/client").RequireCors("SignalR");

    app.MapControllers();

    app.MapFallbackToFile("index.html");

    AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    {
        var ex = e.ExceptionObject as Exception;
        Console.Error.WriteLine($"[FATAL] Unhandled exception: {ex?.GetType().Name}: {ex?.Message}");
        Console.Error.WriteLine($"[FATAL] {ex?.StackTrace}");
        if (e.IsTerminating)
            Console.Error.WriteLine("[FATAL] Process is terminating.");
        Environment.ExitCode = 1;
    };

    TaskScheduler.UnobservedTaskException += (_, e) =>
    {
        Console.Error.WriteLine($"[FATAL] Unobserved task exception: {e.Exception?.GetType().Name}: {e.Exception?.Message}");
        e.SetObserved();
    };

    Console.Error.WriteLine("[INFO] Server starting — HTTP-only MCP mode.");

    var stateService = app.Services.GetRequiredService<ClientStateService>();
    _ = Task.Run(async () =>
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromHours(1));
            try
            {
                stateService.PruneStaleClientStates(TimeSpan.FromHours(24), DateTime.UtcNow);
            }
            catch { }
        }
    });

    await app.RunAsync();
}
