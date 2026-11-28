using System.Security.Cryptography;

namespace AgentStudio.TaskServer.Contracts;

public static class RunnerCredentialRenewalProtocol
{
    public const string Path = "/api/auth/runner/renewal";
    public const string StatusHeader = "X-Runner-Credential-Renewal";
    public const string ServerTimeHeader = "X-Runner-Credential-Server-Time";
    public const string StagedHeader = "X-Runner-Credential-Staged";
    public const string Due = "due";
    public const string Scheduled = "scheduled";
    public const string Disabled = "disabled";
    public const string Failing = "failing";
    public const string FileUnavailable = "token-file-unwritable";
    public const string RequestFailed = "renewal-request-failed";

    public static string NewCredential() => $"rnr.{Guid.NewGuid():N}.{RandomNumberGenerator.GetHexString(64)}";

    public static bool TryReadCredentialId(string? credential, out string id)
    {
        id = string.Empty;
        if (credential is null || !credential.StartsWith("rnr.", StringComparison.Ordinal)) return false;
        var parts = credential.Split('.', 3);
        if (parts.Length != 3 || !IsCredentialId(parts[1]) || parts[2].Length < 20) return false;
        id = parts[1];
        return true;
    }

    public static bool IsCredentialId(string? id)
        => Guid.TryParseExact(id, "N", out var parsed) && id == parsed.ToString("N");
}

// A host registers the verifier of a locally staged successor or reports a fixed failure code.
public sealed record RunnerCredentialRenewalRequest(
    string? SuccessorId = null, string? SuccessorHash = null, string? Failure = null);

public sealed record RunnerCredentialRenewalStatus(
    bool Enabled,
    bool InstallationEnabled,
    bool RunnerEnabled,
    DateTime? CurrentExpiresAt,
    DateTime? LastRenewalAt,
    DateTime? NextRenewalAt,
    DateTime? FailingSince,
    string? FailureReason,
    DateTime? LastReplayAt);
