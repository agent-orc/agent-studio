using AgentStudio.Git;
using AgentStudio.Pipeline;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2989 - composite fingerprints are length-prefixed, so free-text fields
/// that contain the old delimiter can no longer alias a different fact set.
/// </summary>
public sealed class CanonicalFingerprintEncodingTests
{
    [Theory]
    [InlineData("a\nb", "c", "a", "b\nc")]
    [InlineData("a\u001fb", "c", "a", "b\u001fc")]
    [InlineData("1:a;", "", "1:a;", null)]
    [InlineData("", "x", null, "x")]
    public void Encode_DistinguishesFieldListsThatJoinToTheSameText(
        string? left1, string? left2, string? right1, string? right2)
    {
        Assert.NotEqual(CanonicalFields.Encode(left1, left2), CanonicalFields.Encode(right1, right2));
        Assert.NotEqual(CanonicalFields.Sha256Hex(left1, left2), CanonicalFields.Sha256Hex(right1, right2));
    }

    [Fact]
    public void Encode_IsDeterministicAndVersioned()
    {
        Assert.Equal("cf1;3:abc;0:;~;", CanonicalFields.Encode("abc", "", null));
        Assert.Equal(CanonicalFields.Sha256Hex("a", "b"), CanonicalFields.Sha256Hex("a", "b"));
        Assert.Matches("^[0-9a-f]{64}$", CanonicalFields.Sha256Hex("a", "b"));
    }

    [Fact]
    public void List_PrefixesTheCountSoAListCannotAbsorbTheNextField()
    {
        var left = CanonicalFields.Encode([.. CanonicalFields.List(["a"]), "b"]);
        var right = CanonicalFields.Encode([.. CanonicalFields.List(["a", "b"])]);

        Assert.NotEqual(left, right);
    }

    [Fact]
    public void RailAttemptFingerprint_SeparatorInsideFreeTextDoesNotAliasAnotherAttempt()
    {
        var decision = new AcceptanceRailDecision(AcceptanceRailAction.Escalate, "integration-requeue-budget-exhausted");
        // Delimiter-joined, both became "...s<US>r<US>q".
        var first = AcceptanceRailAttemptPolicy.Fingerprint(
            Card(), Failed(signature: "s\u001fr"), decision, integrationAttemptReason: "q");
        var second = AcceptanceRailAttemptPolicy.Fingerprint(
            Card(), Failed(signature: "s"), decision, integrationAttemptReason: "r\u001fq");

        Assert.NotEqual(first, second);
        Assert.True(AcceptanceRailAttemptPolicy.ShouldAttempt(second, first));
    }

    [Fact]
    public void RailAttemptFingerprint_MissingAndEmptyIntegrationFactsStayDistinct()
    {
        var decision = new AcceptanceRailDecision(AcceptanceRailAction.Accept, "git-derived-integrated");
        var status = new TaskIntegrationStatus { Status = IntegrationStatuses.Pending, IntegrationBranch = "develop" };

        var missing = AcceptanceRailAttemptPolicy.Fingerprint(Card(), status, decision, integrationAttemptReason: null);
        var empty = AcceptanceRailAttemptPolicy.Fingerprint(Card(), status, decision, integrationAttemptReason: "");

        Assert.NotEqual(missing, empty);
        Assert.Equal(missing, AcceptanceRailAttemptPolicy.Fingerprint(Card(), status, decision));
    }

    [Fact]
    public void BounceObligation_ReasonContainingNewlineDoesNotAliasAConflictPath()
    {
        // Newline-joined, both evidence strings were "...r\na.cs\nb.cs".
        var first = Project(Conflict(["b.cs"]), attemptReason: "r\na.cs");
        var second = Project(Conflict(["a.cs", "b.cs"]), attemptReason: "r");

        Assert.NotEqual(first.EvidenceFingerprint, second.EvidenceFingerprint);
        Assert.NotEqual(first.IdempotencyKey, second.IdempotencyKey);
    }

    [Fact]
    public void BounceObligation_SubjectFieldsContainingNewlineDoNotAliasTheKey()
    {
        var status = Conflict(["a.cs"]);
        var first = IntegrationBounceObligationStore.Project(Card(), Subject("run-1", "refs/x\nsha"), status,
            epoch: 0, roundCount: 0, holdState: "none", routeDecision: "automatic", attemptReason: "r");
        var second = IntegrationBounceObligationStore.Project(Card(), Subject("run-1", "refs/x") with { ResultSha = "sha\nsha" },
            status, epoch: 0, roundCount: 0, holdState: "none", routeDecision: "automatic", attemptReason: "r");

        Assert.NotEqual(first.IdempotencyKey, second.IdempotencyKey);
    }

    [Fact]
    public void BounceObligation_KeyIsStableAndIndependentOfConflictPathOrder()
    {
        var first = Project(Conflict(["b.cs", "a.cs"]), attemptReason: "Conflict in a.cs");
        var second = Project(Conflict(["a.cs", "b.cs"]), attemptReason: "Conflict in a.cs");

        Assert.Equal(first.IdempotencyKey, second.IdempotencyKey);
        Assert.Equal(first.EvidenceFingerprint, second.EvidenceFingerprint);
        Assert.Matches("^[0-9a-f]{64}$", first.IdempotencyKey);
    }

    private static IntegrationBounceObligation Project(TaskIntegrationStatus status, string attemptReason)
        => IntegrationBounceObligationStore.Project(Card(), Subject("run-1", "refs/heads/task"), status,
            epoch: 0, roundCount: 0, holdState: "none", routeDecision: "automatic", attemptReason: attemptReason);

    private static TaskInfo Card() => new()
    {
        Id = "canonical-card",
        Key = "AGT-2989",
        TaskKey = "fixture::canonical-card",
        State = TaskStates.HumanReview,
        Mode = TaskModes.Coding,
        TaskType = TaskTypes.Chore,
    };

    private static ReviewSubjectRecord Subject(string runAttemptId, string resultRef) => new()
    {
        TaskKey = "fixture::canonical-card",
        RunAttemptId = runAttemptId,
        ResultRef = resultRef,
        ResultSha = "sha",
    };

    private static TaskIntegrationStatus Failed(string signature) => new()
    {
        Status = IntegrationStatuses.ConflictSkipped,
        IntegrationBranch = "develop",
        Failure = new TaskIntegrationFailure
        {
            Code = AcceptedIntegrationFailureCodes.BuildGateFailed,
            FailureSignature = signature,
        },
    };

    private static TaskIntegrationStatus Conflict(List<string> paths) => new()
    {
        Status = IntegrationStatuses.ConflictSkipped,
        IntegrationBranch = "develop",
        Sha = "sha",
        Failure = new TaskIntegrationFailure
        {
            Code = AcceptedIntegrationFailureCodes.MergeConflict,
            RebaseRecoveryAvailable = true,
            ConflictReport = new IntegrationConflictReport { ConflictedFiles = paths },
        },
    };
}
