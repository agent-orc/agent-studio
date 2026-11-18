using AgentStudio.Bus;
using AgentStudio.Registry;
using AgentStudio.Runner;
using AgentStudio.Shared;

namespace AgentStudio.Tokens;

/// <summary>Reads the same merged receipts for cost markers and their model diagnostic.</summary>
internal static class UsageCostInputReader
{
    public static IReadOnlyList<UsageCostInput> Read(IReadOnlyList<ProjectRecord> allProjects,
        IReadOnlyList<ProjectRecord> visibleProjects, ProjectTokenLedger ledger,
        AgentMessageBusStore bus, TaskScannerService scanner, IConfiguration configuration,
        DateTime weekStartUtc, bool includeUnattributed)
    {
        var inputs = new List<UsageCostInput>();
        foreach (var project in visibleProjects)
        {
            try
            {
                var snapshot = ledger.LoadSnapshot(project.DisplayName, project.StorageLocation);
                inputs.Add(new UsageCostInput(project.Id, project.DisplayName,
                    snapshot.Entries, snapshot.Freshness,
                    snapshot.ReceiptSummaries.Values.Select(summary => (DateTime?)summary.LastUpdate).Max()));
            }
            catch (Exception)
            {
                inputs.Add(new UsageCostInput(project.Id, project.DisplayName, [],
                    new ProjectTokenDataFreshness { Status = "unavailable",
                        Warning = "The project ledger could not be read." }));
            }
        }

        try
        {
            var root = configuration["TaskRepository"];
            if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException();
            var workspaceUsage = WorkspaceBusUsage.Read(bus, root, allProjects, scanner, weekStartUtc);
            var unattributed = includeUnattributed ? workspaceUsage.Unattributed : [];
            var unassignedCount = includeUnattributed ? 0 : workspaceUsage.Unattributed.Count;
            for (var index = 0; index < inputs.Count; index++)
            {
                var current = inputs[index];
                if (current.ProjectId is null) continue;
                inputs[index] = current with { Entries = workspaceUsage.MergeInto(current.ProjectId, current.Entries) };
            }
            inputs.Add(new UsageCostInput(null, "Unattributed one-shot jobs", unattributed,
                new ProjectTokenDataFreshness
                {
                    Status = unassignedCount == 0 ? "complete" : "partial",
                    Sources = [WorkspaceBusUsage.Source],
                    Warning = unassignedCount == 0 ? null
                        : "Projectless workspace bus events cannot be assigned to this view."
                }));
        }
        catch (Exception)
        {
            inputs.Add(new UsageCostInput(null, "Unattributed one-shot jobs", [],
                new ProjectTokenDataFreshness { Status = "unavailable",
                    Warning = "The workspace token bus could not be read." }));
        }
        return inputs;
    }
}
