using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LabLock.Server.Services.Ai.Providers;

public class GeminiProvider : IAiProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public GeminiProvider(IHttpClientFactory httpClientFactory)
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
        var baseUrl = !string.IsNullOrEmpty(config.BaseUrl)
            ? config.BaseUrl.TrimEnd('/')
            : "https://generativelanguage.googleapis.com";
        var model = !string.IsNullOrEmpty(config.Model) ? config.Model : "gemini-2.5-pro";

        var systemPrompt = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";

        var geminiContents = new List<object>();
        foreach (var m in messages.Where(x => x.Role != "system"))
        {
            if (m.Role == "tool")
                continue;

            geminiContents.Add(new
            {
                role = m.Role == "assistant" ? "model" : "user",
                parts = new List<object>
                {
                    new { text = m.Content ?? "" }
                }
            });
        }

        var requestBody = new Dictionary<string, object>
        {
            ["systemInstruction"] = new Dictionary<string, object>
            {
                ["parts"] = new List<object> { new { text = systemPrompt } }
            },
            ["contents"] = geminiContents,
            ["generationConfig"] = new
            {
                temperature = config.Temperature,
                maxOutputTokens = config.MaxTokens
 }
        };

        if (tools.Count > 0)
        {
            requestBody["tools"] = new List<object>
            {
                new
                {
                    functionDeclarations = tools.Select(t => new
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
                    }).ToList()
                }
            };
        }

        var json = JsonSerializer.Serialize(requestBody, JsonOptions);
        var apiKey = config.ApiKey;
        var url = $"{baseUrl}/v1beta/models/{model}:streamGenerateContent?key={apiKey}&alt=sse";

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrEmpty(line)) continue;

            GeminiResponse? geminiResp;
            try
            {
                geminiResp = JsonSerializer.Deserialize<GeminiResponse>(line, JsonOptions);
            }
            catch
            {
                continue;
            }

            if (geminiResp?.Candidates == null || geminiResp.Candidates.Count == 0) continue;

            var candidate = geminiResp.Candidates[0];
            if (candidate.Content?.Parts == null) continue;

            foreach (var part in candidate.Content.Parts)
            {
                if (!string.IsNullOrEmpty(part.Text))
                    yield return new AiStreamChunk { TextDelta = part.Text };

                if (part.FunctionCall != null)
                {
                    yield return new AiStreamChunk
                    {
                        ToolCall = new AiToolCall
                        {
                            Id = $"fc_{Guid.NewGuid():N}",
                            Name = part.FunctionCall.Name ?? "",
                            Arguments = part.FunctionCall.Args != null
                                ? JsonSerializer.Deserialize<Dictionary<string, object>>(part.FunctionCall.Args.Value.GetRawText(), JsonOptions) ?? new()
                                : new()
                        }
                    };
                }
            }

            if (candidate.FinishReason is "STOP" or "stop")
            {
                yield return new AiStreamChunk { IsComplete = true };
                yield break;
            }
        }
    }

    private class GeminiResponse
    {
        public List<GeminiCandidate>? Candidates { get; set; }
    }

    private class GeminiCandidate
    {
        public GeminiContent? Content { get; set; }
        [JsonPropertyName("finishReason")]
        public string? FinishReason { get; set; }
    }

    private class GeminiContent
    {
        public string? Role { get; set; }
        public List<GeminiPart>? Parts { get; set; }
    }

    private class GeminiPart
    {
        public string? Text { get; set; }
        [JsonPropertyName("functionCall")]
        public GeminiFunctionCall? FunctionCall { get; set; }
    }

    private class GeminiFunctionCall
    {
        public string? Name { get; set; }
        public JsonElement? Args { get; set; }
    }
}
