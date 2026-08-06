using LabLock.Server.Services;
using LabLock.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LabLock.Server.Controllers;

[ApiController]
[Route("api/commands")]
[Authorize]
public class CommandsController : ControllerBase
{
    private readonly CommandService _commandService;
    private readonly ILogger<CommandsController> _logger;

    public CommandsController(CommandService commandService, ILogger<CommandsController> logger)
    {
        _commandService = commandService;
        _logger = logger;
    }

    [HttpPost("execute")]
    public async Task<IActionResult> ExecuteServerSide([FromBody] CommandRequestDto request)
    {
        var output = "";
        int? exitCode = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(request.TimeoutSeconds > 0 ? request.TimeoutSeconds : 60));
            var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe", $"-NoProfile -Command \"{request.Command.Replace("\"", "\\\"")}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var proc = System.Diagnostics.Process.Start(psi);
            if (proc != null)
            {
                var stdout = await proc.StandardOutput.ReadToEndAsync(cts.Token);
                var stderr = await proc.StandardError.ReadToEndAsync(cts.Token);
                await proc.WaitForExitAsync(cts.Token);
                exitCode = proc.ExitCode;
                output = string.IsNullOrEmpty(stderr) ? stdout : $"{stdout}\nSTDERR:\n{stderr}";
            }
        }
        catch (OperationCanceledException)
        {
            output = "ERROR: Command timed out";
        }
        catch (Exception ex)
        {
            output = $"ERROR: {ex.Message}";
        }

        return Ok(new { output, exitCode });
    }

    [HttpGet]
    public async Task<IActionResult> GetHistory(
        [FromQuery] string clientId = "all",
        [FromQuery] int limit = 50,
        [FromQuery] int page = 1)
    {
        if (clientId != "all")
        {
            var history = await _commandService.GetHistoryAsync(clientId, limit);
            return Ok(new { history, total = history.Count });
        }

        return Ok(new { history = new List<object>(), total = 0 });
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id)
    {
        var command = await _commandService.GetByIdAsync(id);
        if (command == null) return NotFound();
        return Ok(command);
    }
}
