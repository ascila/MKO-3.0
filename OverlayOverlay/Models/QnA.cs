using System;

namespace OverlayOverlay.Models;

public class QnA
{
    public long Id { get; set; }
    public string Question { get; set; } = string.Empty;
    public string? Answer { get; set; }
    public string Status { get; set; } = "pending"; // pending|answered|failed
    public string Language { get; set; } = "en-US";
    public string Source { get; set; } = "capture"; // auto|capture|followup|analyze|regenerate
    public string Route { get; set; } = "default";  // personal|interviewer|project|visual|text|default
    public bool ProjectModeAtAnswer { get; set; }
    public int QuestionNumber { get; set; }
    public QnAContext Context { get; set; } = new();
    public QnATimings Timings { get; set; } = new();
    public string DocPush { get; set; } = "none"; // none|queued|ok|error
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class QnAContext
{
    public string? ImageDataUrl { get; set; }
    public string? ClipboardText { get; set; }
}

public class QnATimings
{
    public int? ExtractMs { get; set; }
    public int? AnswerMs { get; set; }
}

