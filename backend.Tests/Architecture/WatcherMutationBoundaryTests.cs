using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2721 acceptance: "no task or Git mutation outside proposal creation and
/// comments". That is only checkable if every write goes through one adapter,
/// so this test pins the boundary rather than trusting review to notice a new
/// call site.
/// </summary>
public sealed class WatcherMutationBoundaryTests
{
    /// <summary>The single file allowed to reach task-mutating services.</summary>
    private const string Adapter = "WatcherTaskGatewayAdapter.cs";

    /// <summary>Services that can move a card, rewrite its files, or touch Git.</summary>
    private static readonly string[] MutatingServices =
    [
        "TaskMutationService",
        "TaskTransitionService",
        "TaskStateMachine",
        "TaskJsonFile",
        "TimelineLog",
        "GitService",
        "MergeService",
    ];

    /// <summary>Git is never reached from the Watcher at all, not even through the adapter.</summary>
    private static readonly string[] ForbiddenEverywhere =
    [
        "GitService",
        "MergeService",
        "System.Diagnostics.Process",
    ];

    [Fact]
    public void OnlyTheGatewayAdapterReachesTaskMutatingServices()
    {
        var offenders = new List<string>();
        foreach (var file in WatcherSources())
        {
            var name = Path.GetFileName(file);
            if (name == Adapter) continue;
            var text = File.ReadAllText(file);
            foreach (var service in MutatingServices)
            {
                if (text.Contains(service, StringComparison.Ordinal))
                    offenders.Add($"{name} references {service}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"Watcher task mutations must go through {Adapter} (IWatcherTaskGateway):\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void TheWatcherNeverReachesGitOrSpawnsAProcess()
    {
        var offenders = new List<string>();
        foreach (var file in WatcherSources())
        {
            var text = File.ReadAllText(file);
            foreach (var forbidden in ForbiddenEverywhere)
            {
                if (text.Contains(forbidden, StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(file)} references {forbidden}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "The Watcher observes and proposes; Git and process control stay with the pipeline:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void TheGatewayCreatesCardsOnlyInTheProposalState()
    {
        var adapter = File.ReadAllText(Path.Combine(WatcherDir(), Adapter));

        // Preparation is the proposal state; Ready is reachable only from the
        // operator-decision path, which names its own expected source state.
        Assert.Contains("TargetState = TaskStates.Preparation", adapter, StringComparison.Ordinal);
        Assert.Contains("expectedSourceState: TaskStates.Preparation", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("TargetState = TaskStates.Ready", adapter, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryDetectorRuleIsCoveredByTheFixtureMatrix()
    {
        var covered = WatcherFixtureMatrix.All
            .Select(fixture => fixture.ExpectedRule)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(rule => rule, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(WatcherDetectorRules.All.OrderBy(rule => rule, StringComparer.Ordinal), covered);
    }

    private static IEnumerable<string> WatcherSources()
        => Directory.EnumerateFiles(WatcherDir(), "*.cs", SearchOption.AllDirectories);

    private static string WatcherDir()
        => Path.Combine(RepoRoot(), "backend", "Features", "Watcher");

    private static string RepoRoot()
    {
        var current = AppContext.BaseDirectory;
        while (current != null)
        {
            if (File.Exists(Path.Combine(current, "agent-taskboard.sln"))) return current;
            current = Path.GetDirectoryName(current);
        }
        throw new InvalidOperationException("agent-taskboard.sln not found above the test base directory.");
    }
}
