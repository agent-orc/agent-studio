using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Review;

/// <summary>
/// Builds the canonical <see cref="ReviewRoundRecord"/> for a remote review
/// round straight from the accepted fenced report. The rendered
/// <c>remote-review-grade-*.md</c> stays an artifact the record points at; it is
/// never parsed back to recover this state.
/// </summary>
public static class RemoteReviewRoundRecordFactory
{
    public static ReviewRoundRecord Create(
        string attemptId,
        Contract.ReviewReportRequest request,
        DateTime receivedAt,
        string? reportRef,
        ReviewRoundDeliveryGate? deliveryGate = null)
    {
        var buildVerdicts = request.Verdicts
            .Where(verdict => ReviewRoundBuildTestsPairing.IsBuildTestsAspect(verdict.Aspect))
            .Select(verdict => new ReviewRoundVerdictInput(verdict.Aspect, verdict.Status, verdict.Summary))
            .ToList();
        var buildCommands = request.Commands
            .Where(command => ReviewRoundBuildTestsPairing.IsBuildVerifyStep(command.StepId)
                              || ReviewRoundBuildTestsPairing.IsBuildTestsAspect(command.Aspect))
            .Select(command => new ReviewRoundCommandInput(
                command.StepId,
                CommandLine(command),
                command.ExitCode))
            .ToList();

        return new ReviewRoundRecord
        {
            Plane = ReviewPlanes.Remote,
            AttemptId = attemptId,
            SubjectSha = string.IsNullOrWhiteSpace(request.Workspace.ActualHead)
                ? request.Workspace.ExpectedResultSha
                : request.Workspace.ActualHead,
            ReceivedAt = receivedAt,
            Outcome = string.IsNullOrWhiteSpace(request.FailureClassification)
                ? request.Outcome
                : $"{request.Outcome} / {request.FailureClassification}",
            Summary = request.Summary?.Trim() ?? "",
            ReportRef = reportRef,
            BuildTests = ReviewRoundBuildTestsPairing.Pair(buildVerdicts, buildCommands),
            Aspects = request.Verdicts
                .Where(verdict => !ReviewRoundBuildTestsPairing.IsBuildTestsAspect(verdict.Aspect))
                .Select(verdict => new ReviewRoundAspect
                {
                    Name = verdict.Aspect,
                    Verdict = ReviewVerdicts.Normalize(verdict.Status),
                    Summary = verdict.Summary.Trim(),
                })
                .ToList(),
            DeliveryGate = deliveryGate,
        };
    }

    /// <summary>
    /// The command as the report renders it, so the record and the Markdown
    /// artifact quote the same line back to the operator.
    /// </summary>
    private static string CommandLine(Contract.ReviewCommandEvidenceDto command) =>
        Contract.ReviewCommandKinds.IsAgent(command.ExecutionKind)
            ? $"{command.FileName} read-only ({command.Model ?? "default model"})"
            : string.Join(' ', new[] { command.FileName }.Concat(command.Arguments));
}
