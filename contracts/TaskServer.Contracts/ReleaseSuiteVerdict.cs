namespace AgentStudio.TaskServer.Contracts;

/// <summary>The T4 result for one project, candidate commit and command set.</summary>
public sealed record ReleaseSuiteVerdict(string Project, string CommitSha, string CommandDigest,
    string Status, DateTimeOffset FinishedAtUtc);

