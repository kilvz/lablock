using LabLock.Client.Helpers;
using LabLock.Client.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args.Contains("--active-app-helper"))
{
    ActiveAppHelper.Run();
    return;
}

if (args.Contains("--session-agent"))
{
    SessionAgentWorker.Run();
    return;
}

try
{
    ProcessGuard.Harden();
}
catch
{
}

var builder = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options =>
    {
        options.ServiceName = "LabLockAgent";
    })
    .ConfigureLogging(logging =>
    {
        logging.AddConsole();
    })
    .ConfigureAppConfiguration((context, config) =>
    {
        config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
        config.AddEnvironmentVariables("LABLOCK_");
        config.AddCommandLine(args);
    })
    .ConfigureServices((context, services) =>
    {
        services.AddSingleton<SessionAgentService>();
        services.AddSingleton<LogBufferService>();
        services.AddSingleton<SessionContextService>();
        services.AddSingleton<SystemInfoService>();
        services.AddSingleton<PowerShellExecutorService>();
        services.AddSingleton<ServerDiscoveryService>();
        services.AddSingleton<ClientUpdateService>();
        services.AddHostedService<ConnectionService>();
        services.AddHostedService<ProcessMonitorService>();
        services.AddHostedService<WindowMonitorService>();
        services.AddSingleton<ActiveAppHelperService>();
        services.AddHostedService(sp => sp.GetRequiredService<ActiveAppHelperService>());
    });

await builder.Build().RunAsync();
