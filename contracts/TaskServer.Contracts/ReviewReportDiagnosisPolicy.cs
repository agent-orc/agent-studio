namespace AgentStudio.TaskServer.Contracts;

/// <summary>Prevents an unproven deterministic failure or uncited reviewer block from charging a card.</summary>
public static class ReviewReportDiagnosisPolicy
{
    public static ReviewReportRequest Normalize(ReviewReportRequest request, ReviewPlanDto? plan)
    {
        var semanticAspects = plan?.Commands
            .Where(command => ReviewCommandKinds.IsAgent(command.ExecutionKind))
            .Select(command => command.Aspect)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var verdicts = request.Verdicts.Select(verdict => semanticAspects.Contains(verdict.Aspect)
            ? ReviewDiffEvidencePolicy.Normalize(verdict, request.Workspace.ChangedPaths ?? [])
            : verdict).ToArray();
        request = request with { Verdicts = verdicts };

        var failed = request.Commands.Where(command =>
            command.Phase == "verification"
            && command.WorkspaceRole == "candidate"
            && command.ExitCode != 0
            && !ReviewCommandKinds.IsAgent(command.ExecutionKind)).ToArray();
        foreach (var command in failed)
        {
            var clean = request.Commands.FirstOrDefault(repeat => repeat.StepId == command.StepId
                && repeat.Phase == "clean-repeat" && repeat.WorkspaceRole == "clean-repeat");
            if (command.Diagnosis is null
                || command.BaselineExitCode is null
                || clean is null)
                return request with { Outcome = "ReviewInfra", FailureClassification = "DiagnosisMissing" };
            if (command.Diagnosis.ChargesCard
                && (command.BaselineExitCode != 0 || clean.ExitCode == 0))
                return request with { Outcome = "ReviewInfra", FailureClassification = "DiagnosisEvidenceInvalid" };
            if (!command.Diagnosis.ChargesCard)
                return request with
                {
                    Outcome = "ReviewInfra",
                    FailureClassification = command.Diagnosis.Classification,
                };
        }
        if (string.Equals(request.Outcome, "ProductFailure", StringComparison.OrdinalIgnoreCase)
            && failed.Length == 0
            && ReviewGradingPolicy.Grade(verdicts
                .Where(verdict => verdict.Diagnosis?.ChargesCard == true)
                .Select(verdict => verdict.Status))
                != ReviewGrade.ProductFailure)
            return request with { Outcome = "Pass", FailureClassification = null };
        return request;
    }
}
