namespace AgentStudio.TaskServer.Contracts;

/// <summary>Stops an unproven review report from charging a card.</summary>
public static class ReviewDiagnosisAdmissionPolicy
{
    public static ReviewReportRequest NormalizeReport(
        ReviewReportRequest request,
        IEnumerable<ReviewCommandDto> plannedCommands)
    {
        if (!string.Equals(request.Outcome, "ProductFailure", StringComparison.OrdinalIgnoreCase))
            return request;

        var baselineRed = request.Commands.Any(command =>
            command.Phase == "verification"
            && command.BaselineExitCode is not null and not 0);
        if (baselineRed)
            return request with
            {
                Outcome = "IntegrationBranchDefect",
                FailureClassification = "BaselineRed",
                Summary = "The integration baseline failed; this report cannot charge the delivery.",
            };

        var planned = plannedCommands.ToArray();
        var semanticAspects = planned
            .Where(command => ReviewCommandKinds.IsAgent(command.ExecutionKind))
            .Select(command => command.Aspect)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var citedDiffBlock = request.Verdicts.Any(verdict =>
            semanticAspects.Contains(verdict.Aspect)
            && ReviewGradingPolicy.IsBlockingToken(verdict.Status)
            && ReviewVerdictCitationPolicy.HasCitation(verdict));
        var baselineSteps = planned
            .Where(command => command.CompareToBaseline)
            .Select(command => command.StepId)
            .ToHashSet(StringComparer.Ordinal);
        var confirmedGate = request.Commands.Any(command =>
            command.Phase == "verification"
            && baselineSteps.Contains(command.StepId)
            && command.DiagnosisClass == nameof(DeliveryFailureClass.Product)
            && !string.IsNullOrWhiteSpace(command.BaselineSha)
            && command.BaselineExitCode == 0
            && command.CleanRepeatExitCode is not null and not 0
            && command.CleanRepeatSameFingerprint
            && command.DiagnosisConfidence is >= 0.9
            && !command.FingerprintSeenOnOtherCardWithin24Hours);
        if (citedDiffBlock || confirmedGate) return request;

        var environment = request.Commands.Any(command =>
            command.DiagnosisClass == nameof(DeliveryFailureClass.Environment));
        return request with
        {
            Outcome = environment ? "ReviewInfra" : "Inconclusive",
            FailureClassification = environment ? "Environment" : "DiagnosisRequired",
            Summary = "Product failure was not confirmed by baseline, uncached repeat, and card-specific fingerprint evidence.",
        };
    }
}
