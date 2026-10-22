namespace AgentStudio.Shared;

/// <summary>The current short lived deployment of one card's delivery.</summary>
public sealed record TaskPreview(
    string Address, string Status, string DeliverySha, string? ScreenshotUrl,
    DateTime UpdatedAtUtc, DateTime ExpiresAtUtc, string? JobId);
