using LabLock.Server.Services;
using LabLock.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LabLock.Server.Controllers;

[ApiController]
[Route("api/logs")]
[Authorize]
public class LogsController : ControllerBase
{
    private readonly LogStorageService _logStorage;

    public LogsController(LogStorageService logStorage)
    {
        _logStorage = logStorage;
    }

    [HttpGet]
    public async Task<IActionResult> Query(
        [FromQuery] string clientId = "all",
        [FromQuery] string eventType = "all",
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        EventType? eventTypeFilter = eventType != "all"
            ? Enum.Parse<EventType>(eventType, true)
            : null;

        var (logs, total) = await _logStorage.QueryAsync(
            clientId, eventTypeFilter, from, to, search, page, Math.Min(pageSize, 500));

        return Ok(new { logs, total, page, pageSize });
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetStats(
        [FromQuery] string clientId = "all",
        [FromQuery] int hoursBack = 24)
    {
        var from = DateTime.UtcNow.AddHours(-hoursBack);
        var stats = await _logStorage.GetStatsAsync(clientId, from);
        return Ok(stats);
    }
}
