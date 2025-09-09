using System.Threading.Tasks;
using OverlayOverlay.Models;

namespace OverlayOverlay.Services;

public class AnswerInput
{
    public string Question { get; set; } = string.Empty;
    public string Language { get; set; } = "en-US";
    public string Cv { get; set; } = string.Empty;
    public string JobDescription { get; set; } = string.Empty;
    public string? PersonalProfile { get; set; }
    public string? ProjectInfo { get; set; }
    public (string question, string answer, QnAContext context)[]? ConversationHistory { get; set; }
    public bool IsFollowUp { get; set; }
    public string? ImageDataUrl { get; set; }
    public string? ClipboardText { get; set; }
    public string? DocumentId { get; set; }
    public int QuestionNumber { get; set; }
}

public class AnswerResult
{
    public string Answer { get; set; } = "";
}

public static class AnswerGenerator
{
    // Stub — real LLM call will be added later
    public static Task<AnswerResult> GenerateAsync(AnswerInput input)
    {
        var prefix = input.IsFollowUp ? "(follow-up) " : string.Empty;
        var msg = $"{prefix}Answer placeholder for: {input.Question}";
        return Task.FromResult(new AnswerResult { Answer = msg });
    }
}

