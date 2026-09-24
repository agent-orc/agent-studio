

namespace AgentStudio.Tokens;

/// <summary>
/// Adapter that turns the Agent Message Bus's <c>kind=token-usage</c>
/// messages into transient <see cref="OrchestratorLogEntry"/> records so
/// the Phase-4 bus-backed readers can reuse the legacy pure-function
/// folds verbatim. By converting and then reusing the existing math, the
/// bus path cannot drift from <c>orchestrator.jsonl</c> on quantisation,
/// model casing, day-bucket formatting, or category split - parity is
/// guaranteed by construction.
/// </summary>
internal static class BusTokenEntryConverter
{
    /// <summary>
    /// Convert one bus message into the matching orchestrator-log shape.
    /// <see cref="AgentMessageTokens.CacheRead"/> / <c>CacheWrite</c> are
    /// nullable on the bus; the legacy entry shape uses zero defaults so
    /// the folds short-circuit cleanly on missing cache counts.
    /// </summary>
    public static OrchestratorLogEntry ToEntry(AgentMessage m, bool includeParticipant = true)
    {
        var t = m.Tokens is null ? null : StoredUsageNormalization.Normalize(m.Tokens);
        var usage = t is null
            ? null
            : new OrchestratorTokenUsage
            {
                Model = t.Model,
                InputTokens = SafeInt(t.Input),
                OutputTokens = SafeInt(t.Output),
                CacheReadTokens = SafeInt(t.CacheRead ?? 0),
                CacheCreationTokens = SafeInt(t.CacheWrite ?? 0),
                ThinkingLevel = t.ThinkingLevel,
                InputIncludesCached = t.InputIncludesCached,
                UsageNormalization = t.UsageNormalization,
                PinnedModel = t.PinnedModel,
                ModelMismatch = t.ModelMismatch,
            };

        return new OrchestratorLogEntry
        {
            Ts = m.CreatedAt,
            Kind = OrchestratorLogKinds.Decision,
            Topic = m.Topic ?? OrchestratorLogTopics.General,
            Summary = m.Summary ?? string.Empty,
            JobId = m.JobId,
            ParticipantId = includeParticipant ? m.ParticipantId : null,
            TokenUsage = usage,
        };
    }

    /// <summary>
    /// Pull every <c>kind=token-usage</c> message attributed to a project's
    /// orchestrator (participantId <c>orchestrator:&lt;project&gt;</c>) and
    /// convert them to log entries. Mirrors what
    /// <see cref="OrchestratorLog.Read(string)"/> would have surfaced from
    /// <c>orchestrator.jsonl</c>; agent and supporting-agent token-usage
    /// messages on the bus stay out of scope because they were never in
    /// <c>orchestrator.jsonl</c> either.
    /// </summary>
    public static List<OrchestratorLogEntry> LoadOrchestratorEntries(
        AgentMessageBusStore store,
        string workspaceRoot,
        string projectName)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || string.IsNullOrWhiteSpace(projectName))
            return new List<OrchestratorLogEntry>();
        var participant = AgentMessageBusBridge.ParticipantOrchestratorFor(projectName);
        var messages = store.Query(workspaceRoot, projectName, new AgentMessageQuery(
            ParticipantId: participant,
            Kind: "token-usage"));
        var entries = new List<OrchestratorLogEntry>(messages.Count);
        foreach (var m in messages)
        {
            entries.Add(ToEntry(m, includeParticipant: false));
        }
        return entries;
    }

    /// <summary>
    /// Pull every project-scoped <c>kind=token-usage</c> message, regardless
    /// of participant, and retain the participant id on the transient entry.
    /// Runtime token panels use this bus-native shape so coding-agent turns
    /// (<c>agent:*</c>) do not disappear behind orchestrator-only parity
    /// shims.
    /// </summary>
    public static List<OrchestratorLogEntry> LoadTokenUsageEntries(
        AgentMessageBusStore store,
        string workspaceRoot,
        string projectName)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || string.IsNullOrWhiteSpace(projectName))
            return new List<OrchestratorLogEntry>();
        var messages = store.Query(workspaceRoot, projectName, new AgentMessageQuery(
            Kind: "token-usage"));
        var entries = new List<OrchestratorLogEntry>(messages.Count);
        foreach (var m in messages)
        {
            entries.Add(ToEntry(m));
        }
        return entries;
    }

    private static int SafeInt(long value)
    {
        if (value <= 0) return 0;
        return value > int.MaxValue ? int.MaxValue : (int)value;
    }
}
