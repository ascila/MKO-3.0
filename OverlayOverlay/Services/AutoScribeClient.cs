using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace OverlayOverlay.Services;

public class AutoScribeClient
{
    private readonly HttpClient _httpClient = new HttpClient();
    private readonly string _apiUrl;
    private readonly string _apiKey;

    public AutoScribeClient(string apiUrl, string apiKey)
    {
        _apiUrl = apiUrl?.TrimEnd('/') ?? string.Empty;
        _apiKey = apiKey ?? string.Empty;
    }

    public async Task SendQuestionAsync(string questionText, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(_apiUrl)) throw new InvalidOperationException("API URL is not configured.");
        if (string.IsNullOrWhiteSpace(_apiKey)) throw new InvalidOperationException("API key is not configured.");
        if (string.IsNullOrWhiteSpace(questionText)) throw new ArgumentException("Question text is empty.");

        var url = _apiUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? _apiUrl
            : $"http://{_apiUrl}";

        var body = new { text = questionText, sessionId = sessionId };
        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("x-api-key", _apiKey);

        using var resp = await _httpClient.SendAsync(req);
        resp.EnsureSuccessStatusCode();
    }
}

