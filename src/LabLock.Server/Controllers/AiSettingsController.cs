using LabLock.Server.Data;
using LabLock.Server.Models;
using LabLock.Server.Services.Ai;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LabLock.Server.Controllers;

[ApiController]
[Route("api/ai/settings")]
[Authorize(Roles = "admin")]
public class AiSettingsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly AiProviderFactory _providerFactory;

    public AiSettingsController(AppDbContext db, AiProviderFactory providerFactory)
    {
        _db = db;
        _providerFactory = providerFactory;
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var settings = await _db.AiSettings.FindAsync(1);

        if (settings == null)
            return Ok(new
            {
                provider = "none",
                model = "",
                apiKey = "",
                baseUrl = "",
                temperature = 0.5,
                maxTokens = 4096,
                systemPrompt = ""
            });

        return Ok(new
        {
            settings.Provider,
            settings.Model,
            settings.ApiKey,
            settings.BaseUrl,
            settings.Temperature,
            settings.MaxTokens,
            settings.SystemPrompt
        });
    }

    [HttpPut]
    public async Task<IActionResult> Update([FromBody] AiSettingsDto dto)
    {
        var settings = await _db.AiSettings.FindAsync(1);

        if (settings == null)
        {
            settings = new AiSettings
            {
                Id = 1,
                Provider = dto.Provider,
                Model = dto.Model,
                ApiKey = dto.ApiKey ?? "",
                BaseUrl = dto.BaseUrl ?? "",
                Temperature = dto.Temperature,
                MaxTokens = dto.MaxTokens,
                SystemPrompt = dto.SystemPrompt ?? ""
            };
            _db.AiSettings.Add(settings);
        }
        else
        {
            settings.Provider = dto.Provider;
            settings.Model = dto.Model;
            settings.ApiKey = dto.ApiKey ?? settings.ApiKey;
            settings.BaseUrl = dto.BaseUrl ?? settings.BaseUrl;
            settings.Temperature = dto.Temperature;
            settings.MaxTokens = dto.MaxTokens;
            settings.SystemPrompt = dto.SystemPrompt ?? settings.SystemPrompt;
        }

        await _db.SaveChangesAsync();

        return Ok(new
        {
            settings.Provider,
            settings.Model,
            settings.Temperature,
            settings.MaxTokens,
            configured = !string.IsNullOrEmpty(settings.ApiKey) || settings.Provider == "ollama"
        });
    }

    [HttpGet("providers")]
    public IActionResult GetProviders()
    {
        var providers = new[]
        {
            new { id = "none", name = "None (Disabled)", models = Array.Empty<string>() },
            new { id = "openai", name = "OpenAI", models = _providerFactory.GetDefaultModels("openai") },
            new { id = "anthropic", name = "Anthropic", models = _providerFactory.GetDefaultModels("anthropic") },
            new { id = "gemini", name = "Google Gemini", models = _providerFactory.GetDefaultModels("gemini") },
            new { id = "ollama", name = "Ollama (Local)", models = _providerFactory.GetDefaultModels("ollama") },
            new { id = "custom", name = "Custom (OpenAI-compatible)", models = _providerFactory.GetDefaultModels("custom") }
        };

        return Ok(new { providers });
    }

    public record AiSettingsDto
    {
        public string Provider { get; set; } = "none";
        public string Model { get; set; } = "";
        public string? ApiKey { get; set; }
        public string? BaseUrl { get; set; }
        public double Temperature { get; set; } = 0.3;
        public int MaxTokens { get; set; } = 4096;
        public string? SystemPrompt { get; set; }
    }
}
