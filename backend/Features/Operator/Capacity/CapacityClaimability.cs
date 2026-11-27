using AgentStudio.Cli;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Operator;

/// <summary>Explains the existing pickup and observed refusal facts, without changing them.</summary>
internal static class CapacityClaimability
{
    internal static IReadOnlyList<CapacityBlock> Blocks(TaskInfo task, ProjectSettings project,
        IReadOnlyList<CapacityHost> hosts, TaskReferenceIndex references,
        IReadOnlyDictionary<string, TaskIntegrationStatus> integration, CapacityForecast forecast,
        ProjectSlotVerdict slot, DispatchPickupDecision? dispatch)
    {
        var blocks = new List<CapacityBlock>();
        if (task.ParkedBlocker is not null) blocks.Add(new("parked", task.ParkedBlocker.Reason));
        if (forecast.Plan.Outcome is QuotaAdmissionOutcome.Wait or QuotaAdmissionOutcome.Throttle)
            blocks.Add(new("quota", forecast.Plan.Reason));
        if (forecast.Plan.Outcome == QuotaAdmissionOutcome.HostCapabilityMissing)
            blocks.Add(new("dispatch-rule", forecast.Plan.Reason));
        if (!slot.HasSlot) blocks.Add(new("dispatch-rule", slot.Detail));
        var eligible = hosts.Where(host => CodingCapacityService.Assigned(project, host)).ToArray();
        if (eligible.Length > 0 && eligible.All(host => host.Used >= host.Slots))
            blocks.Add(new("dispatch-rule", "No free coding slot at the project execution location"));
        if (!ProjectExecutionPolicy.AllowsAutomaticPickup(project))
            blocks.Add(new("dispatch-rule", "Automatic pickup is paused or manual"));
        else if (eligible.Length == 0) blocks.Add(new("dispatch-rule", "No fresh coding host at the project execution location"));
        else if (!references.EvaluateWaitsOn(task, integration, project.IntegrationBranch).Blocked
            && !eligible.Any(host => ProjectExecutionPolicy.IsLocalExecution(project)
            ? AgentTypes.IsAutoPickupEligible(task.Agent) && !TaskKinds.IsDecision(task.Kind)
                && !RetiredContainerTask.IsRetired(task) && !TaskSlugs.IsHumanDecisionNeeded(task.Id)
                && BuildProfileGate.AllowsAutoPickup(project.BuildProfile)
                && (!project.IntakeEnabled.GetValueOrDefault() || task.Phase == LifecyclePhases.IntakePassed)
            : RemoteDispatchEligibility.IsAssignedAndRunnable(task, project, host.Id, host.Name, references, integration)))
            blocks.Add(new("dispatch-rule", "Current pickup eligibility rejects this card"));
        if (dispatch is { Admit: false })
            blocks.Add(new("dispatch-rule", dispatch.Reason));
        else if (dispatch is null && task.RemoteDispatchRejection is { } rejection && rejection.RejectedAtUtc >= task.EnteredLaneAt)
            blocks.Add(new(rejection.Code == "quota-admission" ? "quota" : "dispatch-rule", $"Last claim refusal: {rejection.Code}: {rejection.Reason}"));
        return blocks;
    }
}
