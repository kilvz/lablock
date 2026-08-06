using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LabLock.Server.Services.Ai.Providers;

public class OllamaProvider : IAiProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public OllamaProvider(IHttpClientFactory httpClientFactory)
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
        var baseUrl = !string.IsNullOrEmpty(config.BaseUrl) ? config.BaseUrl.TrimEnd('/') : "http://localhost:11434";
        var model = !string.IsNullOrEmpty(config.Model) ? config.Model : "llama3.2";

        var ollamaMessages = messages.Select(m =>
        {
            var dict = new Dictionary<string, object>
            {
                ["role"] = m.Role == "tool" ? "tool" : m.Role
            };

            if (m.Role == "tool")
            {
                dict["content"] = m.Content;
            }
            else if (m.ToolCalls is { Count: > 0 })
            {
                dict["content"] = m.Content ?? "";
                dict["tool_calls"] = m.ToolCalls.Select(tc => new Dictionary<string, object>
                {
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object>
                    {
                        ["name"] = tc.Name,
                        ["arguments"] = tc.Arguments
                    }
                }).ToList();
            }
            else
            {
                dict["content"] = m.Content;
            }

            return dict;
        }).ToList();

        var requestBody = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = ollamaMessages,
            ["stream"] = true,
            ["options"] = new Dictionary<string, object>
            {
                ["temperature"] = config.Temperature,
                ["num_predict"] = config.MaxTokens
            }
        };

        if (tools.Count > 0)
        {
            requestBody["tools"] = tools.Select(t => new
            {
                type = "function",
                function = new
                {
                    name = t.Name,
                    description = t.Description,
                    parameters = new
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
                }
            }).ToList();
        }

        var json = JsonSerializer.Serialize(requestBody, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/chat")
            {
                Content = content
            },
            HttpCompletionOption.ResponseHeadersRead, ct);

        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrEmpty(line)) continue;

            OllamaResponse? ollamaResp;
            try
            {
                ollamaResp = JsonSerializer.Deserialize<OllamaResponse>(line, JsonOptions);
            }
            catch
            {
                continue;
            }

            if (ollamaResp == null) continue;

            if (ollamaResp.Done)
            {
                yield return new AiStreamChunk { IsComplete = true };
                yield break;
            }

            if (!string.IsNullOrEmpty(ollamaResp.Message?.Content))
                yield return new AiStreamChunk { TextDelta = ollamaResp.Message.Content };

            if (ollamaResp.Message?.ToolCalls != null)
            {
                foreach (var tc in ollamaResp.Message.ToolCalls)
                {
                    yield return new AiStreamChunk
                    {
                        ToolCall = new AiToolCall
                        {
                            Id = Guid.NewGuid().ToString("N")[..12],
                            Name = tc.Function?.Name ?? "",
                            Arguments = tc.Function?.Arguments ?? new()
                        }
                    };
                }
            }
        }
    }

    private class OllamaResponse
    {
        public string? Model { get; set; }
        public DateTime? CreatedAt { get; set; }
        public OllamaMessage? Message { get; set; }
        public bool Done { get; set; }
    }

    private class OllamaMessage
    {
        public string? Role { get; set; }
        public string? Content { get; set; }
        public List<OllamaToolCall>? ToolCalls { get; set; }
    }

    private class OllamaToolCall
    {
        public string? Type { get; set; }
        public OllamaFunction? Function { get; set; }
    }

    private class OllamaFunction
    {
        public string? Name { get; set; }
        public Dictionary<string, object>? Arguments { get; set; }
    }
}
