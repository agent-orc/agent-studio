using Contract = AgentStudio.TaskServer.Contracts;
namespace AgentStudio.OrchestrationDecisions;

public enum RemoteBuildTestGateClass
{
    Passed,
    NotApplicable,
    NonBlocking,
    Failed,
}

/// <param name="VerifyDefinitionMissing">
/// AGT-3428: the delivery is refused because the repository defines no verify
/// commands, not because a gate was red. The host records the typed
/// <c>verify-definition-missing</c> code, which no gate triage classifies.
/// </param>
public sealed record RemoteDeliveryIntegrationDecision(
    bool ShouldIntegrate,
    RemoteBuildTestGateClass BuildTestGate,
    string Reason,
    bool VerifyDefinitionMissing = false);

/// <summary>
/// Pure admission policy for immediate Remote delivery integration. A fenced
/// result is eligible only after its source run persisted a settled immutable
/// envelope, the Remote Review reached Pass, and its build/test measurement is
/// green, explicitly not applicable, or explicitly non-blocking with its limitation recorded. A plan that found no verify
/// definition at all (AGT-3428) is not admissible: nothing could check the
/// delivery, so it is refused with the plan's message instead of merged unverified.
/// </summary>
public static class RemoteDeliveryIntegrationPolicy
{
    public static RemoteDeliveryIntegrationDecision Decide(
        bool hasSettledResultEnvelope,
        string? reviewOutcome,
        Contract.ReviewPlanDto? reviewPlan,
        IReadOnlyList<Contract.ReviewVerdictDto> verdicts)
    {
        if (!hasSettledResultEnvelope)
        {
            return new RemoteDeliveryIntegrationDecision(
                false,
                RemoteBuildTestGateClass.Failed,
                "The source run has no settled immutable Result-Envelope.");
        }

        if (reviewPlan?.BuildTestDeferredToBatch == true)
            return new RemoteDeliveryIntegrationDecision(false,
                RemoteBuildTestGateClass.Failed,
                "The full build/test gate is deferred to a batch and has no per-card verdict.");

        // AGT-2819: a gate that was already red on the merge base is a defect of
        // the integration branch, not of this delivery. Refusing the card for it
        // stalled the whole board in Human Review for weeks while the deliveries
        // were merged by hand instead. The card does not make the branch worse,
        // so it is admissible; the branch defect carries its own operator alert.
        var integrationBranchDefect = string.Equals(
            reviewOutcome,
            ReviewOutcomeKinds.IntegrationBranchDefect,
            StringComparison.OrdinalIgnoreCase);
        if (!string.Equals(reviewOutcome, ReviewOutcomeKinds.Pass, StringComparison.OrdinalIgnoreCase)
            && !integrationBranchDefect)
        {
            return new RemoteDeliveryIntegrationDecision(
                false,
                RemoteBuildTestGateClass.Failed,
                $"Remote Review ended with '{reviewOutcome ?? "unknown"}', not Pass. "
                + ReviewMeasurementPolicy.Summary(reviewPlan, verdicts));
        }

        static bool IsBuildTestAspect(string aspect) =>
            string.Equals(aspect, "build-tests", StringComparison.OrdinalIgnoreCase)
            || string.Equals(aspect, "review-verification", StringComparison.OrdinalIgnoreCase);
        var buildTests = verdicts
            .Where(verdict => IsBuildTestAspect(verdict.Aspect))
            .ToList();
        if (!string.IsNullOrWhiteSpace(reviewPlan?.VerifyDefinitionMissing)
            && buildTests.All(IsNotApplicable))
        {
            return new RemoteDeliveryIntegrationDecision(
                false,
                RemoteBuildTestGateClass.NotApplicable,
                reviewPlan.VerifyDefinitionMissing,
                VerifyDefinitionMissing: true);
        }

        if (buildTests.Count == 0)
        {
            var buildTestWasPlanned = reviewPlan?.Commands.Any(command =>
                                          IsBuildTestAspect(command.Aspect)) == true
                                      || reviewPlan?.RequiredAspects.Any(IsBuildTestAspect) == true;
            if (reviewPlan is null || buildTestWasPlanned)
            {
                return new RemoteDeliveryIntegrationDecision(
                    false,
                    RemoteBuildTestGateClass.Failed,
                    reviewPlan is null
                        ? "The settled Remote Review has no frozen plan proving build/test is not applicable. Next automatic action: freeze a plan and rerun verification."
                        : "The Remote Review report omitted its planned build/test verdict. "
                          + ReviewMeasurementPolicy.Summary(reviewPlan, verdicts));
            }

            return new RemoteDeliveryIntegrationDecision(
                true,
                RemoteBuildTestGateClass.NotApplicable,
                "The Remote Review plan has no applicable build/test gate.");
        }

        var measurements = ReviewMeasurementPolicy.Measurements(reviewPlan, verdicts)
            .Where(item => ReviewMeasurementPolicy.IsBuildTestAspect(item.Aspect)).ToArray();
        if (measurements.Any(item => !item.Admits))
            return new(false, RemoteBuildTestGateClass.Failed,
                string.Join(" ", measurements.Where(item => !item.Admits).Select(item => item.Summary)));
        var gateClass = measurements.All(item => item.Disposition == ReviewMeasurementDisposition.NotApplicable)
            ? RemoteBuildTestGateClass.NotApplicable
            : measurements.Any(item => item.Disposition == ReviewMeasurementDisposition.NonBlocking)
                ? RemoteBuildTestGateClass.NonBlocking : RemoteBuildTestGateClass.Passed;
        return new(true, gateClass, string.Join(" ", measurements.Select(item => item.Summary))
            + (integrationBranchDefect ? " A gate was already red on the integration branch; its defect remains recorded." : ""));
    }

