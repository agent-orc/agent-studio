using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStudio.Prompts;

namespace AgentStudio.Runner;

/// <summary>
/// Pure retry decision for consecutive Remote completion deliveries that lack
/// a complete immutable result envelope.
/// </summary>
public static class RemoteDeliveryFailurePolicy
{
    public const string DeliveryFailed = "delivery-failed";
    public const int MaximumAttempts = 2;

    public static RemoteDeliveryFailureDecision Decide(int previousConsecutiveFailures)
        => ForAttempt(Math.Max(0, previousConsecutiveFailures) + 1);

    public static RemoteDeliveryFailureDecision ForAttempt(int attempt)
    {
        var normalized = Math.Clamp(attempt, 1, MaximumAttempts);
        return new RemoteDeliveryFailureDecision(
            normalized,
            MaximumAttempts,
            normalized >= MaximumAttempts);
    }
}

/// <summary>
/// Durable consecutive-failure state. The first failed delivery remains on the
/// card while it returns to Ready; a backend or runner restart therefore cannot
/// reset the second-attempt escalation boundary.
/// </summary>
public sealed class RemoteDeliveryFailureStore
{
    internal const string FieldName = "remoteDeliveryFailure";

    private readonly ILogger<RemoteDeliveryFailureStore> _logger;

    public RemoteDeliveryFailureStore(ILogger<RemoteDeliveryFailureStore> logger)
    {
        _logger = logger;
    }

    public RemoteDeliveryFailureDecision Record(
        TaskInfo task,
        string reason,
        string? fenceBranch,
        string? fenceCommitSha)
    {
        var previous = Read(task.FolderPath);
        var decision = RemoteDeliveryFailurePolicy.Decide(previous?.ConsecutiveAttempts ?? 0);
        var state = new RemoteDeliveryFailureState(
            RemoteDeliveryFailurePolicy.DeliveryFailed,
            decision.Attempt,
            Normalize(reason, 2000),
            NormalizeNull(fenceBranch, 1000),
            NormalizeNull(fenceCommitSha, 128),
            DateTime.UtcNow);
        TaskJsonFile.UpdateFieldOrThrow(task.FolderPath, FieldName, state);
        return decision;
    }

    public RemoteDeliveryFailureDecision? GetDecision(TaskInfo task)
    {
        var current = Read(task.FolderPath);
        return current is null
            ? null
            : RemoteDeliveryFailurePolicy.ForAttempt(current.ConsecutiveAttempts);
    }

    /// <summary>
    /// A deliberate requeue after the exhausted escalation starts a new chain.
    /// The automatic first retry keeps its single spent attempt.
    /// </summary>
    public void PrepareForClaim(TaskInfo task)
    {
        if ((Read(task.FolderPath)?.ConsecutiveAttempts ?? 0)
            < RemoteDeliveryFailurePolicy.MaximumAttempts)
            return;
        Reset(task);
    }

    public void Reset(TaskInfo task)
        => TaskJsonFile.RemoveField(task.FolderPath, FieldName, _logger);

