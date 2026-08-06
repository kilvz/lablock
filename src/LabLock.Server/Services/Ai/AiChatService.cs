using System.Runtime.CompilerServices;
using System.Text.Json;
using LabLock.Server.Data;
using LabLock.Server.Services.Ai.Tools;
using Microsoft.EntityFrameworkCore;
using AiMessageEntity = LabLock.Server.Models.AiMessage;
using AiConversationEntity = LabLock.Server.Models.AiConversation;

namespace LabLock.Server.Services.Ai;

public class AiChatService
{
    private readonly AiProviderFactory _providerFactory;
    private readonly AiToolExecutor _toolExecutor;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ClientStateService _clientState;
    private readonly IConfiguration _config;

    private const string DefaultSystemPrompt = """
You are LabLock AI, an intelligent assistant for managing a network of Windows lab PCs.

Your capabilities:
- Execute PowerShell commands on any connected PC or all PCs at once
- View real-time status of all PCs (online/offline, current user, active app)
- Retrieve detailed system information (hardware, software, processes)
- Search and analyze activity logs (keystrokes, process usage, window focus)
- Review command execution history

Guidelines:
- Be concise and helpful in your responses
- Format data as tables when showing multiple items
- When asked to install software, use appropriate package managers (winget, chocolatey, or direct download)
- ALWAYS confirm with the user before executing destructive commands (shutdown, restart, format, delete system files, uninstall)
- When a user says "all PCs" or "every PC", use the execute_command_all tool
- Show command output in code blocks for readability
- If a command fails, analyze the error and suggest fixes
- For security-sensitive operations, warn the user about implications
- Use list_clients first when you need to find specific PCs
- Keep responses focused and actionable
""";

    public AiChatService(
        AiProviderFactory providerFactory,
        AiToolExecutor toolExecutor,
        IServiceScopeFactory scopeFactory,
        ClientStateService clientState,
        IConfiguration config)
    {
        _providerFactory = providerFactory;
        _toolExecutor = toolExecutor;
        _scopeFactory = scopeFactory;
        _clientState = clientState;
        _config = config;
    }

    public AiConfig GetCurrentConfig()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = db.AiSettings.Find(1);

        if (settings != null)
        {
            return new AiConfig
            {
                Provider = settings.Provider,
                Model = settings.Model,
                ApiKey = settings.ApiKey,
                BaseUrl = settings.BaseUrl,
                Temperature = settings.Temperature,
                MaxTokens = settings.MaxTokens
            };
        }