    private static bool IsNotApplicable(Contract.ReviewVerdictDto verdict)
        => ReviewMeasurementPolicy.IsNotApplicable(verdict);
}

public enum ReviewMeasurementDisposition
{
    Passed,
    NotApplicable,
    NonBlocking,
    ProductFailure,
    InfrastructureFailure,
    MissingVerdict,
}

public sealed record ReviewMeasurement(
    string StepId,
    string Aspect,
    ReviewMeasurementDisposition Disposition,
    string Detail,
    string NextAction)
{
    public bool Admits => Disposition is ReviewMeasurementDisposition.Passed
        or ReviewMeasurementDisposition.NotApplicable or ReviewMeasurementDisposition.NonBlocking;
    public string Summary => $"{StepId} ({Aspect}): {Disposition}: {Detail} Next automatic action: {NextAction}.";
}

/// <summary>One interpretation of measured review evidence for grading, settlement and admission.</summary>
public static class ReviewMeasurementPolicy
{
    public static bool IsBuildTestAspect(string aspect) =>
        string.Equals(aspect, "build-tests", StringComparison.OrdinalIgnoreCase)
        || string.Equals(aspect, "review-verification", StringComparison.OrdinalIgnoreCase);

    public static ReviewMeasurementDisposition Disposition(Contract.ReviewVerdictDto verdict)
    {
        if (Contract.ReviewGradingPolicy.IsBlockingToken(verdict.Status))
            return verdict.Diagnosis is { ChargesCard: false }
                ? ReviewMeasurementDisposition.InfrastructureFailure : ReviewMeasurementDisposition.ProductFailure;
        if (IsNotApplicable(verdict)) return ReviewMeasurementDisposition.NotApplicable;
        if (Contract.ReviewUnattributedTestFailurePolicy.IsNonBlocking(verdict.Diagnosis)
            || verdict.Classification is "IntegrationBranchDefect" or "TestQuarantine" or "FlakyQuarantine"
            || (verdict.Diagnosis is { ChargesCard: false, Classification: "IntegrationBranchDefect" }))
            return ReviewMeasurementDisposition.NonBlocking;
        if (verdict.Classification is Contract.DeliveryFailureDiagnosis.Environment
                or Contract.DeliveryFailureDiagnosis.Intermittent or Contract.DeliveryFailureDiagnosis.FirstOccurrence
            || Contract.ReviewCachedBaselinePolicy.IsCacheDefect(verdict.Classification))
            return ReviewMeasurementDisposition.InfrastructureFailure;
        if (string.Equals(verdict.Status, "pass", StringComparison.OrdinalIgnoreCase))
            return ReviewMeasurementDisposition.Passed;
        // An arbitrary concern is useful reviewer feedback, but is not proof
        // that a deterministic measurement passed.
        return IsBuildTestAspect(verdict.Aspect)
            ? ReviewMeasurementDisposition.InfrastructureFailure : ReviewMeasurementDisposition.NonBlocking;
    }

