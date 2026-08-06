using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LabLock.Server.Services.Ai.Providers;

public class AnthropicProvider : IAiProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public AnthropicProvider(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async IAsyncEnumerable<AiStreamChunk> ChatStreamAsync(
        List<AiMessage> messages,
        List<AiToolDefinition> tools,
        AiConfig config,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("AiClient");
        var baseUrl = !string.IsNullOrEmpty(config.BaseUrl) ? config.BaseUrl.TrimEnd('/') : "https://api.anthropic.com";
        var model = !string.IsNullOrEmpty(config.Model) ? config.Model : "claude-sonnet-4-20250514";

        var systemPrompt = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";

        var apiMessages = messages
            .Where(m => m.Role != "system")
            .Select(m =>
            {
                if (m.Role == "tool")
                {
                    return new Dictionary<string, object>
                    {
                        ["role"] = "user",
                        ["content"] = new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["type"] = "tool_result",
                                ["tool_use_id"] = m.ToolCallId ?? "",
                                ["content"] = m.Content ?? ""
                            }
                        }
                    };
                }

                if (m.ToolCalls is { Count: > 0 })
                {
                    var content = new List<object>();
                    content.Add(new Dictionary<string, object>
                    {
                        ["type"] = "text",
                        ["text"] = m.Content ?? ""
                    });
                    foreach (var tc in m.ToolCalls)
                    {
                        content.Add(new Dictionary<string, object>
                        {
                            ["type"] = "tool_use",
                            ["id"] = tc.Id,
                            ["name"] = tc.Name,
                            ["input"] = tc.Arguments
                        });
                    }
                    return new Dictionary<string, object>
                    {
                        ["role"] = "assistant",
                        ["content"] = content
                    };
                }

                return new Dictionary<string, object>
                {
                    ["role"] = m.Role,
                    ["content"] = m.Content
                };
            })
            .ToList();

        var requestBody = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = apiMessages,
            ["max_tokens"] = config.MaxTokens,
            ["temperature"] = config.Temperature,
            ["stream"] = true
        };

        if (!string.IsNullOrEmpty(systemPrompt))
            requestBody["system"] = systemPrompt;

        if (tools.Count > 0)
        {
            requestBody["tools"] = tools.Select(t => new
            {
                name = t.Name,
                description = t.Description,
                input_schema = new
                {
                    type = "object",
                    properties = t.Parameters.ToDictionary(
                        p => p.Key,
                        p =>
                        {
                            var param = new Dictionary<string, object>
                            {
                                ["type"] = p.Value.Type,
                                ["description"] = p.Value.Description
                            };
                            if (p.Value.EnumValues is { Length: > 0 })
                                param["enum"] = p.Value.EnumValues;
                            return param;
                        }),
                    required = t.Required
                }
            }).ToList();
        }

        var json = JsonSerializer.Serialize(requestBody, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/messages");
        httpRequest.Content = content;
        httpRequest.Headers.Add("x-api-key", config.ApiKey);
        httpRequest.Headers.Add("anthropic-version", "2023-06-01");

        using var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrEmpty(line)) continue;
            if (!line.StartsWith("data: ")) continue;
            var data = line[6..];
            if (data == "[DONE]")
            {
                yield return new AiStreamChunk { IsComplete = true };
                yield break;
            }

            SseEvent? sseEvent;
            try
            {
                sseEvent = JsonSerializer.Deserialize<SseEvent>(data, JsonOptions);
            }
            catch
            {
                continue;
            }

            if (sseEvent == null) continue;

            if (sseEvent.Type == "content_block_delta" && sseEvent.Delta?.Text != null)
                yield return new AiStreamChunk { TextDelta = sseEvent.Delta.Text };

            if (sseEvent.Type == "content_block_start" && sseEvent.ContentBlock?.Type == "tool_use")
            {
                yield return new AiStreamChunk
                {
                    ToolCall = new AiToolCall
                    {
                        Id = sseEvent.ContentBlock.Id ?? "",
                        Name = sseEvent.ContentBlock.Name ?? ""
                    }
                };
            }

            if (sseEvent.Type == "content_block_delta" && sseEvent.Delta?.PartialJson != null)
            {
                Dictionary<string, object>? args = null;
                try
                {
                    args = JsonSerializer.Deserialize<Dictionary<string, object>>(sseEvent.Delta.PartialJson, JsonOptions);
                }
                catch { }

                if (args != null)
                {
                    yield return new AiStreamChunk
                    {
                        ToolCall = new AiToolCall
                        {
                            Name = "",
                            Arguments = args
                        }
                    };
                }
            }

            if (sseEvent.Type == "message_stop")
            {
                yield return new AiStreamChunk { IsComplete = true };
                yield break;
            }

            if (sseEvent.Type == "error")
            {
                yield return new AiStreamChunk { Error = sseEvent.Error?.Message ?? "Unknown Anthropic error" };
                yield break;
            }
        }
    }

    private class SseEvent
    {
        public string? Type { get; set; }
        public DeltaContent? Delta { get; set; }
        public ContentBlock? ContentBlock { get; set; }
        public AnthropicError? Error { get; set; }
    }

    private class DeltaContent
    {
        public string? Text { get; set; }
        [JsonPropertyName("partial_json")]
        public string? PartialJson { get; set; }
    }

    private class ContentBlock
    {
        public string? Type { get; set; }
        public string? Id { get; set; }
        public string? Name { get; set; }
    }

    private class AnthropicError
    {
        public string? Message { get; set; }
    }
}
