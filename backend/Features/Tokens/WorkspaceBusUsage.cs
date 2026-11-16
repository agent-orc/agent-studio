using AgentStudio.Bus;
using AgentStudio.Registry;
using AgentStudio.Tasks;

namespace AgentStudio.Tokens;

/// <summary>
/// Workspace-scoped one-shot <c>kind=token-usage</c> bus rows split by owner:
/// rows that name a workspace project (bus project, ad-hoc project tag, or
/// owning job; <see cref="TokenUsageAttribution"/>) land on that project,
/// projectless rows are unattributed, and rows naming a project outside the
/// workspace are dropped. The usage cockpit cost and the scoped token ledger
/// read this one split so their totals reconcile (AGT-3798).
/// <see cref="ByProject"/> is keyed by project id as read; <see cref="KeyedByName"/>
/// re-keys it for surfaces that address projects by display name.
/// </summary>
public sealed record WorkspaceBusUsage(
    IReadOnlyDictionary<string, IReadOnlyList<OrchestratorLogEntry>> ByProject,
    IReadOnlyList<OrchestratorLogEntry> Unattributed)
{
    public const string Source = "workspace-token-bus";

    public static WorkspaceBusUsage Read(AgentMessageBusStore bus, string workspaceRoot,
        IReadOnlyList<ProjectRecord> workspaceProjects, TaskScannerService scanner, DateTime sinceUtc)
    {
        var messages = bus.Query(workspaceRoot, AgentMessageBusPaths.WorkspaceScope,
            new AgentMessageQuery(Kind: "token-usage", Since: sinceUtc));
        var byProject = new Dictionary<string, List<OrchestratorLogEntry>>(StringComparer.OrdinalIgnoreCase);
        var unattributed = new List<OrchestratorLogEntry>();
        var jobOwners = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var message in messages)
        {
            var entry = BusTokenEntryConverter.ToEntry(message);
            var attribution = TokenUsageAttribution.Resolve(message.Project, message.Tags, message.JobId,
                name => UsageCockpitEndpoints.MatchProject(workspaceProjects, name)?.Id,
                jobId => UsageCockpitEndpoints.JobOwner(scanner, workspaceProjects, jobOwners, jobId));
            if (attribution.ProjectId is null)
            {
                // A row naming a project outside this workspace is not ours.
                if (string.IsNullOrWhiteSpace(message.Project)) unattributed.Add(entry);
                continue;
            }
            if (!byProject.TryGetValue(attribution.ProjectId, out var rows))
                byProject[attribution.ProjectId] = rows = [];
            rows.Add(entry);
        }
        return new WorkspaceBusUsage(
            byProject.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<OrchestratorLogEntry>)pair.Value,
                StringComparer.OrdinalIgnoreCase),
            unattributed);
    }

    /// <summary>
    /// The project's ledger with its workspace rows merged in one at a time, so
    /// a row the ledger or an earlier workspace row already holds counts once.
    /// </summary>
    public IReadOnlyList<OrchestratorLogEntry> MergeInto(string project, IReadOnlyList<OrchestratorLogEntry> ledger)
        => ByProject.TryGetValue(project, out var rows)
            ? rows.Aggregate(ledger, (current, entry) => TokenLedgerMerge.WithoutDuplicates(current, [entry]))
            : ledger;

    /// <summary>The same split keyed by display name, limited to <paramref name="projects"/>.</summary>
    public WorkspaceBusUsage KeyedByName(IEnumerable<ProjectRecord> projects, bool includeUnattributed)
        => new(projects
                .Where(project => ByProject.ContainsKey(project.Id))
                .ToDictionary(project => project.DisplayName, project => ByProject[project.Id], StringComparer.OrdinalIgnoreCase),
            includeUnattributed ? Unattributed : []);
}
