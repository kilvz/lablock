using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace LabLock.Server.Mcp;

public class McpApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _username;
    private readonly string _password;
    private string? _token;

    public McpApiClient()
    {
        _baseUrl = (Environment.GetEnvironmentVariable("LABLOCK_API_URL") ?? "http://127.0.0.1:5000").TrimEnd('/');
        _username = Environment.GetEnvironmentVariable("LABLOCK_API_USERNAME") ?? "admin";
        _password = Environment.GetEnvironmentVariable("LABLOCK_API_PASSWORD") ?? "admin";
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
    }

    private async Task<string> GetTokenAsync()
    {
        if (!string.IsNullOrEmpty(_token)) return _token;

        var loginResp = await _http.PostAsJsonAsync($"{_baseUrl}/api/auth/login",
            new { username = _username, password = _password });
        if (!loginResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"REST login failed: HTTP {(int)loginResp.StatusCode}");

        using var doc = JsonDocument.Parse(await loginResp.Content.ReadAsStringAsync());
        _token = doc.RootElement.GetProperty("token").GetString();
        return _token!;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null)
    {
        var token = await GetTokenAsync();
        var req = new HttpRequestMessage(method, $"{_baseUrl}{path}");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (body != null)
            req.Content = JsonContent.Create(body);
        return await _http.SendAsync(req);
    }

    public async Task<string> SendCommandAsync(string clientId, string command, int timeoutSec)
    {
        var resp = await SendAsync(HttpMethod.Post,
            $"/api/clients/{Uri.EscapeDataString(clientId)}/command/wait",
            new { command, timeoutSeconds = timeoutSec });
        return await ReadBodyOrThrowAsync(resp, "output");
    }

    public async Task<string> SendInteractiveAsync(string clientId, string action, object? parameters, int timeoutMs)
    {
        var body = new
        {
            action,
            parameters = parameters != null ? JsonSerializer.Serialize(parameters) : null,
            timeoutMs
        };

        var resp = await SendAsync(HttpMethod.Post,
            $"/api/clients/{Uri.EscapeDataString(clientId)}/interactive", body);
        return await ReadBodyOrThrowAsync(resp, "output");
    }

    public async Task<string> GetSystemInfoAsync(string clientId)
    {
        var resp = await SendAsync(HttpMethod.Get, $"/api/clients/{Uri.EscapeDataString(clientId)}/system-info");
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(await ErrorMessageAsync(resp));
        return await resp.Content.ReadAsStringAsync();
    }

    public async Task DeleteClientAsync(string clientId)
    {
        var resp = await SendAsync(HttpMethod.Delete, $"/api/clients/{Uri.EscapeDataString(clientId)}");
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(await ErrorMessageAsync(resp));
    }

    public async Task<string> PushUpdateAsync(string clientId)
    {
        var resp = await SendAsync(HttpMethod.Post, $"/api/clients/{Uri.EscapeDataString(clientId)}/update");
        return await ReadBodyOrThrowAsync(resp, null);
    }

    public async Task<string> GetUpdateStatusAsync()
    {
        var resp = await SendAsync(HttpMethod.Get, "/api/update/version");
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(await ErrorMessageAsync(resp));
        return await resp.Content.ReadAsStringAsync();
    }

    private static async Task<string> ReadBodyOrThrowAsync(HttpResponseMessage resp, string? prop)
    {
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(ParseError(body) ?? $"HTTP {(int)resp.StatusCode}");
        if (prop == null) return body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? "";
        }
        catch (JsonException) { }
        return body;
    }

    private static async Task<string> ErrorMessageAsync(HttpResponseMessage resp)
    {
        var body = await resp.Content.ReadAsStringAsync();
        return ParseError(body) ?? $"HTTP {(int)resp.StatusCode}";
    }

    private static string? ParseError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                return e.GetString();
        }
        catch (JsonException) { }
        return null;
    }

    public void Dispose() => _http.Dispose();
}
