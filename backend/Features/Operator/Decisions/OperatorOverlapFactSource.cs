using AgentStudio.Projects;
using AgentStudio.Runner;

namespace AgentStudio.Operator;

/// <summary>Projects the claim path's continuous scope-only wait into Needs you.</summary>
public sealed class OperatorOverlapFactSource(OverlapDispatchGuard guard, TaskScannerService scanner,
    ProjectSettingsService settings) : IOperatorDecisionFactSource
{
    public IReadOnlyList<string> Types { get; } = [OperatorDecisionTypes.OverlapStarved];

    public IReadOnlyList<OperatorDecisionFact>? Collect(DateTime nowUtc)
        => scanner.TryGetPublishedBoard() is { } board ? guard.Facts(board.Live, settings.Get, nowUtc) : null;
}
