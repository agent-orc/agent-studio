namespace AgentStudio.TaskServer.Contracts;

public sealed record StartRunnerGitSetupRequest(
    string RunnerId, string RepositoryUrl, string Method, string? Token = null);

public sealed record RunnerGitSetupDto(
    string Id, string RunnerId, string RepositoryUrl, string Method, string State,
    string? PublicKey, bool FetchVerified, bool PushVerified);

public sealed record RunnerGitSetupWork(
    string Id, string RepositoryUrl, string Method, string Action, string? Token);

public sealed record ReportRunnerGitKeyRequest(string PublicKey);

public sealed record ReportRunnerGitProofRequest(bool FetchVerified, bool PushVerified);