    internal static RemoteDeliveryFailureState? Read(string folderPath)
    {
        var path = Path.Combine(folderPath, "task.json");
        if (!File.Exists(path)) return null;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty(FieldName, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        return value.Deserialize<RemoteDeliveryFailureState>(TaskJsonFile.ReadOpts);
    }

    private static string Normalize(string? value, int maximumLength)
    {
        var normalized = (value ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        if (normalized.Length == 0)
            return "Remote completion delivery failed without a diagnostic.";
        return normalized.Length <= maximumLength
            ? normalized
            : normalized[..maximumLength];
    }

    private static string? NormalizeNull(string? value, int maximumLength)
    {
        var normalized = (value ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        if (normalized.Length == 0) return null;
        return normalized.Length <= maximumLength
            ? normalized
            : normalized[..maximumLength];
    }
}

/// <summary>Idempotent, append-only operator and next-attempt context.</summary>
public static class RemoteDeliveryFailureNote
{
    public static void Append(
        string folderPath,
        string attemptId,
        RemoteDeliveryFailureDecision decision,
        string reason,
        string? fenceBranch,
        string? fenceCommitSha,
        string? fenceBranchUrl)
    {
        AppendStatus(
            folderPath,
            attemptId,
            decision,
            reason,
            fenceBranch,
            fenceCommitSha,
            fenceBranchUrl);
        AppendPrompt(
            folderPath,
            attemptId,
            decision,
            reason,
            fenceBranch,
            fenceCommitSha);
    }

    private static void AppendStatus(
        string folderPath,
        string attemptId,
        RemoteDeliveryFailureDecision decision,
        string reason,
        string? fenceBranch,
        string? fenceCommitSha,
        string? fenceBranchUrl)
    {
        var path = Path.Combine(folderPath, "status.md");
        var marker = $"<!-- agent-studio:remote-delivery-failure:{attemptId} -->";
        var existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        if (existing.Contains(marker, StringComparison.Ordinal)) return;

        var nl = Environment.NewLine;
        var note = new StringBuilder();
        if (existing.Length > 0 && !existing.EndsWith(nl, StringComparison.Ordinal))
            note.Append(nl);
        if (existing.Length > 0) note.Append(nl);
        note.Append(marker).Append(nl);
        note.Append("## Remote delivery failure").Append(nl).Append(nl);
        note.Append("- Delivery status: `")
            .Append(RemoteDeliveryFailurePolicy.DeliveryFailed)
            .Append("`").Append(nl);
        note.Append("- Envelope attempt: ")
            .Append(decision.Attempt)
            .Append('/')
            .Append(decision.MaximumAttempts)
            .Append(nl);
        note.Append("- Error: ").Append(reason.Trim()).Append(nl);
        if (!string.IsNullOrWhiteSpace(fenceBranch))
        {
            var reference = !string.IsNullOrWhiteSpace(fenceBranchUrl)
                ? $"[{fenceBranch}]({fenceBranchUrl})"
                : $"`{fenceBranch}`";
            note.Append("- Salvage fence: ").Append(reference);
            if (!string.IsNullOrWhiteSpace(fenceCommitSha))
                note.Append(" at `").Append(fenceCommitSha).Append('`');
            note.Append(nl);
        }
        note.Append(decision.Escalate
            ? "- Action: escalated with category `unverified-delivery` after two consecutive envelope failures."
            : "- Action: automatically requeued to `2-ready`; the next runner must resume from the salvage fence and publish a complete immutable result envelope.")
            .Append(nl);

        File.AppendAllText(path, note.ToString(), Encoding.UTF8);
    }

    private static void AppendPrompt(
        string folderPath,
        string attemptId,
        RemoteDeliveryFailureDecision decision,
        string reason,
        string? fenceBranch,
        string? fenceCommitSha)
    {
        var nl = PromptText.NewLine;
        var promptPath = Path.Combine(folderPath, "prompt.md");
        var promptMarker = $"<!-- agent-studio:remote-delivery-retry:{attemptId} -->";
        var prompt = File.Exists(promptPath) ? File.ReadAllText(promptPath) : string.Empty;
        if (prompt.Contains(promptMarker, StringComparison.Ordinal)) return;
        var retry = new StringBuilder();
        if (prompt.Length > 0 && !prompt.EndsWith(nl, StringComparison.Ordinal))
            retry.Append(nl);
        if (prompt.Length > 0) retry.Append(nl);
        retry.Append("---").Append(nl).Append(nl);
        retry.Append(promptMarker).Append(nl);
        retry.Append("## Automatic remote delivery retry").Append(nl).Append(nl);
        retry.Append("The previous run's delivery failed because its immutable result envelope was incomplete. ")
            .Append(reason.Trim()).Append(nl).Append(nl);
        if (!string.IsNullOrWhiteSpace(fenceBranch))
        {
            retry.Append("Recover the previous work from salvage fence `")
                .Append(fenceBranch)
                .Append('`');
            if (!string.IsNullOrWhiteSpace(fenceCommitSha))
                retry.Append(" at `").Append(fenceCommitSha).Append('`');
            retry.Append(" before continuing.").Append(nl).Append(nl);
        }
        retry.Append("Publish a complete immutable result envelope with `BaseSha`, `ImmutableResultRef`, and `ArtifactManifestDigest`. ")
            .Append(decision.Escalate
                ? "This was the second consecutive envelope failure and requires operator recovery."
                : "This is the one automatic retry before `unverified-delivery` escalation.")
            .Append(nl);
        File.AppendAllText(promptPath, retry.ToString(), Encoding.UTF8);
    }
}

public sealed record RemoteDeliveryFailureState(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("consecutiveAttempts")] int ConsecutiveAttempts,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("fenceBranch")] string? FenceBranch,
    [property: JsonPropertyName("fenceCommitSha")] string? FenceCommitSha,
    [property: JsonPropertyName("lastFailureAtUtc")] DateTime LastFailureAtUtc);

public sealed record RemoteDeliveryFailureDecision(
    int Attempt,
    int MaximumAttempts,
    bool Escalate);
