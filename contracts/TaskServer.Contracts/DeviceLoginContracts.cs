namespace AgentStudio.TaskServer.Contracts;

public sealed record StartDeviceLoginRequest(string HostId, string Provider);

public sealed record DeviceLoginSessionDto(
    string Handle, string State, DateTime ExpiresAt, string? VerificationUrl = null,
    string? UserCode = null, string? Detail = null, string? SessionKey = null);

public sealed record DeviceLoginClaimDto(string Handle, string Provider, DateTime ExpiresAt);

public sealed record DeviceLoginInstructionsRequest(string VerificationUrl, string UserCode);

public sealed record DeviceLoginCompletionRequest(string Result, string Detail);
