namespace AgentStudio.TaskServer.Contracts;

public sealed record StartRunnerDeviceLoginRequest(string RunnerId, string Provider);

public sealed record RunnerDeviceLoginDto(
    string Id, string RunnerId, string Provider, string State,
    string? VerificationUrl, string? UserCode, DateTime ExpiresAt);

public sealed record RunnerDeviceLoginWork(string Id, string Provider);

public sealed record ReportRunnerDeviceInstructionsRequest(
    string VerificationUrl, string? UserCode);

public sealed record ReportRunnerDeviceResultRequest(bool Verified);
