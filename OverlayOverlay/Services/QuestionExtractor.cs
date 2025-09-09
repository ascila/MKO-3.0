using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using OverlayOverlay.Models;
using OverlayOverlay.Services;

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
        // Prefiltro rápido
        var trimmed = (text ?? string.Empty).Trim();
        var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 3 || trimmed.Length < 10)
            return (false, "");
        // If contextual keywords are not provided, derive from SetupContext
        if (contextualKeywords == null || contextualKeywords.Length == 0)
        {
            try
            {
                var ctx = ContextProvider.Get();
                contextualKeywords = BuildKeywordsFromContext(ctx);
            }
            catch { contextualKeywords = Array.Empty<string>(); }
        }
        // Heurístico adicional: si no hay '?' ni interrogativos, requerir más contexto
        if (!trimmed.Contains("?"))
        {
            var lower = trimmed.ToLowerInvariant();
            string[] cues = new[] {
                // EN
                "who","what","why","how","when","where","can","could","would","should","do ","does","did",
                // ES
                "qué","que ","cómo","como ","cuándo","cuando ","dónde","donde ","por qué","por que","puedes","podrías","podrias","harías","harias"
            };
            bool hasCue = false;
            foreach (var c in cues) { if (lower.Contains(c)) { hasCue = true; break; } }
            if (!hasCue && words.Length < 5)
                return (false, "");
        }

        var prompt = BuildPrompt(trimmed, contextualKeywords);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
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

        var uri = "https://api.openai.com/v1/chat/completions";
        HttpResponseMessage resp = null!;
        string respText = string.Empty;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            resp = await http.PostAsync(uri, new StringContent(json, Encoding.UTF8, "application/json"));
            respText = await resp.Content.ReadAsStringAsync();
            if ((int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500)
            {
                await Task.Delay(600);
                continue;
            }
            break;
        }
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(respText);
        var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(content)) return (false, "");

        using var j = JsonDocument.Parse(content);
        var isQ = j.RootElement.TryGetProperty("isQuestion", out var isQe) && isQe.GetBoolean();
        var q = j.RootElement.TryGetProperty("question", out var qe) ? qe.GetString() ?? "" : "";
        return (isQ, q);
    }

    private static string BuildPrompt(string text, string[]? contextualKeywords)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are an expert linguist specializing in interview question analysis.");
        sb.AppendLine("Your primary task is to extract a single, clear, and concise question from a potentially noisy block of transcribed text. You must synthesize the core inquiry, removing filler words, conversational fluff, and redundant phrases. The final output must be a well-formed question.");
        sb.AppendLine();
        sb.AppendLine("CRITICAL INSTRUCTION #1: DO NOT TRANSLATE. You MUST preserve the original language of the text. If the input is in Spanish, the output MUST be in Spanish. If it is in English, the output MUST be in English.");
        sb.AppendLine("CRITICAL INSTRUCTION #2: Use the provided \"contextualKeywords\" to correct transcription errors for technical terms, names, or specific concepts.");
        sb.AppendLine("CRITICAL INSTRUCTION #3: If multiple questions are present, return only the most complete and relevant one. Do not merge or invent content that is not present in the text.");
        sb.AppendLine("CRITICAL INSTRUCTION #4: Respond ONLY with a valid JSON object. Do not include explanations, commentary, or text outside the JSON.");
        sb.AppendLine();
        sb.AppendLine("Additional constraint: if isQuestion is true, ensure the question ends with a single question mark (?) and do not wrap it in quotes.");
        sb.AppendLine();
        sb.AppendLine("Text to analyze:");
        sb.AppendLine("\"\"\"");
        sb.AppendLine(text);
        sb.AppendLine("\"\"\"");
        if (contextualKeywords is { Length: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("Contextual Keywords to use for correction:");
            foreach (var k in contextualKeywords)
                sb.AppendLine("- " + k);
        }
        sb.AppendLine();
        sb.AppendLine("EXAMPLE 1 (English with Keyword Correction):");
        sb.AppendLine("- Text: \"tell me about your experience with pavor bay\"");
        sb.AppendLine("- Contextual Keywords: [\"Power BI\", \"SQL\", \"Python\"]");
        sb.AppendLine("- Output: {\"isQuestion\": true, \"question\": \"Tell me about your experience with Power BI?\"}");
        sb.AppendLine();
        sb.AppendLine("EXAMPLE 2 (Spanish without correction):");
        sb.AppendLine("- Text: \"ok entonces... estaba pensando... cuéntame de una vez que tuviste que, ya sabes, liderar un proyecto importante?\"");
        sb.AppendLine("- Contextual Keywords: []");
        sb.AppendLine("- Output: {\"isQuestion\": true, \"question\": \"Cuéntame de una vez que tuviste que liderar un proyecto importante?\"}");
        sb.AppendLine();
        sb.AppendLine("EXAMPLE 3 (English, No Question):");
        sb.AppendLine("- Text: \"yes, that totally makes sense, thank you\"");
        sb.AppendLine("- Contextual Keywords: [\"React\", \"JavaScript\"]");
        sb.AppendLine("- Output: {\"isQuestion\": false, \"question\": \"\"}");
        sb.AppendLine();
        sb.AppendLine("EXAMPLE 4 (Spanish, No Question):");
        sb.AppendLine("- Text: \"si, eso tiene todo el sentido, gracias\"");
        sb.AppendLine("- Contextual Keywords: []");
        sb.AppendLine("- Output: {\"isQuestion\": false, \"question\": \"\"}");
        return sb.ToString();
    }

    private static string[] BuildKeywordsFromContext(SetupContext ctx)
    {
        if (ctx == null) return Array.Empty<string>();
        var src = string.Join("\n", new[] { ctx.Cv ?? string.Empty, ctx.JobDescription ?? string.Empty, ctx.ProjectInfo ?? string.Empty, ctx.PersonalProfile ?? string.Empty });
        if (string.IsNullOrWhiteSpace(src)) return Array.Empty<string>();
        var matches = Regex.Matches(src, "[A-Za-zÁÉÍÓÚÜÑáéíóúüñ0-9][A-Za-zÁÉÍÓÚÜÑáéíóúüñ0-9+_.-]{2,}");
        var groups = matches.Select(m => m.Value.Trim())
                            .Where(w => w.Length >= 3)
                            .GroupBy(w => w, StringComparer.OrdinalIgnoreCase)
                            .Select(g => new { Word = g.First(), Count = g.Count() })
                            .OrderByDescending(x => x.Count)
                            .ThenBy(x => x.Word)
                            .Take(40)
                            .Select(x => x.Word)
                            .ToArray();
        return groups;
    }
}
