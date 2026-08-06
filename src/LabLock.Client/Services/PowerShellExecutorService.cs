using System.Diagnostics;
using LabLock.Shared.Models;
using Microsoft.Extensions.Logging;

namespace LabLock.Client.Services;

public class PowerShellExecutorService
{
    private readonly ILogger<PowerShellExecutorService> _logger;
    private readonly SessionContextService _sessionContext;
    private readonly string _workingDirectory;

    public PowerShellExecutorService(ILogger<PowerShellExecutorService> logger, SessionContextService sessionContext)
    {
        _logger = logger;
        _sessionContext = sessionContext;
        _workingDirectory = Environment.CurrentDirectory;
    }

    public async Task<CommandResultDto> ExecuteAsync(string command, int timeoutSeconds = 60, CancellationToken ct = default)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var startedAt = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var context = BuildContext(startedAt);

        try
        {
            var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -Command \"{command.Replace("\"", "\\\"")}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            var readTask = Task.WhenAll(
                process.StandardOutput.ReadToEndAsync(ct).ContinueWith(t => { if (t.IsCompletedSuccessfully) stdout.Write(t.Result); }),
                process.StandardError.ReadToEndAsync(ct).ContinueWith(t => { if (t.IsCompletedSuccessfully) stderr.Write(t.Result); })
            );

            var waitTask = process.WaitForExitAsync(cts.Token);

            await Task.WhenAny(waitTask, Task.Delay(timeoutSeconds * 1000 + 1000, ct));

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                var result = new CommandResultDto
                {
                    Output = stdout.ToString(),
                    Error = "Command timed out",
                    ExitCode = -1
                };
                return ApplyContext(result, startedAt, stopwatch, context, _workingDirectory);
            }

            await readTask;

            var success = new CommandResultDto
            {
                Output = stdout.ToString(),
                Error = stderr.ToString(),
                ExitCode = process.ExitCode
            };
            return ApplyContext(success, startedAt, stopwatch, context, _workingDirectory);
        }
        catch (OperationCanceledException)
        {
            var cancelled = new CommandResultDto
            {
                Output = stdout.ToString(),
                Error = "Command was cancelled",
                ExitCode = -1
            };
            return ApplyContext(cancelled, startedAt, stopwatch, context, _workingDirectory);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PowerShell execution failed");
            var failed = new CommandResultDto
            {
                Output = stdout.ToString(),
                Error = ex.Message,
                ExitCode = -1
            };
            return ApplyContext(failed, startedAt, stopwatch, context, _workingDirectory);
        }
    }

    public async Task<string> ExecuteSimpleAsync(string command)
    {
        var result = await ExecuteAsync(command, 60);
        if (!string.IsNullOrEmpty(result.Error))
            return $"OUTPUT:\n{result.Output}\n\nERROR:\n{result.Error}";
        return result.Output;
    }

    private (int sessionId, int interactiveSessionId, string interactiveUser, string runAsUser, string osVersion) BuildContext(DateTime startedAt)
    {
        return (
            _sessionContext.AgentSessionId,
            _sessionContext.GetInteractiveSessionId(),
            _sessionContext.GetInteractiveUser(),
            _sessionContext.RunAsUser,
            _sessionContext.OsVersion
        );
    }

    private static CommandResultDto ApplyContext(
        CommandResultDto result, DateTime startedAt, Stopwatch stopwatch,
        (int sessionId, int interactiveSessionId, string interactiveUser, string runAsUser, string osVersion) context,
        string workingDirectory)
    {
        stopwatch.Stop();
        result.SessionId = context.sessionId;
        result.InteractiveSessionId = context.interactiveSessionId;
        result.InteractiveUser = context.interactiveUser;
        result.RunAsUser = context.runAsUser;
        result.OsVersion = context.osVersion;
        result.WorkingDirectory = workingDirectory;
        result.ExecutedAt = startedAt;
        result.DurationMs = stopwatch.ElapsedMilliseconds;
        return result;
    }
}
