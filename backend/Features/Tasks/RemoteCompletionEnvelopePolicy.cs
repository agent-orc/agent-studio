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
/// Mechanical fallback and transient model capacity are also exempt: they
/// return the fenced attempt to Ready and create no delivered ReviewSubject.
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
        bool hasArtifactManifestDigest,
        string? reportedReason = null)
    {
        var outcome = reportedOutcome?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!requiresEnvelope || outcome is "environmentfailure" or "mechanicalfallback" or "transientfailure")
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

        var pushFailure = reportedReason?.IndexOf("[runner-result-ref-push-failed:", StringComparison.Ordinal);
        var pushFailureDetail = pushFailure is >= 0
            ? " " + reportedReason![pushFailure.Value..Math.Min(reportedReason.Length, pushFailure.Value + 500)]
            : string.Empty;
        return new RemoteCompletionEnvelopeDecision(
            RemoteCompletionEnvelopeDisposition.FailDelivery,
            RemoteDeliveryFailurePolicy.DeliveryFailed,
            "Remote coding completion did not carry a complete immutable result envelope "
            + $"(missing: {string.Join(", ", missing)}). The delivery failed and no ReviewSubject was created."
            + pushFailureDetail,
            missing);
    }
}
