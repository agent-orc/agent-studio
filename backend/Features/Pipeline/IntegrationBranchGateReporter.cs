using AgentStudio.Bus;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

/// <summary>
/// One verification gate the review measured on the integration branch, ready to
/// be recorded and (on a red transition) alerted.
/// </summary>
public sealed record IntegrationBranchGateFinding(
    string StepId,
    string Command,
    string MergeBaseSha,
    string Summary,
    string? LastGreenSha);

/// <summary>
/// Turns a settled review report into integration-branch gate health (AGT-2819).
/// <para>
/// Reviews already run every deterministic gate on the merge base before
/// attributing its failure, so the branch is measured on every delivery. This
/// service is the bounded side effect at the end of that flow: persist what was
/// measured, and emit one operator-feed alert on the day a gate turns red,
/// naming the step and the commits it turned red between.
/// </para>
/// </summary>
public sealed class IntegrationBranchGateReporter
{
    private readonly AgentMessageBusBridge _bus;
    private readonly TimelineLog _timeline;
    private readonly ILogger<IntegrationBranchGateReporter> _logger;
    private readonly string? _workspaceRoot;

    public IntegrationBranchGateReporter(
        AgentMessageBusBridge bus,
        TimelineLog timeline,
        IConfiguration configuration,
        ILogger<IntegrationBranchGateReporter> logger)
        : this(bus, timeline, configuration["TaskRepository"], logger)
    {
    }

    /// <summary>Explicit-root ctor for tests.</summary>
    public IntegrationBranchGateReporter(
        AgentMessageBusBridge bus,
        TimelineLog timeline,
        string? workspaceRoot,
        ILogger<IntegrationBranchGateReporter> logger)
    {
        _bus = bus;
        _timeline = timeline;
        _workspaceRoot = workspaceRoot;
        _logger = logger;
    }

    /// <summary>
    /// The verification steps this report measured on the merge base, split by
    /// what the measurement said. Pure: it reads the frozen plan and the recorded
    /// evidence and nothing else.
    /// </summary>
    public static IReadOnlyList<IntegrationBranchGateObservation> Observations(
        Contract.ReviewPlanDto? plan,
        IReadOnlyList<Contract.ReviewCommandEvidenceDto> commands,
        string branch,
        DateTime observedAtUtc)
    {
        if (plan is null || string.IsNullOrWhiteSpace(branch)) return [];
        var observations = new List<IntegrationBranchGateObservation>();
        foreach (var evidence in commands)
        {
            if (!string.Equals(evidence.Phase, "verification", StringComparison.Ordinal)
                || !string.Equals(evidence.WorkspaceRole, "candidate", StringComparison.Ordinal))
                continue;
            var planned = plan.Commands.FirstOrDefault(command =>
                string.Equals(command.StepId, evidence.StepId, StringComparison.Ordinal));
            if (planned is null
                || Contract.ReviewCommandKinds.IsAgent(planned.ExecutionKind)
                || !planned.CompareToBaseline)
                continue;

            var owner = Contract.ReviewFailureAttributionPolicy.Attribute(planned, evidence);
            var red = owner == Contract.ReviewFailureOwner.IntegrationBranch;
            // A gate that was never run on the merge base proves nothing about
            // the branch, so it is reported without a commit: it clears a stale
            // red flag and never names one.
            var measured = evidence.BaselineSha is { Length: > 0 } && evidence.BaselineExitCode is not null;
            observations.Add(new IntegrationBranchGateObservation(
                branch,
                evidence.StepId,
                CommandLine(planned),
                red,
                red || (measured && evidence.BaselineExitCode == 0) ? evidence.BaselineSha : null,
                observedAtUtc));
        }
        return observations;
    }

    /// <summary>
    /// Records the report's measurements, appends the card's timeline entry for
    /// each integration-branch defect, and alerts the operator feed once per
    /// green-to-red transition. Never throws into the report path.
    /// </summary>
    public async Task<IReadOnlyList<IntegrationBranchGateFinding>> RecordAsync(
        TaskInfo task,
        Contract.ReviewPlanDto? plan,
        IReadOnlyList<Contract.ReviewCommandEvidenceDto> commands,
        string? branch,
        string? attemptId,
        DateTime observedAtUtc,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(branch)) return [];
        var observations = Observations(plan, commands, branch!, observedAtUtc);
        if (observations.Count == 0) return [];

        var findings = new List<IntegrationBranchGateFinding>();
        foreach (var observation in observations)
        {
            IntegrationBranchGateDecision decision;
            try
            {
                decision = string.IsNullOrWhiteSpace(_workspaceRoot)
                    ? IntegrationBranchGateHealthPolicy.Decide(previous: null, observation)
                    : IntegrationBranchGateHealthStore.Apply(
                        _workspaceRoot!,
                        task.ProjectName,
                        observation);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "IntegrationBranchGateReporter: could not record {Step} on {Branch} for {Project}.",
                    observation.StepId,
                    observation.Branch,
                    task.ProjectName);
                continue;
            }

            if (!observation.Red) continue;

            var finding = new IntegrationBranchGateFinding(
                observation.StepId,
                observation.Command,
                observation.MergeBaseSha ?? string.Empty,
                decision.Summary,
                decision.Next.LastGreenSha);
            findings.Add(finding);
            AppendTimeline(task, observation, decision, attemptId);

            if (!decision.ShouldAlert) continue;
            try
            {
                await _bus.EmitIntegrationBranchGateRedAsync(
                    task.ProjectName,
                    observation.Branch,
                    observation.StepId,
                    observation.Command,
                    observation.MergeBaseSha,
                    decision.Next.LastGreenSha,
                    decision.Summary,
                    task.Id,
                    ct).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "IntegrationBranchGateReporter: operator alert for {Step} on {Branch} failed.",
                    observation.StepId,
                    observation.Branch);
            }
        }
        return findings;
    }

    private void AppendTimeline(
        TaskInfo task,
        IntegrationBranchGateObservation observation,
        IntegrationBranchGateDecision decision,
        string? attemptId)
    {
        try
        {
            _timeline.Append(
                task.FolderPath,
                TimelineEventKinds.IntegrationBranchGateDefect,
                TimelineActors.System,
                decision.Summary,
                runId: attemptId,
                details: new Dictionary<string, string>
                {
                    ["branch"] = observation.Branch,
                    ["step"] = observation.StepId,
                    ["command"] = observation.Command,
                    ["mergeBase"] = observation.MergeBaseSha ?? string.Empty,
                    ["lastGreen"] = decision.Next.LastGreenSha ?? string.Empty,
                    ["transition"] = decision.Transition.ToString(),
                });
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "IntegrationBranchGateReporter: timeline append for {TaskId} failed.",
                task.Id);
        }
    }

    private static string CommandLine(Contract.ReviewCommandDto command)
        => command.Arguments.Count == 0
            ? command.FileName
            : $"{command.FileName} {string.Join(' ', command.Arguments)}";
}