        return new AiConfig
        {
            Provider = _config["Ai:Provider"] ?? "none",
            Model = _config["Ai:Model"] ?? "",
            ApiKey = _config["Ai:ApiKey"] ?? "",
            BaseUrl = _config["Ai:BaseUrl"] ?? "",
            Temperature = double.TryParse(_config["Ai:Temperature"], out var t) ? t : 0.3,
            MaxTokens = int.TryParse(_config["Ai:MaxTokens"], out var m) ? m : 4096
        };
    }

    public bool IsConfigured()
    {
        var config = GetCurrentConfig();
        return config.Provider is not "none" and not ""
            && (config.ApiKey != "" || config.Provider == "ollama");
    }

    public string BuildSystemPrompt()
    {
        var config = GetCurrentConfig();
        var systemPrompt = config.Provider != "none"
            ? GetStoredSystemPrompt()
            : DefaultSystemPrompt;

        systemPrompt += "\n\n--- Current Lab Status ---\n";
        systemPrompt += $"Total PCs: {_clientState.TotalCount}\n";
        systemPrompt += $"Online: {_clientState.OnlineCount}\n";
        systemPrompt += $"Offline: {_clientState.TotalCount - _clientState.OnlineCount}\n";
        systemPrompt += $"Current time (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}\n";
        systemPrompt += "\nOnline PCs:\n";

        foreach (var client in _clientState.GetAllClients().Where(c => c.IsOnline))
            systemPrompt += $"- {client.ClientId} (user: {client.CurrentUser}, app: {client.ActiveProcess}, CPU: {client.CpuPercent}%)\n";

        return systemPrompt;
    }

    private string GetStoredSystemPrompt()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = db.AiSettings.Find(1);
        return settings?.SystemPrompt ?? DefaultSystemPrompt;
    }

    public async IAsyncEnumerable<AiChatStreamEvent> ChatAsync(
        string conversationId, string userMessage, [EnumeratorCancellation] CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var conversation = await db.AiConversations
            .Include(c => c.Messages)
            .FirstOrDefaultAsync(c => c.Id == conversationId, ct);

        if (conversation == null)
        {
            conversation = new AiConversationEntity { Id = conversationId };
            db.AiConversations.Add(conversation);
        }

        db.AiMessages.Add(new AiMessageEntity
        {
            ConversationId = conversationId,
            Role = "user",
            Content = userMessage
        });

        await db.SaveChangesAsync(ct);

        var messages = new List<AiMessage>
        {
            new() { Role = "system", Content = BuildSystemPrompt() }
        };

        messages.AddRange(conversation.Messages
            .OrderBy(m => m.Timestamp)
            .Select(m => new AiMessage
            {
                Role = m.Role,
                Content = m.Content,
                ToolCalls = m.ToolCallsJson != null
                    ? JsonSerializer.Deserialize<List<AiToolCall>>(m.ToolCallsJson)
                    : null,
                ToolCallId = m.ToolCallId
            }));

        messages.Add(new AiMessage { Role = "user", Content = userMessage });

        var tools = ToolDefinitions.GetAll();
        var config = GetCurrentConfig();
        var provider = _providerFactory.Create(config.Provider);

        var assistantContent = new System.Text.StringBuilder();
        var toolCalls = new List<AiToolCall>();
        var iterationCount = 0;
        const int maxIterations = 10;

        while (iterationCount < maxIterations)
        {
            iterationCount++;
            assistantContent.Clear();
            toolCalls.Clear();

            await foreach (var chunk in provider.ChatStreamAsync(messages, tools, config, ct))
            {
                if (chunk.Error != null)
                {
                    yield return new AiChatStreamEvent { Type = "error", Content = chunk.Error };
                    yield break;
                }

                if (chunk.TextDelta != null)
                {
                    assistantContent.Append(chunk.TextDelta);
                    yield return new AiChatStreamEvent { Type = "text", Content = chunk.TextDelta };
                }

                if (chunk.ToolCall != null)
                {
                    toolCalls.Add(chunk.ToolCall);
                    yield return new AiChatStreamEvent
                    {
                        Type = "tool_call",
                        ToolName = chunk.ToolCall.Name,
                        ToolArgs = JsonSerializer.Serialize(chunk.ToolCall.Arguments)
                    };
                }
            }

            if (toolCalls.Count == 0)
            {
                db.AiMessages.Add(new AiMessageEntity
                {
                    ConversationId = conversationId,
                    Role = "assistant",
                    Content = assistantContent.ToString()
                });

                conversation.UpdatedAt = DateTime.UtcNow;

                if (string.IsNullOrEmpty(conversation.Title))
                    conversation.Title = userMessage.Length > 50
                        ? userMessage[..50]
                        : userMessage;

                await db.SaveChangesAsync(ct);
                yield return new AiChatStreamEvent { Type = "done" };
                yield break;
            }

            messages.Add(new AiMessage
            {
                Role = "assistant",
                Content = assistantContent.ToString(),
                ToolCalls = toolCalls
            });

            foreach (var toolCall in toolCalls)
            {
                yield return new AiChatStreamEvent
                {
                    Type = "tool_executing",
                    ToolName = toolCall.Name
                };

                string result;
                try
                {
                    result = await _toolExecutor.ExecuteToolAsync(toolCall, ct);
                }
                catch (Exception ex)
                {
                    result = $"Tool execution failed: {ex.Message}";
                }

                yield return new AiChatStreamEvent
                {
                    Type = "tool_result",
                    ToolName = toolCall.Name,
                    Content = result
                };

                messages.Add(new AiMessage
                {
                    Role = "tool",
                    ToolCallId = toolCall.Id,
                    Content = result
                });
            }
        }

        yield return new AiChatStreamEvent
        {
            Type = "error",
            Content = "Maximum tool call iterations exceeded. Please try a simpler request."
        };
    }
}
