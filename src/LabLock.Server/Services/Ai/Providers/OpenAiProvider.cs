using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LabLock.Server.Services.Ai.Providers;

public class OpenAiProvider : IAiProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public OpenAiProvider(IHttpClientFactory httpClientFactory)
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
        var baseUrl = !string.IsNullOrEmpty(config.BaseUrl) ? config.BaseUrl.TrimEnd('/') : "https://api.openai.com";
        var model = !string.IsNullOrEmpty(config.Model) ? config.Model : "gpt-4o";

        var requestMessages = messages.Select(m =>
        {
            var msg = new Dictionary<string, object>
            {
                ["role"] = m.Role
            };

            if (m.ToolCalls is { Count: > 0 })
            {
                msg["content"] = m.Content ?? "";
                msg["tool_calls"] = m.ToolCalls.Select(tc => new
                {
                    id = tc.Id,
                    type = "function",
                    function = new { name = tc.Name, arguments = JsonSerializer.Serialize(tc.Arguments, JsonOptions) }
                }).ToList();
            }
            else if (m.Role == "tool")
            {
                msg["content"] = m.Content ?? "";
                msg["tool_call_id"] = m.ToolCallId ?? "";
            }
            else
            {
                msg["content"] = m.Content;
            }

            return msg;
        }).ToList();

        var requestBody = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = requestMessages,
            ["temperature"] = config.Temperature,
            ["max_tokens"] = config.MaxTokens,
            ["stream"] = true
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

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/chat/completions");
        httpRequest.Content = content;
        if (!string.IsNullOrEmpty(config.ApiKey))
            httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.ApiKey);

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

            DeltaResponse? delta;
            try
            {
                delta = JsonSerializer.Deserialize<DeltaResponse>(data, JsonOptions);
            }
            catch
            {
                continue;
            }

            if (delta?.Choices == null || delta.Choices.Count == 0) continue;
            var choice = delta.Choices[0];

            if (choice.FinishReason == "tool_calls")
            {
                yield return new AiStreamChunk { IsComplete = true };
                yield break;
            }

            if (choice.FinishReason == "stop")
            {
                yield return new AiStreamChunk { IsComplete = true };
                yield break;
            }

            if (choice.Delta?.ToolCalls != null)
            {
                foreach (var tc in choice.Delta.ToolCalls)
                {
                    yield return new AiStreamChunk
                    {
                        ToolCall = new AiToolCall
                        {
                            Id = tc.Id ?? "",
                            Name = tc.Function?.Name ?? "",
                            Arguments = !string.IsNullOrEmpty(tc.Function?.Arguments)
                                ? JsonSerializer.Deserialize<Dictionary<string, object>>(tc.Function.Arguments) ?? new()
                                : new()
                        }
                    };
                }
            }

            if (!string.IsNullOrEmpty(choice.Delta?.Content))
                yield return new AiStreamChunk { TextDelta = choice.Delta.Content };
        }
    }

    private class DeltaResponse
    {
        public List<DeltaChoice>? Choices { get; set; }
    }

    private class DeltaChoice
    {
        public DeltaMessage? Delta { get; set; }
        [JsonPropertyName("finish_reason")]
        public string? FinishReason { get; set; }
    }

    private class DeltaMessage
    {
        public string? Content { get; set; }
        public string? Role { get; set; }
        public List<DeltaToolCall>? ToolCalls { get; set; }
    }

    private class DeltaToolCall
    {
        public string? Id { get; set; }
        public string? Type { get; set; }
        public DeltaFunction? Function { get; set; }
    }

    private class DeltaFunction
    {
        public string? Name { get; set; }
        public string? Arguments { get; set; }
    }
}
