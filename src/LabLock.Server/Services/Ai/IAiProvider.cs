namespace LabLock.Server.Services.Ai;

public interface IAiProvider
{
    IAsyncEnumerable<AiStreamChunk> ChatStreamAsync(
        List<AiMessage> messages,
        List<AiToolDefinition> tools,
        AiConfig config,
        CancellationToken ct);
}

public class AiStreamChunk
{
    public string? TextDelta { get; set; }
    public AiToolCall? ToolCall { get; set; }
    public bool IsComplete { get; set; }
    public string? Error { get; set; }
}

public class AiToolCall
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public Dictionary<string, object> Arguments { get; set; } = new();
}

public class AiToolDefinition
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public Dictionary<string, AiToolParameter> Parameters { get; set; } = new();
    public List<string> Required { get; set; } = new();
}

public class AiToolParameter
{
    public string Type { get; set; } = "string";
    public string Description { get; set; } = "";
    public string[]? EnumValues { get; set; }
}

public class AiConfig
{
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public double Temperature { get; set; } = 0.3;
    public int MaxTokens { get; set; } = 4096;
}

public class AiMessage
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
    public List<AiToolCall>? ToolCalls { get; set; }
    public string? ToolCallId { get; set; }
}

public class AiChatStreamEvent
{
    public string Type { get; set; } = "";
    public string? Content { get; set; }
    public string? ToolName { get; set; }
    public string? ToolArgs { get; set; }
}
