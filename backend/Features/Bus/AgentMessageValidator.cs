
namespace AgentStudio.Bus;

/// <summary>
/// Validates <see cref="AgentMessage"/> instances against the value sets in
/// <c>docs/app/schemas/agent-message.schema.json</c>. The on-disk shape is JSON; we
/// keep enum-like fields as strings so the schema's mixed casing survives a
/// round-trip, and check the values here in one place.
/// </summary>
/// <remarks>
/// Validation is "where practical" by design: the bus contract allows additive
/// optional fields without a schemaVersion bump, so we check required fields,
/// known enums, and length constraints, but accept unknown payload keys.
/// </remarks>
public static class AgentMessageValidator
{
    public static readonly IReadOnlySet<string> Roles = new HashSet<string>(StringComparer.Ordinal)
    {
        "actor", "system", "evidence",
    };

    public static readonly IReadOnlySet<string> Kinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "observation",
        "question",
        "decision",
        "advisory",
        "intervention",
        "artifact",
        "token-usage",
        "lifecycle",
        "error",
        "heartbeat",
    };

    public static readonly IReadOnlySet<string> Severities = new HashSet<string>(StringComparer.Ordinal)
    {
        "Info", "Warn", "High",
    };

    public static readonly IReadOnlySet<string> ParticipantKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "User", "Orchestrator", "Supervisor", "CodingAgent",
        "SupportingAgent", "SystemReview", "Runtime", "External", "Persona",
    };

    public static readonly IReadOnlySet<string> ArtifactKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "screenshot", "log-slice", "status-md", "diff", "markdown-report",
        "json-document", "supervisor-advisory", "supervisor-intervention",
        "runtime-event", "transcript", "external",
    };

    public static bool TryValidate(AgentMessage message, out string? error)
    {
        if (message is null) { error = "message is null"; return false; }
        if (message.SchemaVersion != 1) { error = $"unsupported schemaVersion {message.SchemaVersion}"; return false; }
        if (string.IsNullOrWhiteSpace(message.Id) || message.Id.Length < 8 || message.Id.Length > 128)
        { error = "id length out of range (8..128)"; return false; }
        if (message.CreatedAt == default || message.CreatedAt.Kind == DateTimeKind.Unspecified)
        { error = "createdAt must be a UTC timestamp"; return false; }
        if (string.IsNullOrWhiteSpace(message.ParticipantId)) { error = "participantId required"; return false; }
        if (!Roles.Contains(message.Role)) { error = $"role '{message.Role}' not in enum"; return false; }
        if (!Kinds.Contains(message.Kind)) { error = $"kind '{message.Kind}' not in enum"; return false; }
        if (message.Severity is not null && !Severities.Contains(message.Severity))
        { error = $"severity '{message.Severity}' not in enum"; return false; }
        if (message.Summary is { Length: > 280 }) { error = "summary exceeds 280 chars"; return false; }
        if (message.Tags is not null)
        {
            if (message.Tags.Count > 16) { error = "tags exceeds 16 items"; return false; }
            foreach (var t in message.Tags)
            {
                if (string.IsNullOrEmpty(t) || t.Length > 64) { error = "tag length out of range (1..64)"; return false; }
            }
        }
        if (message.Artifacts is not null)
        {
            foreach (var a in message.Artifacts)
            {
                if (a is null || string.IsNullOrWhiteSpace(a.Kind) || string.IsNullOrWhiteSpace(a.Uri))
                { error = "artifact missing kind/uri"; return false; }
                if (!ArtifactKinds.Contains(a.Kind)) { error = $"artifact kind '{a.Kind}' not in enum"; return false; }
            }
        }
        error = null;
        return true;
    }

    public static bool TryValidate(AgentParticipant participant, out string? error)
    {
        if (participant is null) { error = "participant is null"; return false; }
        if (string.IsNullOrWhiteSpace(participant.Id)) { error = "id required"; return false; }
        if (!ParticipantKinds.Contains(participant.Kind))
        { error = $"kind '{participant.Kind}' not in enum"; return false; }
        if (string.IsNullOrWhiteSpace(participant.DisplayName)) { error = "displayName required"; return false; }
        error = null;
        return true;
    }
}
