using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.Runner;

/// <summary>
/// Durable post-guard for remote claims that fail while preparing the repository
/// or execution environment, before an agent process can start.
///
/// The counter lives on the task so a runner or backend restart cannot reset the
/// loop. The first two failures return the task to Ready; the third hands it to
/// the intervention lane. A deliberate operator requeue after escalation starts
/// a fresh chain.
/// </summary>
public sealed class RemoteClaimFailureBudget
{
    public const int MaxAttempts = 3;
    internal const string FieldName = "remoteClaimFailure";

    private readonly ILogger<RemoteClaimFailureBudget> _logger;
    public int MaximumAttempts { get; }

    public RemoteClaimFailureBudget(ILogger<RemoteClaimFailureBudget> logger, int maximumAttempts = MaxAttempts)
    {
        _logger = logger;
        MaximumAttempts = Math.Clamp(maximumAttempts, 1, 20);
    }

    public RemoteClaimFailureDecision Record(TaskInfo task, string? reason, string? cause = null, string? host = null)
    {
        var previous = Read(task.FolderPath);
        var normalized = NormalizeReason(reason);
        var fingerprint = Fingerprint(cause ?? "environment-preparation", normalized);
        var attempts = string.Equals(previous?.Fingerprint, fingerprint, StringComparison.Ordinal)
            ? Math.Max(0, previous!.Attempts) + 1
            : 1;
        var state = new RemoteClaimFailureState(
            attempts,
            normalized,
            DateTime.UtcNow,
            fingerprint,
            cause ?? "environment-preparation",
            host);
        TaskJsonFile.UpdateFieldOrThrow(task.FolderPath, FieldName, state);
        return new RemoteClaimFailureDecision(
            attempts,
            MaximumAttempts,
            attempts >= MaximumAttempts,
            state.Reason);
    }

    /// <summary>
    /// A card manually requeued after the exhausted terminal starts a new
    /// attempt chain. Automatic retries retain their budget.
    /// </summary>
    public void PrepareForClaim(TaskInfo task)
    {
        if ((Read(task.FolderPath)?.Attempts ?? 0) < MaximumAttempts) return;
        Reset(task);
    }

    public void Reset(TaskInfo task)
        => TaskJsonFile.RemoveField(task.FolderPath, FieldName, _logger);

    public RemoteClaimFailureState? GetState(TaskInfo task)
        => Read(task.FolderPath);

    public static string Fingerprint(string cause, string reason)
    {
        var value = $"{cause.Trim().ToLowerInvariant()}:{NormalizeReason(reason).ToLowerInvariant()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();
    }

    internal static RemoteClaimFailureState? Read(string folderPath)
    {
        var path = Path.Combine(folderPath, "task.json");
        if (!File.Exists(path)) return null;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty(FieldName, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        return value.Deserialize<RemoteClaimFailureState>(TaskJsonFile.ReadOpts);
    }

    private static string NormalizeReason(string? reason)
    {
        var value = (reason ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        if (value.Length == 0)
            return "remote repository/environment preparation failed without a diagnostic";
        return value.Length <= 1000 ? value : value[..1000];
    }
}

public sealed record RemoteClaimFailureState(
    [property: JsonPropertyName("attempts")] int Attempts,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("lastFailureAtUtc")] DateTime LastFailureAtUtc,
    [property: JsonPropertyName("fingerprint")] string? Fingerprint = null,
    [property: JsonPropertyName("cause")] string? Cause = null,
    [property: JsonPropertyName("host")] string? Host = null);

public sealed record RemoteClaimFailureDecision(
    int Attempt,
    int MaximumAttempts,
    bool Escalate,
    string Reason);
