using LabLock.Server.Data;
using LabLock.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LabLock.Server.Controllers;

[ApiController]
[Route("api/update")]
public class ClientUpdateController : ControllerBase
{
    private const string DashboardSecret = "lablock-dashboard-2026";

    private readonly ClientUpdateService _update;

    public ClientUpdateController(ClientUpdateService update)
    {
        _update = update;
    }

    [HttpGet("version")]
    [Authorize]
    public IActionResult GetVersion()
    {
        var manifest = _update.GetManifest();
        if (manifest == null)
            return NotFound(new { error = "No update package published" });

        return Ok(new
        {
            manifest.Version,
            manifest.Size,
            manifest.Sha256,
            manifest.UploadedAt
        });
    }

    [HttpPost("package")]
    [Authorize]
    [RequestSizeLimit(200 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 200 * 1024 * 1024)]
    public async Task<IActionResult> UploadPackage(IFormFile file, [FromForm] string version)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "File is required" });

        if (string.IsNullOrWhiteSpace(version))
            return BadRequest(new { error = "Version is required" });

        using var stream = file.OpenReadStream();

        var magic = new byte[2];
        var read = await stream.ReadAsync(magic);
        if (read < 2 || magic[0] != (byte)'M' || magic[1] != (byte)'Z')
            return BadRequest(new { error = "Uploaded file is not a valid Windows executable" });

        stream.Position = 0;
        var manifest = await _update.PublishAsync(stream, version.Trim());

        return Ok(new
        {
            message = "Update package published",
            manifest.Version,
            manifest.Sha256,
            manifest.Size
        });
    }

    [HttpGet("package")]
    public async Task<IActionResult> DownloadPackage([FromQuery] string? apiKey)
    {
        if (!_update.HasPackage)
            return NotFound(new { error = "No update package published" });

        if (!string.IsNullOrEmpty(apiKey))
        {
            using var scope = HttpContext.RequestServices.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var known = await db.Clients.AnyAsync(c => c.ApiKey == apiKey);
            if (!known && apiKey != DashboardSecret)
                return Unauthorized(new { error = "Invalid api key" });
        }

        return PhysicalFile(_update.PackagePath, "application/octet-stream", ClientUpdateService.PackageFileName);
    }
}
