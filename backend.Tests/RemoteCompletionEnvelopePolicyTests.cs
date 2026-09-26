using Xunit;

namespace AgentStudio.Tests;

public sealed class RemoteCompletionEnvelopePolicyTests
{
    [Theory]
    [InlineData("done")]
    [InlineData("noop")]
    public void Successful_coding_completion_with_the_full_envelope_is_persisted(string outcome)
    {
        var decision = RemoteCompletionEnvelopePolicy.Decide(
            requiresEnvelope: true,
            outcome,
            runAttemptKnown: true,
            hasResultSha: true,
            hasBaseSha: true,
            hasImmutableResultRef: true,
            hasArtifactManifestDigest: true);

        Assert.Equal(RemoteCompletionEnvelopeDisposition.Persist, decision.Disposition);
        Assert.Equal(outcome, decision.AuthorityOutcome);
        Assert.Null(decision.Reason);
    }

    [Theory]
    [InlineData(false, true, true, true, true, "RunAttemptAuthority")]
    [InlineData(true, false, true, true, true, "ResultSha")]
    [InlineData(true, true, false, true, true, "BaseSha")]
    [InlineData(true, true, true, false, true, "ImmutableResultRef")]
    [InlineData(true, true, true, true, false, "ArtifactManifestDigest")]
    public void Coding_completion_with_any_missing_envelope_fact_fails_delivery(
        bool runAttemptKnown,
        bool hasResultSha,
        bool hasBaseSha,
        bool hasImmutableResultRef,
        bool hasArtifactManifestDigest,
        string missingFact)
    {
        var decision = RemoteCompletionEnvelopePolicy.Decide(
            requiresEnvelope: true,
            "done",
            runAttemptKnown,
            hasResultSha,
            hasBaseSha,
            hasImmutableResultRef,
            hasArtifactManifestDigest);

        Assert.Equal(RemoteCompletionEnvelopeDisposition.FailDelivery, decision.Disposition);
        Assert.Equal("delivery-failed", decision.AuthorityOutcome);
        Assert.Contains(missingFact, decision.Reason);
        Assert.Contains("no ReviewSubject was created", decision.Reason);
        Assert.Contains(missingFact, decision.MissingFacts!);
    }

    [Theory]
    [InlineData(false, "done")]
    [InlineData(true, "environmentfailure")]
    [InlineData(true, "mechanicalfallback")]
    public void Non_coding_or_preparation_failure_completion_does_not_require_an_envelope(
        bool requiresEnvelope,
        string outcome)
    {
        var decision = RemoteCompletionEnvelopePolicy.Decide(
            requiresEnvelope,
            outcome,
            runAttemptKnown: false,
            hasResultSha: false,
            hasBaseSha: false,
            hasImmutableResultRef: false,
            hasArtifactManifestDigest: false);

        Assert.Equal(RemoteCompletionEnvelopeDisposition.NotRequired, decision.Disposition);
        Assert.Equal(outcome, decision.AuthorityOutcome);
        Assert.Null(decision.Reason);
    }

    [Theory]
    [InlineData("blocked")]
    [InlineData("needsinput")]
    [InlineData("unknown")]
    public void Non_success_coding_terminal_still_requires_delivery_envelope(string outcome)
    {
        var decision = RemoteCompletionEnvelopePolicy.Decide(
            requiresEnvelope: true,
            outcome,
            runAttemptKnown: true,
            hasResultSha: true,
            hasBaseSha: false,
            hasImmutableResultRef: true,
            hasArtifactManifestDigest: true);

        Assert.Equal(RemoteCompletionEnvelopeDisposition.FailDelivery, decision.Disposition);
        Assert.Equal("delivery-failed", decision.AuthorityOutcome);
        Assert.Contains("BaseSha", decision.MissingFacts!);
    }
}
