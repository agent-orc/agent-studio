namespace AgentStudio.TaskServer.Contracts;

/// <summary>Checks class repeats and exact assertion controls against the failed run.</summary>
public static class ReviewRepeatFilterPolicy
{
    public static bool IsValid(ReviewCommandEvidenceDto repeat, IReadOnlyList<ReviewCommandEvidenceDto> commands)
    {
        if (repeat.Phase != "clean-repeat")
            return repeat.RepeatFilter is null && repeat.RepeatObservedTests is null
                && repeat.RepeatObservation is null;
        if (repeat.RepeatObservation is not null
            && (repeat.ExitCode != 0 || !ReviewRepeatObservation.IsKnown(repeat.RepeatObservation)))
            return false;
        if (repeat.RepeatFilter is null)
            return repeat.RepeatObservedTests is null;

        var candidate = commands.FirstOrDefault(command => command.StepId == repeat.StepId
            && command.Phase == "verification" && command.WorkspaceRole == "candidate");
        if (candidate is null || repeat.RepeatObservedTests is null) return false;
        var failed = (candidate.InitialFailures ?? (candidate.NewFailures ?? [])
            .Concat(candidate.PreExistingFailures ?? [])
            .Concat(candidate.FlakyQuarantinedFailures ?? []).ToArray())
            .Select(WithoutParameters)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (failed.Length is 0 or > 10 || failed.Any(name => !ValidTestName(name))) return false;
        var classes = failed.Select(name => name[..name.LastIndexOf('.')])
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (classes.Length is 0 or > 5) return false;
        var expectedFilter = string.Join('|', classes.Select(name => $"FullyQualifiedName~{name}."));
        if (!string.Equals(repeat.RepeatFilter, expectedFilter, StringComparison.Ordinal)
            || repeat.RepeatObservedTests.Any(name => !failed.Contains(name, StringComparer.Ordinal)))
            return false;
        return repeat.ExitCode != 0 || repeat.RepeatObservedTests.Order(StringComparer.Ordinal)
            .SequenceEqual(failed.Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    /// <summary>An exact control cannot identify an individual parameterized case by FQN alone.</summary>
    public static string? ExactTestFilter(string test)
        => ValidTestName(test) ? $"FullyQualifiedName={test}" : null;

    public const int MaximumAssertionControls = 3;

    public static bool RequiresAssertionProof(ReviewCommandEvidenceDto candidate, ReviewCommandEvidenceDto repeat)
        => candidate.InitialFailures is { Count: > 0 } initial
            && repeat.InitialFailures is { Count: > 0 } repeated
            && !initial.ToHashSet(StringComparer.Ordinal).SetEquals(repeated)
            && initial.Intersect(repeated, StringComparer.Ordinal)
                .Except(candidate.PreExistingFailures ?? [], StringComparer.Ordinal).Any();

    public static bool HasCompleteAssertionProof(
        ReviewAssertionEvidenceDto proof, ReviewCommandEvidenceDto candidate,
        IReadOnlyList<ReviewCommandEvidenceDto> commands)
    {
        if (proof.IncompleteReason is not null || string.IsNullOrWhiteSpace(proof.Fingerprint)
            || proof.Fingerprint != proof.RepeatFingerprint
            || proof.Filter is null || proof.Filter != ExactTestFilter(proof.Test)
            || candidate.InitialFailures?.Contains(proof.Test, StringComparer.Ordinal) != true
            || candidate.NewFailures?.Contains(proof.Test, StringComparer.Ordinal) != true
            || candidate.PreExistingFailures?.Contains(proof.Test, StringComparer.Ordinal) == true
            || proof.Repeat is not { Signal: null, ExitCode: > 0 and < 124 } repeat
            || proof.Control is not { Signal: null, ExitCode: 0 } control)
            return false;
        var baseline = commands.FirstOrDefault(item => item.StepId == candidate.StepId
            && item.Phase == "verification" && item.WorkspaceRole.StartsWith("baseline-", StringComparison.Ordinal)
            && item.BaselineSha == candidate.BaselineSha);
        return baseline?.Envelope is not null
            && control.Envelope?.DeclaredEnvironmentDigest == baseline.Envelope.DeclaredEnvironmentDigest
            && control.Envelope?.AllowlistDigest == baseline.Envelope.AllowlistDigest
            && control.TreeBefore == baseline.TreeBefore
            && repeat.ExpectedResultSha == candidate.ExpectedResultSha && control.ExpectedResultSha == candidate.ExpectedResultSha
            && !string.IsNullOrEmpty(candidate.AttemptId)
            && repeat.AttemptId == candidate.AttemptId && control.AttemptId == candidate.AttemptId
            && repeat.HostId == candidate.HostId && control.HostId == candidate.HostId
            && repeat.ExecutorId == candidate.ExecutorId && control.ExecutorId == candidate.ExecutorId
            && !repeat.BaselineCacheHit && !control.BaselineCacheHit
            && !repeat.DependencyCacheHit && !control.DependencyCacheHit
            && repeat.InitialFailures is { Count: 1 } && repeat.InitialFailures[0] == proof.Test
            && control.RepeatObservedTests?.SequenceEqual([proof.Test], StringComparer.Ordinal) == true
            && repeat.RepeatFilter == proof.Filter && control.RepeatFilter == proof.Filter
            && repeat.AssertionEvidence.Count == 0 && control.AssertionEvidence.Count == 0
            && repeat.StepId == candidate.StepId && control.StepId == candidate.StepId
            && repeat.FileName == candidate.FileName
            && repeat.Arguments.SequenceEqual(candidate.Arguments, StringComparer.Ordinal)
            && repeat.FileName == control.FileName
            && repeat.Arguments.SequenceEqual(control.Arguments, StringComparer.Ordinal)
            && repeat.HeadBefore == candidate.HeadBefore && repeat.TreeBefore == candidate.TreeBefore
            && control.HeadBefore == candidate.BaselineSha && !string.IsNullOrEmpty(control.TreeBefore)
            && repeat.Envelope is not null && control.Envelope is not null && candidate.Envelope is not null
            && repeat.Envelope.DeclaredEnvironmentDigest == candidate.Envelope.DeclaredEnvironmentDigest
            && repeat.Envelope.AllowlistDigest == candidate.Envelope.AllowlistDigest
            && repeat.Envelope.ProcessorCount == candidate.Envelope.ProcessorCount
            && repeat.Envelope.ProcessorCount == control.Envelope.ProcessorCount
            && repeat.Envelope.CgroupKind == control.Envelope.CgroupKind
            && repeat.Envelope.CpuMax == control.Envelope.CpuMax;
    }

    private static string WithoutParameters(string name) => name.Split('(', 2)[0];

    private static bool ValidTestName(string name)
    {
        var separator = name.LastIndexOf('.');
        return separator > 0 && separator < name.Length - 1
            && name.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '_' or '.' or '+' or '`');
    }
}
