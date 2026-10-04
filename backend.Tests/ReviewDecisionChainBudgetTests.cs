using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the per-card reissue budget: neither an automatic verdict nor an
/// operator continuation opens a new automatic budget.
/// </summary>
public class ReviewDecisionChainBudgetTests
{
    private const string Job = "job-1";
    private const string Other = "job-2";

    private static ReviewDecisionRecord Rec(
        ReviewDecisionKind kind,
        string jobId = Job,
        int? epoch = null)
        => new(
            CreatedAt: DateTime.UnixEpoch,
            JobId: jobId,
            Project: "demo",
            Kind: kind,
            Reason: kind.ToString(),
            Prompt: string.Empty,
            Response: string.Empty,
            FollowUp: string.Empty)
        {
            AttemptEpoch = epoch,
        };

    [Fact]
    public void NoRecords_IsZero()
        => Assert.Equal(0, ReviewDecisionOrchestrator.CountReissuesInCurrentChain(
            Array.Empty<ReviewDecisionRecord>(), Job));

    [Fact]
    public void OnlyReissues_CountsAll_LikeLifetimeTotal()
    {
        // No chain-ender in between: per-chain count == the old lifetime total, so
        // in-chain budget behaviour is unchanged.
        var records = new[]
        {
            Rec(ReviewDecisionKind.Reissue),
            Rec(ReviewDecisionKind.Reissue),
            Rec(ReviewDecisionKind.Reissue),
        };
        Assert.Equal(3, ReviewDecisionOrchestrator.CountReissuesInCurrentChain(records, Job));
    }

    [Fact]
    public void AutomaticEscalate_DoesNotResetBudget()
    {
        var records = new[]
        {
            Rec(ReviewDecisionKind.Reissue),
            Rec(ReviewDecisionKind.Reissue),
            Rec(ReviewDecisionKind.Escalate),
            Rec(ReviewDecisionKind.Reissue),
        };
        Assert.Equal(3, ReviewDecisionOrchestrator.CountReissuesInCurrentChain(records, Job));
    }

    [Fact]
    public void AutomaticAccept_DoesNotResetBudget()
    {
        var records = new[]
        {
            Rec(ReviewDecisionKind.Reissue),
            Rec(ReviewDecisionKind.AcceptAsDone),
            Rec(ReviewDecisionKind.Reissue),
            Rec(ReviewDecisionKind.Reissue),
        };
        Assert.Equal(3, ReviewDecisionOrchestrator.CountReissuesInCurrentChain(records, Job));
    }

    [Fact]
    public void OperatorRequeue_DoesNotResetCardBudget()
    {
        var records = new[]
        {
            Rec(ReviewDecisionKind.Reissue),
            Rec(ReviewDecisionKind.Reissue),
            Rec(ReviewDecisionKind.Escalate),
            Rec(ReviewDecisionKind.OperatorRequeue, epoch: 1),
            Rec(ReviewDecisionKind.Reissue, epoch: 1),
        };
        Assert.Equal(3, ReviewDecisionOrchestrator.CountReissuesInCurrentChain(records, Job));
    }

    [Fact]
    public void Skipped_IsNotAChainBoundary()
    {
        // Skipped leaves the card for the normal sentinel path: it neither counts
        // nor resets, so the two reissues around it are one continuous chain.
        var records = new[]
        {
            Rec(ReviewDecisionKind.Reissue),
            Rec(ReviewDecisionKind.Skipped),
            Rec(ReviewDecisionKind.Reissue),
        };
        Assert.Equal(2, ReviewDecisionOrchestrator.CountReissuesInCurrentChain(records, Job));
    }

    [Fact]
    public void CountsOnlyTheRequestedJob()
    {
        // Another job's records, including a newer epoch, must not touch this
        // job's count.
        var records = new[]
        {
            Rec(ReviewDecisionKind.Reissue),
            Rec(ReviewDecisionKind.OperatorRequeue, Other, epoch: 4),
            Rec(ReviewDecisionKind.Reissue, Other, epoch: 4),
            Rec(ReviewDecisionKind.Reissue),
        };
        Assert.Equal(2, ReviewDecisionOrchestrator.CountReissuesInCurrentChain(records, Job));
    }

    [Fact]
    public void LegacyRowsBelongToEpochZero()
    {
        Assert.True(ReviewDecisionOrchestrator.IsInAttemptEpoch(Rec(ReviewDecisionKind.Reissue), 0));
        Assert.False(ReviewDecisionOrchestrator.IsInAttemptEpoch(Rec(ReviewDecisionKind.Reissue), 1));
        Assert.True(ReviewDecisionOrchestrator.IsInAttemptEpoch(
            Rec(ReviewDecisionKind.Reissue, epoch: 2), 2));
    }

