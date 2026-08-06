using LabLock.Server.Services.Ai.Providers;

namespace LabLock.Server.Services.Ai;

public class AiProviderFactory
{
    private readonly IHttpClientFactory _httpClientFactory;

    public AiProviderFactory(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public IAiProvider Create(string provider)
    {
        return provider.ToLowerInvariant() switch
        {
            "openai" => new OpenAiProvider(_httpClientFactory),
            "anthropic" => new AnthropicProvider(_httpClientFactory),
            "gemini" => new GeminiProvider(_httpClientFactory),
            "ollama" => new OllamaProvider(_httpClientFactory),
            "custom" => new OpenAiProvider(_httpClientFactory),
            _ => throw new ArgumentException($"Unknown AI provider: {provider}")
        };
    }

    public string GetDefaultBaseUrl(string provider)
    {
        return provider.ToLowerInvariant() switch
        {
            "openai" => "https://api.openai.com",
            "anthropic" => "https://api.anthropic.com",
            "gemini" => "https://generativelanguage.googleapis.com",
            "ollama" => "http://localhost:11434",
            "custom" => "",
            _ => ""
        };
    }

    public string[] GetDefaultModels(string provider)
    {
        return provider.ToLowerInvariant() switch
        {
            "openai" => ["gpt-4o", "gpt-4o-mini", "gpt-4-turbo", "gpt-3.5-turbo"],
            "anthropic" => ["claude-sonnet-4-20250514", "claude-3-5-haiku-20241022", "claude-opus-4-20250514"],
            "gemini" => ["gemini-2.5-pro", "gemini-2.5-flash", "gemini-2.0-flash"],
            "ollama" => [],
            "custom" => [],
            _ => []
        };
    }
}
