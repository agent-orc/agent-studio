using AgentStudio.Runner;

namespace AgentStudio.Tasks;

/// <summary>The authority action for a Remote coding completion's immutable result envelope.</summary>
public enum RemoteCompletionEnvelopeDisposition
{
    NotRequired,
    Persist,
    FailDelivery,
}

/// <summary>A pure completion-boundary decision made before attempt settlement.</summary>
public sealed record RemoteCompletionEnvelopeDecision(
    RemoteCompletionEnvelopeDisposition Disposition,
    string AuthorityOutcome,
    string? Reason = null,
    IReadOnlyList<string>? MissingFacts = null)
{
    public bool ShouldPersist => Disposition == RemoteCompletionEnvelopeDisposition.Persist;
    public bool ShouldFailDelivery => Disposition == RemoteCompletionEnvelopeDisposition.FailDelivery;
}

/// <summary>
/// Prevents any terminal coding RunAttempt from being treated as delivered
/// unless the server can persist its complete immutable result envelope.
/// Environment-preparation failures are exempt because no coding checkout ran.
/// A mechanical fallback is also exempt: it returns the fenced attempt to Ready
/// for a new coding generation and is not a delivered ReviewSubject.
/// </summary>
public static class RemoteCompletionEnvelopePolicy
{
    public static RemoteCompletionEnvelopeDecision Decide(
        bool requiresEnvelope,
        string? reportedOutcome,
        bool runAttemptKnown,
        bool hasResultSha,
        bool hasBaseSha,
        bool hasImmutableResultRef,
        bool hasArtifactManifestDigest)
    {
        var outcome = reportedOutcome?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!requiresEnvelope || outcome == "environmentfailure" || outcome == "mechanicalfallback")
        {
            return new RemoteCompletionEnvelopeDecision(
                RemoteCompletionEnvelopeDisposition.NotRequired,
                outcome);
        }

        var missing = new List<string>(4);
        if (!runAttemptKnown) missing.Add("RunAttemptAuthority");
        if (!hasResultSha) missing.Add("ResultSha");
        if (!hasBaseSha) missing.Add("BaseSha");
        if (!hasImmutableResultRef) missing.Add("ImmutableResultRef");
        if (!hasArtifactManifestDigest) missing.Add("ArtifactManifestDigest");
        if (missing.Count == 0)
        {
            return new RemoteCompletionEnvelopeDecision(
                RemoteCompletionEnvelopeDisposition.Persist,
                outcome);
        }

        return new RemoteCompletionEnvelopeDecision(
            RemoteCompletionEnvelopeDisposition.FailDelivery,
            RemoteDeliveryFailurePolicy.DeliveryFailed,
            "Remote coding completion did not carry a complete immutable result envelope "
            + $"(missing: {string.Join(", ", missing)}). The delivery failed and no ReviewSubject was created.",
            missing);
    }
}
