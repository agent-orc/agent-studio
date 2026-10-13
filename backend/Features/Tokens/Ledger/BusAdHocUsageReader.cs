namespace AgentStudio.Tokens;

/// <summary>
/// Reads ad-hoc one-shot usage (<c>support:adhoc</c> token-usage messages)
/// from the workspace-scoped bus. Ad-hoc calls are not bound to a project,
/// so they live under <see cref="AgentMessageBusPaths.WorkspaceScope"/>.
/// </summary>
public sealed class BusAdHocUsageReader
{
    private readonly AgentMessageBusStore _store;
    private readonly IConfiguration _config;

    public BusAdHocUsageReader(AgentMessageBusStore store, IConfiguration config)
    {
        _store = store;
        _config = config;
    }

    /// <summary>
    /// Every ad-hoc call since <paramref name="since"/>, with the source label
    /// the aggregate reports as its log path. Empty when the workspace root is
    /// not configured.
    /// </summary>
    public (IReadOnlyList<AdHocUsageRecord> Records, string Source) Read(DateTime? since = null)
    {
        var workspace = _config["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspace)) return ([], "(unconfigured)");

        var query = new AgentMessageQuery(
            ParticipantId: "support:adhoc",
            Kind: "token-usage",
            Since: since);
        var messages = _store.Query(workspace!, AgentMessageBusPaths.WorkspaceScope, query);
        return (messages.Select(ToRecord).ToList(), "(bus)");
    }

    private static AdHocUsageRecord ToRecord(AgentMessage m)
    {
        var t = m.Tokens;
        return new AdHocUsageRecord
        {
            Ts = m.CreatedAt,
            Source = m.Topic ?? AdHocUsageSources.Unknown,
            Model = t?.Model ?? "",
            InputTokens = (int)(t?.Input ?? 0),
            OutputTokens = (int)(t?.Output ?? 0),
            CacheReadTokens = (int)(t?.CacheRead ?? 0),
            CacheCreationTokens = (int)(t?.CacheWrite ?? 0),
            DurationMs = 0,
            Ok = true,
            Project = m.Project,
            JobId = m.JobId,
        };
    }
}
