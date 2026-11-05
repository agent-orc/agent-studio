using AgentStudio.Pipeline;
using AgentStudio.Tasks;

namespace AgentStudio.Deploy;

/// <summary>
/// Project-local verification map for delivery reads. Store mutations publish
/// immediately; a timed scan reconciles edits made outside the store.
/// </summary>
internal sealed class DeliveryVerificationIndex(TimeProvider clock,
    Func<string, IntegrationVerificationRecord?> read)
{
    internal static readonly TimeSpan ReconciliationInterval = TimeSpan.FromMinutes(5);
    private readonly object _gate = new();
    private readonly Dictionary<string, ProjectState> _projects = new(StringComparer.OrdinalIgnoreCase);

    private sealed class ProjectState
    {
        public readonly Dictionary<string, bool> Cards = new(StringComparer.Ordinal);
        public long SeenGeneration;
        public long Version;
        public DateTimeOffset NextReconciliation;
    }

    internal sealed record Snapshot(string Signature, Dictionary<string, string> Integrated);

    public Snapshot Capture(string project, IReadOnlyList<TaskInfo> tasks)
    {
        lock (_gate)
        {
            if (!_projects.TryGetValue(project, out var state))
            {
                state = new ProjectState { NextReconciliation = clock.GetUtcNow() + ReconciliationInterval };
                _projects[project] = state;
            }

            var currentFolders = tasks.Select(task => task.FolderPath).ToHashSet(StringComparer.Ordinal);
            foreach (var folder in state.Cards.Keys.Where(folder => !currentFolders.Contains(folder)).ToArray())
            {
                state.Cards.Remove(folder);
                state.Version++;
            }

            // A new card must be read once, including after service restart.
            foreach (var folder in currentFolders.Where(folder => !state.Cards.ContainsKey(folder)))
            {
                state.Cards[folder] = read(folder)?.State
                    == IntegrationVerificationStates.Verified;
                state.Version++;
            }

            var (generation, changes) = IntegrationVerificationStore.ChangesSince(state.SeenGeneration);
            foreach (var change in changes)
            {
                if (!state.Cards.ContainsKey(change.Folder)) continue;
                state.Cards[change.Folder] = change.Verified;
                state.Version++;
            }
            state.SeenGeneration = generation;

            if (clock.GetUtcNow() >= state.NextReconciliation)
            {
                foreach (var folder in currentFolders)
                {
                    var verified = read(folder)?.State
                        == IntegrationVerificationStates.Verified;
                    if (state.Cards[folder] == verified) continue;
                    state.Cards[folder] = verified;
                    state.Version++;
                }
                state.NextReconciliation = clock.GetUtcNow() + ReconciliationInterval;
            }

            var integrated = tasks.Where(task => state.Cards[task.FolderPath])
                .GroupBy(task => string.IsNullOrWhiteSpace(task.Key) ? task.Id : task.Key!,
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Title, StringComparer.OrdinalIgnoreCase);
            var taskSignature = string.Join('|', tasks.Select(task => task.Id + ':' + task.Title));
            return new(state.Version + ":" + taskSignature, integrated);
        }
    }
}