    public static IReadOnlyList<ReviewMeasurement> Measurements(
        Contract.ReviewPlanDto? plan, IReadOnlyList<Contract.ReviewVerdictDto> verdicts)
    {
        var measurements = new List<ReviewMeasurement>();
        var matched = new HashSet<Contract.ReviewVerdictDto>();
        foreach (var command in plan?.Commands ?? [])
        {
            var siblings = plan!.Commands.Count(item => item.Aspect == command.Aspect);
            var verdict = verdicts.FirstOrDefault(item => item.Aspect == command.Aspect
                && (CitesStep(item, command.StepId) || siblings == 1));
            measurements.Add(Measurement(command.StepId, command.Aspect, verdict, command));
            if (verdict is not null) matched.Add(verdict);
        }
        foreach (var aspect in plan?.RequiredAspects ?? [])
            if (!measurements.Any(item => item.Aspect == aspect))
            {
                var verdict = verdicts.FirstOrDefault(item => item.Aspect == aspect);
                measurements.Add(Measurement(aspect, aspect, verdict));
                if (verdict is not null) matched.Add(verdict);
            }
        foreach (var verdict in verdicts)
            if (!matched.Contains(verdict))
                measurements.Add(Measurement(verdict.EvidenceChecked ?? verdict.Aspect, verdict.Aspect, verdict));
        return measurements;
    }

    private static bool CitesStep(Contract.ReviewVerdictDto verdict, string stepId)
        => verdict.EvidenceChecked?.Split(';', StringSplitOptions.TrimEntries)
            .Contains("command:" + stepId, StringComparer.Ordinal) == true;

    private static ReviewMeasurement Measurement(string step, string aspect, Contract.ReviewVerdictDto? verdict, Contract.ReviewCommandDto? command = null)
    {
        var disposition = verdict is null ? ReviewMeasurementDisposition.MissingVerdict : Disposition(verdict);
        var action = disposition switch
        {
            ReviewMeasurementDisposition.ProductFailure => "send a fix round with the cited failure",
            ReviewMeasurementDisposition.InfrastructureFailure => "rerun the measurement with fresh evidence within the infrastructure retry budget",
            ReviewMeasurementDisposition.MissingVerdict => "rerun the omitted planned measurement before admission",
            ReviewMeasurementDisposition.NonBlocking => "continue delivery; retain this measurement limitation",
            _ => "continue delivery",
        };
        var detail = verdict is null ? "The frozen plan has no matching verdict." : $"{verdict.Classification}: {verdict.Summary}";
        if (command is not null) detail = $"{command.FileName} {string.Join(" ", command.Arguments)}; {detail}";
        return new(step, aspect, disposition, detail, action);
    }

    public static string Outcome(Contract.ReviewPlanDto? plan, IReadOnlyList<Contract.ReviewVerdictDto> verdicts)
    {
        var measurements = Measurements(plan, verdicts);
        if (measurements.Any(item => item.Disposition == ReviewMeasurementDisposition.ProductFailure)) return "ProductFailure";
        if (measurements.Any(item => !item.Admits)) return "ReviewInfra";
        return "Pass";
    }

    public static string Summary(Contract.ReviewPlanDto? plan, IReadOnlyList<Contract.ReviewVerdictDto> verdicts)
        => string.Join(" ", Measurements(plan, verdicts).Select(item => item.Summary));

    public static bool IsNotApplicable(Contract.ReviewVerdictDto verdict)
        => string.Equals(verdict.Status, "not-applicable", StringComparison.OrdinalIgnoreCase)
           || string.Equals(verdict.Status, "not_applicable", StringComparison.OrdinalIgnoreCase)
           || string.Equals(verdict.Classification, "NotApplicable", StringComparison.OrdinalIgnoreCase)
           || (string.Equals(verdict.Status, "skipped", StringComparison.OrdinalIgnoreCase)
               && string.Equals(verdict.Classification, "NoCommands", StringComparison.OrdinalIgnoreCase));

    /// <summary>Red delivery and clean repeat on a red exit-status baseline are branch debt, not a host crash.</summary>
    public static bool IsRedBaseline(
        Contract.ReviewCommandDto? planned, Contract.ReviewCommandEvidenceDto command,
        IReadOnlyList<Contract.ReviewCommandEvidenceDto> commands)
        => planned is { CompareToBaseline: true }
           && Contract.ReviewBaselineModes.IsExitStatus(planned.BaselineMode)
           && command.Signal is null && command.ExitCode is > 0 and < 124
           && command.BaselineExitCode is > 0 and < 124
           && !string.IsNullOrWhiteSpace(command.BaselineSha)
           && command.NewFailures is not { Count: > 0 }
           && command.Diagnosis is { ChargesCard: false, Classification: Contract.DeliveryFailureDiagnosis.Environment }
           && commands.Any(repeat => repeat.StepId == command.StepId && repeat.Phase == "clean-repeat"
               && repeat.WorkspaceRole == "clean-repeat" && repeat.Signal is null && repeat.ExitCode is > 0 and < 124);

