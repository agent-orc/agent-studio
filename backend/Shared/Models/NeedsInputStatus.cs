namespace AgentStudio.Shared;

/// <summary>Read-only projection of results/needs-input.md for operator surfaces.</summary>
public sealed record NeedsInputStatus(
    string Message,
    string FirstLine,
    string RunAttemptId,
    string? SalvageBranch,
    string ArtifactPath);
