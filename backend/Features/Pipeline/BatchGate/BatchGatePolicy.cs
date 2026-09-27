using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Pipeline;

public enum BatchExclusionReason
{
    MissingEnvelope,
    MissingImmutableResultRef,
    MissingResultSha,
    MissingRunAttempt,
    MissingDeliveryEpoch,
    MissingFencingToken,
    ReviewNotPassed,
    ReviewSubjectMismatch,
    BuildTestNotDeferred,
    MissingReviewCompletion,
    MissingEnqueueSequence,
    SupersededGeneration,
    ProjectMismatch,
    RepositoryMismatch,
    IntegrationBranchMismatch,
    GateProfileMismatch,
    PlatformVersionMismatch,
    OtherBatchOwnsMember,
    ActivePerTaskGate,
    NotDocumentationOnly,
    NextBatch,
}

public sealed record BatchGateScope(
    string Project, string Repository, string IntegrationBranch,
    string GateProfile, string GateProfileDigest, string PlatformVersion);

public sealed record BatchGateSubject(
    string TaskKey, string Project, string Repository, string IntegrationBranch,
    string GateProfile, string GateProfileDigest, string PlatformVersion,
    string ResultRef, string ResultSha, string RunAttempt, long DeliveryEpoch,
    long FencingToken, string ReviewSubjectSha, bool SettledEnvelope,
    bool ModelReviewPassed, bool BuildTestDeferredToBatch,
    bool CurrentGeneration, bool ActivePerTaskGate, string? OwningBatchId,
    bool DocumentationOnly, DateTimeOffset ReviewCompletedAtUtc,
    long EnqueueSequence);

public sealed record BatchGateExclusion(string TaskKey, BatchExclusionReason Reason);

public sealed record BatchGateFormationOptions(
    bool Enabled = false, int CloseSize = 4, int MaximumSize = 8,
    int DeadlineMinutes = 15, int PressureSize = 2, int ReviewQueuePressure = 12,
    bool DocumentationOnly = true, int MaximumDiagnosticRuns = 12)
{
    public bool IsValid => CloseSize > 0 && MaximumSize >= CloseSize
        && MaximumSize <= 8 && DeadlineMinutes > 0 && PressureSize > 0
        && PressureSize <= CloseSize && ReviewQueuePressure > 0
        && MaximumDiagnosticRuns >= 0 && MaximumDiagnosticRuns <= 64;
}

public sealed record BatchGateManifest(
    string BatchId, BatchGateScope Scope, string BaseSha, string MembershipDigest,
    DateTimeOffset ClosedAtUtc, IReadOnlyList<BatchGateSubject> Members,
    IReadOnlyList<BatchGateExclusion> Exclusions,
    IReadOnlyList<string> EligibleKeys,
    bool IsDiagnostic = false);

public sealed record BatchGateFormation(
    BatchGateManifest? Manifest, IReadOnlyList<BatchGateExclusion> Exclusions,
    IReadOnlyList<string> EligibleKeys, bool UsePerTaskGate);

public static class BatchGatePolicy
{
    public static BatchGateScope ScopeOf(BatchGateSubject subject)
        => new(subject.Project, subject.Repository, subject.IntegrationBranch,
            subject.GateProfile, subject.GateProfileDigest, subject.PlatformVersion);

    public static BatchGateFormation Form(
        IEnumerable<BatchGateSubject> candidates, BatchGateScope scope,
        string baseSha, BatchGateFormationOptions options,
        DateTimeOffset now, int reviewQueueLength = 0, bool loadThrottled = false)
    {
        if (!options.IsValid) throw new ArgumentOutOfRangeException(nameof(options));
        var eligible = new List<BatchGateSubject>();
        var exclusions = new List<BatchGateExclusion>();
        foreach (var subject in candidates)
        {
            var reason = Exclusion(subject, scope, options);
            if (reason is { } excluded) exclusions.Add(new(subject.TaskKey, excluded));
            else eligible.Add(subject);
        }
        eligible.Sort(Compare);
        var keys = eligible.Select(x => x.TaskKey).ToArray();
        if (!options.Enabled || eligible.Count == 0)
            return new(null, exclusions, keys, UsePerTaskGate: !options.Enabled);

        var deadline = now - eligible[0].ReviewCompletedAtUtc >= TimeSpan.FromMinutes(options.DeadlineMinutes);
        var pressure = eligible.Count >= options.PressureSize
            && (reviewQueueLength >= options.ReviewQueuePressure || loadThrottled);
        if (eligible.Count < options.CloseSize && !deadline && !pressure)
            return new(null, exclusions, keys, false);
        if (eligible.Count == 1 && deadline)
            return new(null, exclusions, keys, true);

        var selected = eligible.Take(options.MaximumSize).ToArray();
        exclusions.AddRange(eligible.Skip(options.MaximumSize)
            .Select(x => new BatchGateExclusion(x.TaskKey, BatchExclusionReason.NextBatch)));
        var digest = MembershipDigest(baseSha, scope, selected);
        var manifest = new BatchGateManifest(
            Guid.NewGuid().ToString("N"), scope, baseSha, digest, now,
            selected, exclusions.ToArray(), keys);
        return new(manifest, exclusions, keys, false);
    }

