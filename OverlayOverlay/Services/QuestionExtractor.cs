using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace OverlayOverlay.Services;

public class QuestionExtractor
{
    private readonly string _openAiApiKey;
    private readonly string _openAiModel;

    public QuestionExtractor(string openAiApiKey, string openAiModel = "gpt-4o-mini")
    {
        _openAiApiKey = openAiApiKey;
        _openAiModel = openAiModel;
    }

    public async Task<(bool isQuestion, string question)> ExtractAsync(string text, string[]? contextualKeywords = null)
    {
        // Filtro rápido (como en tu mock JS)
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 3 || trimmed.Length < 10)
            return (false, "");

        var prompt = BuildPrompt(trimmed, contextualKeywords);

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _openAiApiKey);

        var body = new
        {
            model = _openAiModel,
            messages = new object[]
            {
                new { role = "system", content = "You extract one interview question from noisy transcript, keep original language, output strict JSON only." },
                new { role = "user", content = prompt }
            },
            temperature = 0.2,
            response_format = new { type = "json_object" }
        };
        var json = JsonSerializer.Serialize(body);
        var resp = await http.PostAsync("https://api.openai.com/v1/chat/completions", new StringContent(json, Encoding.UTF8, "application/json"));
        var respText = await resp.Content.ReadAsStringAsync();
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(respText);
        var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(content)) return (false, "");

        // content is expected to be a JSON object
        using var j = JsonDocument.Parse(content);
        var isQ = j.RootElement.TryGetProperty("isQuestion", out var isQe) && isQe.GetBoolean();
        var q = j.RootElement.TryGetProperty("question", out var qe) ? qe.GetString() ?? "" : "";
        return (isQ, q);
    }

    private static string BuildPrompt(string text, string[]? contextualKeywords)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are an expert linguist specializing in interview question analysis.");
        sb.AppendLine("Extract a single clear question from a noisy transcript. Remove filler. Keep original language. Respond ONLY with JSON.");
        sb.AppendLine();
        sb.AppendLine("Text to analyze:");
        sb.AppendLine(text);
        if (contextualKeywords is { Length: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("Contextual Keywords:");
            foreach (var k in contextualKeywords)
                sb.AppendLine("- " + k);
        }
        sb.AppendLine();
        sb.AppendLine("Output JSON fields: isQuestion:boolean, question:string");
        return sb.ToString();
    }
}

