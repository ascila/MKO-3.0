using System;

namespace OverlayOverlay.Services;

public static class AnswerRouter
{
    public static string Route(string question, bool projectMode, string language)
    {
        var q = (question ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(q)) return "default";

        // Very simple heuristics; refine later
        if (q.Contains("tell me about yourself") || q.Contains("háblame de ti") || q.Contains("cuéntame de ti"))
            return "personal";
        if (q.Contains("do you have any questions") || q.Contains("tienes alguna pregunta"))
            return "interviewer";
        if (projectMode && (q.Contains("project") || q.Contains("proyecto") || q.Contains("architecture") || q.Contains("diseño")))
            return "project";
        return "default";
    }
}