    public static BatchExclusionReason? Exclusion(
        BatchGateSubject s, BatchGateScope scope, BatchGateFormationOptions options)
    {
        if (!s.SettledEnvelope) return BatchExclusionReason.MissingEnvelope;
        if (string.IsNullOrWhiteSpace(s.ResultRef)
            || !s.ResultRef.StartsWith("refs/heads/agent-studio/results/", StringComparison.Ordinal))
            return BatchExclusionReason.MissingImmutableResultRef;
        if (!IsSha(s.ResultSha)) return BatchExclusionReason.MissingResultSha;
        if (string.IsNullOrWhiteSpace(s.RunAttempt)) return BatchExclusionReason.MissingRunAttempt;
        if (s.DeliveryEpoch <= 0) return BatchExclusionReason.MissingDeliveryEpoch;
        if (s.FencingToken <= 0) return BatchExclusionReason.MissingFencingToken;
        if (!s.ModelReviewPassed) return BatchExclusionReason.ReviewNotPassed;
        if (!string.Equals(s.ReviewSubjectSha, s.ResultSha, StringComparison.OrdinalIgnoreCase))
            return BatchExclusionReason.ReviewSubjectMismatch;
        if (!s.BuildTestDeferredToBatch) return BatchExclusionReason.BuildTestNotDeferred;
        if (s.ReviewCompletedAtUtc == default) return BatchExclusionReason.MissingReviewCompletion;
        if (s.EnqueueSequence <= 0) return BatchExclusionReason.MissingEnqueueSequence;
        if (!s.CurrentGeneration) return BatchExclusionReason.SupersededGeneration;
        if (!Same(s.Project, scope.Project)) return BatchExclusionReason.ProjectMismatch;
        if (!Same(s.Repository, scope.Repository)) return BatchExclusionReason.RepositoryMismatch;
        if (!Same(s.IntegrationBranch, scope.IntegrationBranch)) return BatchExclusionReason.IntegrationBranchMismatch;
        if (!Same(s.GateProfile, scope.GateProfile)
            || !Same(s.GateProfileDigest, scope.GateProfileDigest))
            return BatchExclusionReason.GateProfileMismatch;
        if (!Same(s.PlatformVersion, scope.PlatformVersion)) return BatchExclusionReason.PlatformVersionMismatch;
        if (!string.IsNullOrWhiteSpace(s.OwningBatchId)) return BatchExclusionReason.OtherBatchOwnsMember;
        if (s.ActivePerTaskGate) return BatchExclusionReason.ActivePerTaskGate;
        if (options.DocumentationOnly && !s.DocumentationOnly) return BatchExclusionReason.NotDocumentationOnly;
        return null;
    }

    public static string MembershipDigest(
        string baseSha, BatchGateScope scope, IReadOnlyList<BatchGateSubject> orderedMembers)
    {
        if (!IsSha(baseSha) || orderedMembers.Count == 0)
            throw new ArgumentException("A batch digest requires a base SHA and members.");
        // A fixed array representation is deliberately independent of object property order.
        var canonical = new object[]
        {
            1, baseSha.ToLowerInvariant(), scope.Project, scope.Repository,
            scope.IntegrationBranch, scope.GateProfile, scope.GateProfileDigest,
            scope.PlatformVersion,
            orderedMembers.Select((s, i) => new object[]
            {
                i, s.TaskKey, s.ResultRef, s.ResultSha.ToLowerInvariant(),
                s.RunAttempt, s.DeliveryEpoch, s.FencingToken,
                s.ReviewSubjectSha.ToLowerInvariant(), s.ModelReviewPassed,
                s.BuildTestDeferredToBatch, s.DocumentationOnly,
                s.ReviewCompletedAtUtc.ToUniversalTime().ToString("O"), s.EnqueueSequence,
            }).ToArray(),
        };
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical)));
    }

    public static string CandidateRef(BatchGateManifest manifest, long coordinatorFence)
    {
        if (coordinatorFence <= 0) throw new ArgumentOutOfRangeException(nameof(coordinatorFence));
        return $"refs/agent-studio/batch-candidates/{manifest.BatchId}/{manifest.MembershipDigest}/{coordinatorFence}";
    }

    public static bool CascadeGuard(int totalMembers, int conflicts, int consecutiveConflicts)
        => totalMembers > 0 && (conflicts * 4 > totalMembers || consecutiveConflicts >= 3);

    public static int DiagnosticRunsPerCycle(int memberCount)
        => memberCount <= 1 ? 0 : (int)Math.Ceiling(Math.Log2(memberCount));

    public static BatchSupersedeAction Supersede(BatchPhase phase)
        => phase switch
        {
            BatchPhase.Formed or BatchPhase.Assembling or BatchPhase.Assembled
                => BatchSupersedeAction.EjectAndReconstruct,
            BatchPhase.Running => BatchSupersedeAction.StopAndDiscardVerdict,
            BatchPhase.Green or BatchPhase.Publishing => BatchSupersedeAction.BlockPublication,
            BatchPhase.Published => BatchSupersedeAction.PreserveHistory,
            _ => BatchSupersedeAction.BlockPublication,
        };

    private static int Compare(BatchGateSubject a, BatchGateSubject b)
    {
        var byTime = a.ReviewCompletedAtUtc.CompareTo(b.ReviewCompletedAtUtc);
        if (byTime != 0) return byTime;
        var bySequence = a.EnqueueSequence.CompareTo(b.EnqueueSequence);
        return bySequence != 0 ? bySequence : StringComparer.Ordinal.Compare(a.TaskKey, b.TaskKey);
    }

    private static bool Same(string a, string b)
        => !string.IsNullOrWhiteSpace(a) && string.Equals(a, b, StringComparison.Ordinal);

    private static bool IsSha(string value)
        => value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);
}

public enum BatchPhase { Formed, Assembling, Assembled, Running, Green, Publishing, Published, Red, Paused, Abandoned }
public enum BatchSupersedeAction { EjectAndReconstruct, StopAndDiscardVerdict, BlockPublication, PreserveHistory }
