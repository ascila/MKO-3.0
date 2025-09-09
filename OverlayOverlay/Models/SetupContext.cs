using System;

namespace OverlayOverlay.Models;

public class SetupContext
{
    public string Cv { get; set; } = string.Empty;
    public string JobDescription { get; set; } = string.Empty;
    public string ProjectInfo { get; set; } = string.Empty;
    public string PersonalProfile { get; set; } = string.Empty;
    public string DocumentId { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

