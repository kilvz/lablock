using System.Text.Json;
using LabLock.Server.Data;
using LabLock.Server.Models;
using LabLock.Server.Services.Ai;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LabLock.Server.Controllers;

[ApiController]
[Route("api/ai")]
[Authorize]
public class AiController : ControllerBase
{
    private readonly AiChatService _chatService;
    private readonly AppDbContext _db;

    public AiController(AiChatService chatService, AppDbContext db)
    {
        _chatService = chatService;
        _db = db;
    }

    [HttpPost("chat/{conversationId}")]
    public async Task Chat(
        string conversationId,
        [FromBody] JsonElement body,
        CancellationToken ct)
    {
        var message = body.GetProperty("message").GetString() ?? "";

        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";

        await foreach (var evt in _chatService.ChatAsync(conversationId, message, ct))
        {
            var json = JsonSerializer.Serialize(evt);
            await Response.WriteAsync($"data: {json}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }
    }

    [HttpPost("conversations")]
    public async Task<IActionResult> CreateConversation()
    {
        var conversation = new AiConversation
        {
            Title = "New conversation",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.AiConversations.Add(conversation);
        await _db.SaveChangesAsync();
        return Ok(new { conversation.Id });
    }

    [HttpGet("conversations")]
    public async Task<IActionResult> ListConversations()
    {
        var conversations = await _db.AiConversations
            .OrderByDescending(c => c.UpdatedAt)
            .Select(c => new { c.Id, c.Title, c.CreatedAt, c.UpdatedAt })
            .ToListAsync();

        return Ok(new { conversations });
    }

    [HttpGet("conversations/{id}")]
    public async Task<IActionResult> GetConversation(string id)
    {
        var conversation = await _db.AiConversations
            .Include(c => c.Messages)
            .FirstOrDefaultAsync(c => c.Id == id);

        if (conversation == null) return NotFound();

        return Ok(new
        {
            conversation.Id,
            conversation.Title,
            messages = conversation.Messages.OrderBy(m => m.Timestamp).ToList()
        });
    }

    [HttpDelete("conversations/{id}")]
    public async Task<IActionResult> DeleteConversation(string id)
    {
        var conversation = await _db.AiConversations
            .Include(c => c.Messages)
            .FirstOrDefaultAsync(c => c.Id == id);

        if (conversation == null) return NotFound();

        _db.AiMessages.RemoveRange(conversation.Messages);
        _db.AiConversations.Remove(conversation);
        await _db.SaveChangesAsync();

        return NoContent();
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        return Ok(new
        {
            configured = _chatService.IsConfigured(),
            config = _chatService.GetCurrentConfig()
        });
    }
}
