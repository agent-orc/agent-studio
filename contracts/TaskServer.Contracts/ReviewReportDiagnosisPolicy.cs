namespace AgentStudio.TaskServer.Contracts;

/// <summary>Prevents an unproven deterministic failure or uncited reviewer block from charging a card.</summary>
public static class ReviewReportDiagnosisPolicy
{
    /// <summary>
    /// A review worker that predates the AGT-2916 diagnosis contract reported a
    /// failed deterministic command without a diagnosis and without a clean
    /// repeat (AGT-3089). The same worker gives the same report on every retry
    /// of the unchanged subject, so this result is escalated instead of retried.
    /// </summary>
    public const string DiagnosisContractUnsupported = "DiagnosisContractUnsupported";

    /// <summary>
    /// True when a report has the shape only a pre-diagnosis worker produces:
    /// the worker did not end with its own ReviewInfra classification, a failed
    /// deterministic candidate command carries no diagnosis, and the report has
    /// no clean-repeat evidence at all. A current worker either diagnoses every
    /// failed deterministic command or ends with ReviewInfra itself.
    /// </summary>
    public static bool IsDiagnosisContractUnsupported(ReviewReportRequest request)
        => !string.Equals(request.Outcome, "ReviewInfra", StringComparison.OrdinalIgnoreCase)
           && FailedDeterministicCandidates(request).Any(command => command.Diagnosis is null)
           && !request.Commands.Any(command => command.Phase == "clean-repeat");

    /// <summary>
    /// Whether a ReviewInfra result may be retried on the unchanged subject.
    /// Only <see cref="DiagnosisContractUnsupported"/> is excluded; every other
    /// infrastructure class keeps the bounded retry.
    /// </summary>
    public static bool RetriesOnUnchangedSubject(string? failureClassification)
        => !string.Equals(failureClassification, DiagnosisContractUnsupported, StringComparison.Ordinal);

    /// <summary>Escalation reason for <see cref="DiagnosisContractUnsupported"/>.</summary>
    public static string DiagnosisContractUnsupportedReason(string executorId, string? workerReleaseId)
    {
        var release = ReviewWorkerProvenancePolicy.IsKnown(workerReleaseId)
            ? workerReleaseId!.Trim()
            : ReviewWorkerProvenancePolicy.Unknown;
        return $"Review worker {release} on {executorId} predates the diagnosis contract; upgrade the review host.";
    }

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
        if (IsDiagnosisContractUnsupported(request))
            return request with
            {
                Outcome = "ReviewInfra",
                FailureClassification = DiagnosisContractUnsupported,
            };

        var failed = FailedDeterministicCandidates(request);
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

    private static ReviewCommandEvidenceDto[] FailedDeterministicCandidates(ReviewReportRequest request)
        => request.Commands.Where(command =>
            command.Phase == "verification"
            && command.WorkspaceRole == "candidate"
            && command.ExitCode != 0
            && !ReviewCommandKinds.IsAgent(command.ExecutionKind)).ToArray();
}
