namespace AgentStudio.TaskServer.Contracts;

/// <summary>Prevents an unproven deterministic failure or uncited reviewer block from charging a card.</summary>
public static class ReviewReportDiagnosisPolicy
{
    public static ReviewReportRequest Normalize(ReviewReportRequest request, ReviewPlanDto? plan)
    {
        // Report admission is shared by the Task Server and monolith. A retry
        // flag alone cannot turn a failed or unobserved item into a flake.
        var flakeChecked = ReviewFlakeEvidencePolicy.Normalize(request);
        if (!ReferenceEquals(flakeChecked, request)
            || (request.FailureClassification == DeliveryFailureDiagnosis.FirstOccurrence
                && request.Summary == "Undecidable review failure. " + ReviewFlakeEvidencePolicy.MissingProof))
            return flakeChecked;
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
        string? unproven = null;
        string? nonProduct = null;
        var confirmedProduct = false;
        var integrationBranchDefect = false;
        foreach (var command in failed)
        {
            var clean = request.Commands.FirstOrDefault(repeat => repeat.StepId == command.StepId
                && repeat.Phase == "clean-repeat" && repeat.WorkspaceRole == "clean-repeat");
            var precedingBuild = command.Arguments.Any(argument =>
                    argument.Contains("--no-build", StringComparison.Ordinal))
                ? plan?.Commands.TakeWhile(step => step.StepId != command.StepId)
                    .LastOrDefault(step => Path.GetFileName(step.FileName) is "dotnet" or "sh" or "bash" or "dash"
                        && step.Arguments.Any(argument => argument.Contains("dotnet build", StringComparison.Ordinal)
                            || argument == "build"))
                : null;
            var blockedByBaselineBuild = precedingBuild is not null
                && request.Commands.Any(baseline => baseline.StepId == precedingBuild.StepId
                    && baseline.Phase == "verification"
                    && baseline.WorkspaceRole.StartsWith("baseline-", StringComparison.Ordinal)
                    && baseline.BaselineSha == command.BaselineSha
                    && baseline.ExitCode is > 0);
            if (command.Diagnosis?.Classification == "IntegrationBranchDefect")
            {
                if (command.BaselineExitCode is > 0 && (clean is not null || blockedByBaselineBuild))
                    integrationBranchDefect = true;
                else
                    unproven ??= "DiagnosisEvidenceInvalid";
                continue;
            }
            // A no-build test cannot run in a clean clone when its prerequisite
            // build fails. The red build is recorded under its own planned step.
            if (clean is null && command.Arguments.Any(argument =>
                    argument.Contains("--no-build", StringComparison.Ordinal)))
            {
                clean = request.Commands.FirstOrDefault(repeat =>
                    repeat.StepId == precedingBuild?.StepId
                    && repeat.Phase == "clean-repeat"
                    && repeat.WorkspaceRole == "clean-repeat"
                    && repeat.ExitCode is > 0);
            }
            if (command.Diagnosis is null
                || command.BaselineExitCode is null
                || clean is null)
            {
                unproven ??= "DiagnosisMissing";
                continue;
            }
            if (command.Diagnosis.ChargesCard
                && (command.BaselineExitCode != 0 || clean.ExitCode == 0))
            {
                unproven ??= "DiagnosisEvidenceInvalid";
                continue;
            }
            if (command.Diagnosis.ChargesCard)
                confirmedProduct = true;
            else
                nonProduct ??= command.Diagnosis.Classification;
        }
        // Every failed command must be diagnosed before the report may charge a card.
        if (unproven is not null)
            return request with { Outcome = "ReviewInfra", FailureClassification = unproven };
        if (confirmedProduct)
            return request with
            {
                Outcome = "ProductFailure",
                FailureClassification = DeliveryFailureDiagnosis.Product,
            };
        if (nonProduct is not null)
            return request with { Outcome = "ReviewInfra", FailureClassification = nonProduct };
        if (integrationBranchDefect)
            return request with
            {
                Outcome = "IntegrationBranchDefect",
                FailureClassification = "IntegrationBranchDefect",
            };
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