    public static Contract.ReviewReportRequest NormalizeReport(
        Contract.ReviewReportRequest request, Contract.ReviewPlanDto? plan)
    {
        var commands = request.Commands.Select(command => IsRedBaseline(
            plan?.Commands.FirstOrDefault(step => step.StepId == command.StepId), command, request.Commands)
            ? command with { Diagnosis = new("IntegrationBranchDefect", 1,
                command.Diagnosis!.Evidence.Append($"{command.StepId} is red on delivery, clean repeat and baseline {command.BaselineSha}").ToArray()) }
            : command).ToArray();
        var verdicts = request.Verdicts.Select(verdict =>
        {
            var measured = commands.FirstOrDefault(command => command.WorkspaceRole == "candidate"
                && command.Diagnosis?.Classification == "IntegrationBranchDefect"
                && command.Aspect == verdict.Aspect && CitesStep(verdict, command.StepId));
            return measured is null || verdict.Classification == "IntegrationBranchDefect" ? verdict : verdict with
            {
                Status = "concerns", Classification = "IntegrationBranchDefect", Diagnosis = measured.Diagnosis,
                Summary = verdict.Summary + " Red baseline recorded; delivery continues without charging the card.",
            };
        }).ToArray();
        var normalized = Contract.ReviewReportDiagnosisPolicy.Normalize(request with { Commands = commands, Verdicts = verdicts }, plan);
        // Citation and diff normalization above may downgrade a purported
        // product block. Only a still-blocking, confirmed product finding wins.
        if (normalized.Verdicts.Any(verdict => verdict.Diagnosis?.ChargesCard == true
            && Contract.ReviewGradingPolicy.IsBlockingToken(verdict.Status)))
            return normalized with { Outcome = "ProductFailure",
                FailureClassification = normalized.Outcome is "ProductFailure" or "Pass" && !IsInfrastructureClassification(normalized.FailureClassification)
                    ? normalized.FailureClassification ?? "ReviewFinding"
                    : Contract.DeliveryFailureDiagnosis.Product,
                Summary = Summary(plan, normalized.Verdicts) };
        // Settlement shares the deterministic build/test dispositions with
        // delivery admission. Semantic aspects keep their grading path: a
        // reviewer concern or an omitted aspect verdict is not an incomplete
        // measurement, so an actionable concern still reaches its fix round
        // and the settled attempt is recorded as a delivered round (AGT-4243).
        var gates = Measurements(plan, normalized.Verdicts)
            .Where(item => IsBuildTestAspect(item.Aspect)).ToArray();
        if (normalized.Outcome is "Pass" or "IntegrationBranchDefect" && gates.Length > 0)
        {
            var outcome = gates.Any(item => item.Disposition == ReviewMeasurementDisposition.ProductFailure)
                ? "ProductFailure" : gates.Any(item => !item.Admits) ? "ReviewInfra" : "Pass";
            return normalized with { Outcome = outcome == "Pass" ? normalized.Outcome : outcome,
                FailureClassification = outcome == "ReviewInfra" ? "MeasurementIncomplete"
                    : IsInfrastructureClassification(normalized.FailureClassification) ? null : normalized.FailureClassification,
                Summary = string.Join(" ", gates.Select(item => item.Summary)) };
        }
        return normalized;
    }

    private static bool IsInfrastructureClassification(string? classification)
        => classification is Contract.DeliveryFailureDiagnosis.Environment
            or Contract.DeliveryFailureDiagnosis.Intermittent or Contract.DeliveryFailureDiagnosis.FirstOccurrence
            or "IntegrationBranchDefect"
            || (classification is not null && Contract.ReviewCachedBaselinePolicy.IsCacheDefect(classification));

    public static bool MustMeasureTargetMerge(string? baseline, string? target, int baselineExit, int candidateExit)
        => baselineExit > 0 && candidateExit > 0 && !string.IsNullOrWhiteSpace(target)
            && !string.Equals(baseline, target, StringComparison.OrdinalIgnoreCase);

    public static bool MayReuseResult(IReadOnlyList<string>? infrastructurePredecessors)
        => infrastructurePredecessors is not { Count: > 0 };
}
