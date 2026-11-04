using AgentStudio.Cli;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

/// <summary>
/// Records every selection call. With an inner service it delegates, so the
/// consumer keeps its real behaviour; without one it answers a plain primary
/// launch of the requested pins.
/// </summary>
public sealed class SpyModelSelectionService(IModelSelectionService? inner = null) : IModelSelectionService
{
    private readonly object _lock = new();
    private readonly List<ModelSelectionCall> _calls = [];

    public IReadOnlyList<ModelSelectionCall> Calls
    {
        get { lock (_lock) return [.. _calls]; }
    }

    public ModelSelectionDecision Select(ModelSelectionCall call)
    {
        lock (_lock) _calls.Add(call);
        if (inner is not null) return inner.Select(call);
        var pins = call.Request.Pins;
        var route = new SelectionRoute(pins.CliType ?? CliTypes.Claude, pins.Model, pins.ThinkingLevel);
        var result = new SelectionResult
        {
            Outcome = SelectionOutcome.Primary,
            Route = route,
            Primary = route,
            Rule = SelectionRules.Pin,
            Reason = "launch: spy",
        };
        return new ModelSelectionDecision(result, QuotaAdmissionPlan.From(result));
    }

    public bool IsOperatorSwitchActive(string? cliType) => inner?.IsOperatorSwitchActive(cliType) ?? false;
}
