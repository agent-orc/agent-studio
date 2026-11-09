namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// <paramref name="TargetRunnerId"/> pins the sign-in to one active runner of the host, because the
/// coding and the review identity on one host own separate CLI homes. Empty lets any runner claim it.
/// </summary>
public sealed record StartDeviceLoginRequest(string HostId, string Provider, string? TargetRunnerId = null);

/// <summary>
/// <paramref name="CodeState"/> drives the Claude code-return step (AGT-3243): <c>awaiting</c> once the
/// runner published the sign-in URL, <c>submitted</c> after the Owner pasted the authorization code,
/// <c>delivered</c> after the claiming runner took it. Null when the session takes no code back.
/// </summary>
public sealed record DeviceLoginSessionDto(
    string Handle, string State, DateTime ExpiresAt, string? VerificationUrl = null,
    string? UserCode = null, string? Detail = null, string? SessionKey = null,
    string? HostId = null, string? Provider = null, string? CodeState = null);

public sealed record DeviceLoginClaimDto(string Handle, string Provider, DateTime ExpiresAt);

/// <summary>Claude <c>setup-token</c> shows only a verification URL, so the code is optional for Claude.</summary>
public sealed record DeviceLoginInstructionsRequest(string VerificationUrl, string? UserCode);

public sealed record DeviceLoginCompletionRequest(string Result, string Detail);

/// <summary>What the claiming runner may read back: the session state only, never the code.</summary>
public sealed record DeviceLoginRunnerStateDto(string State);

/// <summary>The authorization code the Owner pastes back after the Claude browser sign-in.</summary>
public sealed record DeviceLoginCodeRequest(string? Code);

/// <summary>The one handoff of that code to the runner that claimed the session.</summary>
public sealed record DeviceLoginCodeDto(string Code);
