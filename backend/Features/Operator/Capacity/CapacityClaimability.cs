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
    internal static int AdmissibleSlots(HostCapacityTargets targets, HostAdmissionFacts facts,
        int roleSlots, List<CapacityBlock> blocks, List<string>? limits = null)
    {
        var initial = HostCapacityPolicy.Decide(targets, facts);
        if (!initial.Admitted && !blocks.Any(block => block.Reason == initial.Detail))
            blocks.Add(new("admission", initial.Detail));
        if (blocks.Count > 0) return 0;
        var available = Math.Max(0, Math.Min(roleSlots, targets.MaxParallelism) - facts.ActiveRuns);
        var count = 0;
        // Evaluate a read-only burst at the observation time using the same central policy.
        // Conservative ramp or CPU pressure can admit the idle host's first slot only.
        while (count < available && HostCapacityPolicy.Decide(targets,
            facts with { ActiveRuns = facts.ActiveRuns + count,
                LastAdmissionAt = count == 0 ? facts.LastAdmissionAt : facts.Now }).Admitted) count++;
        if (count < available && limits is not null)
        {
            var stop = HostCapacityPolicy.Decide(targets, facts with { ActiveRuns = facts.ActiveRuns + count, LastAdmissionAt = facts.Now });
            limits.Add(stop.ReasonCode == AgentStudio.Shared.HostAdmissionReasons.RampLimited
                ? $"{RunnerRampStrategies.Normalize(targets.RampStrategy)} ramp admits one run every {HostCapacityPolicy.RampInterval(targets.RampStrategy).TotalSeconds:0}s and limits admission to {count} of {available} free slots"
                : $"{stop.Detail}; admission is limited to {count} of {available} free slots");
        }
        return count;
    }

    internal static IReadOnlyList<CapacityBlock> HostBlocks(CapacityHost host,
        RemoteProjectRepository? repository, DateTime now)
    {
        var blocks = host.AdmissionBlocks.ToList();
        if (host.AdmissibleSlots == 0 && blocks.Count == 0)
            blocks.Add(new("admission", "No coding slot is admissible now"));
        blocks.AddRange(PreflightBlocks(host, repository, now));
        return blocks;
    }

    /// <summary>Repository preflight holds of one project on one host; empty for a local host.</summary>
    internal static IReadOnlyList<CapacityBlock> PreflightBlocks(CapacityHost host, RemoteProjectRepository? repository, DateTime now)
    {
        var blocks = new List<CapacityBlock>();
        if (!host.IsLocal)
        {
            if (repository is null)
                blocks.Add(new("admission", "Project repository preflight is unavailable: no repository URL is configured"));
            else
            {
                var proof = host.Identity?.RunnerProjectPreflights.FirstOrDefault(row => row.ProjectId == repository.ProjectId);
                if (proof is null || proof.RegistrationFingerprint != ProjectDeliveryPreflightFingerprint.Create(repository)
                    || !ProjectDeliveryPreflightPolicy.IsFresh(proof, now))
                    blocks.Add(new("admission", "Project repository preflight is missing or stale; awaiting delivery proof"));
                else if (!string.Equals(proof.Status, "ready", StringComparison.OrdinalIgnoreCase))
                    blocks.Add(new("admission", $"Project repository preflight failed: {proof.Detail}"));
            }
        }
        return blocks;
    }

    internal static CapacityBlock? Overlap(TaskInfo task, ProjectSettings project, IEnumerable<TaskInfo> tasks,
        IReadOnlyDictionary<string, TaskIntegrationStatus> integration)
    {
        var window = OverlapWindows.Resolve(project);
        var comparison = OverlapComparisonSet.ForProject(tasks, task.ProjectName, window,
            TaskScopePrediction.Predict, integration, project.IntegrationBranch);
        var scope = TaskScopePrediction.Predict(task) with { BypassScope = OverlapComparisonSet.IsDeliveryRecovery(task) };
        var verdict = ParallelSlotPolicy.DecideOverlap(task.Id, scope, comparison.Running,
            OverlapComparisonSet.For(task, comparison.InFlight), window);
        return verdict.Admitted ? null : new("admission", verdict.Reason);
    }

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
