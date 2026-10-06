using System.Text.RegularExpressions;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>A flake label requires a successful repeat of the same command on the same tree.</summary>
public static partial class ReviewFlakeEvidencePolicy
{
    public const string MissingProof = "No passing repeat of the same failing command on the same tree was recorded.";

    public static IEnumerable<ReviewCommandEvidenceDto> UnprovenFailures(ReviewReportRequest report)
        => report.Commands.Where(command => command.Phase == "verification"
            && command.WorkspaceRole == "candidate" && command.NewFailures is { Count: > 0 }
            && command.Diagnosis?.Classification == DeliveryFailureDiagnosis.FirstOccurrence
            && command.Diagnosis.Evidence.Contains(MissingProof));

    public static bool HasPassingRepeat(ReviewCommandEvidenceDto command, ReviewReportRequest report)
        => !string.IsNullOrWhiteSpace(command.TreeBefore)
           && string.Equals(command.HeadBefore, report.Workspace.ExpectedResultSha, StringComparison.OrdinalIgnoreCase)
           && report.Commands.Any(repeat => repeat.StepId == command.StepId
               && repeat.Phase == "clean-repeat" && repeat.WorkspaceRole == "clean-repeat"
               && repeat.ExitCode == 0 && repeat.Signal is null
               && repeat.FileName == command.FileName
               && repeat.Arguments.SequenceEqual(command.Arguments)
               && string.Equals(repeat.ExpectedResultSha, command.ExpectedResultSha, StringComparison.OrdinalIgnoreCase)
               && string.Equals(repeat.HeadBefore, command.HeadBefore, StringComparison.OrdinalIgnoreCase)
               && string.Equals(repeat.TreeBefore, command.TreeBefore, StringComparison.OrdinalIgnoreCase));

    public static ReviewReportRequest Normalize(ReviewReportRequest report)
    {
        var candidates = report.Commands.Where(command => command.Phase == "verification"
            && command.WorkspaceRole == "candidate" && !ReviewCommandKinds.IsAgent(command.ExecutionKind)).ToArray();
        var unsupported = candidates.Where(command =>
                (command.FlakyQuarantinedFailures is { Count: > 0 }
                 || command.Diagnosis?.Classification == DeliveryFailureDiagnosis.Intermittent)
                && !HasPassingRepeat(command, report))
            .ToArray();
        bool ProvenVerdict(ReviewVerdictDto verdict)
        {
            var matching = candidates.Where(command => command.Aspect == verdict.Aspect
                && (command.ExitCode != 0 || command.FlakyQuarantinedFailures is { Count: > 0 })).ToArray();
            return matching.Length > 0 && matching.All(command => HasPassingRepeat(command, report));
        }
        var unsupportedVerdicts = report.Verdicts.Where(verdict =>
            (ClaimsFlake(verdict.Classification) || ClaimsFlake(verdict.Summary))
            && !ProvenVerdict(verdict)).ToArray();
        if (unsupported.Length == 0 && unsupportedVerdicts.Length == 0) return report;

        var diagnosis = new DeliveryFailureDiagnosisResult(DeliveryFailureDiagnosis.FirstOccurrence,
            0.2, [MissingProof]);
        return report with
        {
            Outcome = "ReviewInfra",
            FailureClassification = DeliveryFailureDiagnosis.FirstOccurrence,
            Summary = "Undecidable review failure. " + MissingProof,
            Commands = report.Commands.Select(command => unsupported.Contains(command)
                ? command with
                {
                    // Keep the failed items available to the existing fingerprint counter.
                    NewFailures = (command.NewFailures ?? []).Concat(command.FlakyQuarantinedFailures ?? [])
                        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                    FlakyQuarantinedFailures = [],
                    Diagnosis = diagnosis,
                }
                : command).ToArray(),
            Verdicts = report.Verdicts.Select(verdict => unsupportedVerdicts.Contains(verdict)
                ? verdict with
                {
                    Status = "concerns",
                    Classification = DeliveryFailureDiagnosis.FirstOccurrence,
                    Summary = "Undecidable review failure. " + MissingProof,
                    Missing = MissingProof,
                    Diagnosis = diagnosis,
                }
                : verdict).ToArray(),
        };
    }

    private static bool ClaimsFlake(string? text) => text is not null
        && (text.Contains(ReviewFlakyQuarantine.Classification, StringComparison.OrdinalIgnoreCase)
            || text == DeliveryFailureDiagnosis.Intermittent || FlakeWord().IsMatch(
                text.Replace("0 flaky quarantined failures", "", StringComparison.OrdinalIgnoreCase)));

    // Match an affirmative attribution, not discussion of the flake policy.
    [GeneratedRegex(@"\b(?:is|are|was|were|as|known|existing|pre-existing)\s+(?:(?:a|an|the|known|existing|pre-existing|test|baseline)\s+)*(?:flake|flaky|flakiness)\b|\b[1-9]\d*\s+flaky\s+(?:quarantined\s+)?failures?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FlakeWord();
}