    [Fact]
    public void OperatorBoundary_ForcesAssessmentUntilFreshVerdictArrives()
    {
        var boundaryOnly = new[]
        {
            Rec(ReviewDecisionKind.Escalate),
            Rec(ReviewDecisionKind.OperatorRequeue, epoch: 1),
        };
        Assert.True(ReviewDecisionOrchestrator.IsPendingOperatorRequeueAssessment(
            boundaryOnly[^1], 1));

        var assessed = Rec(ReviewDecisionKind.AcceptAsDone, epoch: 1);
        Assert.False(ReviewDecisionOrchestrator.IsPendingOperatorRequeueAssessment(
            assessed, 1));
    }

    [Fact]
    public void EpochSidecar_ForcesAssessmentWhenBoundaryJournalWriteIsMissing()
    {
        var staleEscalation = Rec(ReviewDecisionKind.Escalate, epoch: 0);

        Assert.True(ReviewDecisionOrchestrator.IsPendingOperatorRequeueAssessment(
            staleEscalation, 1));
        Assert.True(ReviewDecisionOrchestrator.IsPendingOperatorRequeueAssessment(
            latest: null, epoch: 1));

        var freshVerdict = Rec(ReviewDecisionKind.AcceptAsDone, epoch: 1);
        Assert.False(ReviewDecisionOrchestrator.IsPendingOperatorRequeueAssessment(
            freshVerdict, 1));
    }
}

/// <summary>
/// End-to-end coverage of the per-card reissue budget through
/// the REAL on-disk decision journal: records are appended with
/// <see cref="ReviewDecisionLog.Append"/> and counted back with
/// <see cref="ReviewDecisionOrchestrator.CountReissuesInCurrentChain"/> over
/// <see cref="ReviewDecisionLog.ReadAll"/> - the exact composition the private
/// production <c>CountPriorReissues(workspace, project, jobId)</c> performs. This
/// proves the operator epoch boundary survives the JSONL round-trip
/// (including the <see cref="ReviewDecisionKind"/> string-enum converter), which
/// the in-memory unit cases above do not exercise. It runs fully isolated in a
/// temp workspace - no live backend or integration host is required - which is the
/// integration coverage the AGT-1935 belege said was missing.
/// </summary>
public sealed class ReviewDecisionChainBudgetJournalTests : IDisposable
{
    private const string Project = "demo";
    private const string Job = "job-1";
    private readonly string _workspace;

    public ReviewDecisionChainBudgetJournalTests()
        => _workspace = Path.Combine(Path.GetTempPath(), "rdo-chain-budget-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    private void Append(
        ReviewDecisionKind kind,
        int minute,
        string jobId = Job,
        int? epoch = null)
        => ReviewDecisionLog.Append(_workspace, new ReviewDecisionRecord(
            CreatedAt: new DateTime(2026, 1, 1, 0, minute, 0, DateTimeKind.Utc),
            JobId: jobId,
            Project: Project,
            Kind: kind,
            Reason: kind.ToString(),
            Prompt: "p",
            Response: "r",
            FollowUp: string.Empty)
        {
            AttemptEpoch = epoch,
        });

    private int CurrentChainReissues(string jobId = Job)
        => ReviewDecisionOrchestrator.CountReissuesInCurrentChain(
            ReviewDecisionLog.ReadAll(_workspace, Project), jobId);

    [Fact]
    public void MissingJournal_CountsZero()
        => Assert.Equal(0, CurrentChainReissues());

    [Fact]
    public void PersistedOperatorRequeueKeepsCardBudget()
    {
        Append(ReviewDecisionKind.Reissue, 1);
        Append(ReviewDecisionKind.Reissue, 2);
        Append(ReviewDecisionKind.Escalate, 3);
        Append(ReviewDecisionKind.OperatorRequeue, 4, epoch: 1);
        Append(ReviewDecisionKind.Reissue, 5, epoch: 1);

        Assert.Equal(3, CurrentChainReissues());
    }

    [Fact]
    public void PersistedAutomaticEscalateDoesNotResetEpochZero()
    {
        Append(ReviewDecisionKind.Reissue, 1);
        Append(ReviewDecisionKind.Escalate, 2);
        Append(ReviewDecisionKind.Reissue, 3);

        Assert.Equal(2, CurrentChainReissues());
    }

    [Fact]
    public void PersistedChainWithoutEnder_MatchesLifetimeTotal()
    {
        // No chain-ender persisted: the per-chain count equals the old sticky
        // lifetime total, so in-chain budget behaviour is unchanged.
        Append(ReviewDecisionKind.Reissue, 1);
        Append(ReviewDecisionKind.Reissue, 2);

        Assert.Equal(2, CurrentChainReissues());
    }

    [Fact]
    public void PersistedOtherJobRecords_AreIgnored()
    {
        // A different job's persisted chain-ender must not reset this job's count.
        Append(ReviewDecisionKind.Reissue, 1);
        Append(ReviewDecisionKind.Escalate, 2, jobId: "job-2");
        Append(ReviewDecisionKind.Reissue, 3, jobId: "job-2");
        Append(ReviewDecisionKind.Reissue, 4);

        Assert.Equal(2, CurrentChainReissues());
    }
}
