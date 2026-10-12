using AgentStudio.Pipeline;
using AgentStudio.Runner;

namespace AgentStudio.Tasks;

public enum AcceptanceLineageAdmission
{
    /// <summary>The review subject matches the current settled RunAttempt.</summary>
    Proceed,
    /// <summary>
    /// The RunAttempt is gone from the authority store and the operator
    /// overrode: acceptance rests on the delivery evidence on the card.
    /// </summary>
    AcceptOnDeliveryEvidence,
    Refuse,
}

public sealed record AcceptanceLineageDecision(
    AcceptanceLineageAdmission Admission,
    ReviewSubjectAttemptCheck Check,
    string? Message);

/// <summary>
/// AGT-3076 - one lineage rule for Human Review acceptance. A rebuilt attempt
/// authority store (new authority epoch) can lose the RunAttempt that a
/// delivered card's review subject names. That is a missing fact, not a server
/// fault: without an override the card stays in Human Review with a reason,
/// and a written operator override records the acceptance against the
/// delivery evidence that still exists. Every other lineage mismatch stays a
/// refusal even with an override, because it means a different delivery is
/// current.
/// </summary>
public static class AcceptanceAttemptLineagePolicy
{
    public const string MissingRunAttemptRefusal =
        "Acceptance refused: no run attempt in the current authority epoch";

    public static AcceptanceLineageDecision Decide(
        ReviewSubjectAttemptCheck check,
        bool operatorOverride,
        string taskKey,
        string? subjectRunAttemptId,
        long authorityEpoch,
        string? validationError)
    {
        if (check == ReviewSubjectAttemptCheck.Current)
            return new(AcceptanceLineageAdmission.Proceed, check, null);

        if (check != ReviewSubjectAttemptCheck.NoCurrentRunAttempt)
        {
            return new(
                AcceptanceLineageAdmission.Refuse,
                check,
                "Acceptance refused: "
                + (validationError ?? "the review subject does not belong to the current run attempt."));
        }

        var attempt = string.IsNullOrWhiteSpace(subjectRunAttemptId) ? "unknown" : subjectRunAttemptId;
        if (operatorOverride)
        {
            return new(
                AcceptanceLineageAdmission.AcceptOnDeliveryEvidence,
                check,
                $"No run attempt was available for '{taskKey}' in authority epoch {authorityEpoch} "
                + $"(the review subject names '{attempt}'). The operator accepted the card against its "
                + "recorded delivery evidence.");
        }

        return new(
            AcceptanceLineageAdmission.Refuse,
            check,
            $"{MissingRunAttemptRefusal} ({authorityEpoch}) for '{taskKey}'. The review subject names run attempt "
            + $"'{attempt}', which the attempt authority store no longer holds. Accept with an operator override "
            + "and a written reason to record the acceptance against the delivery evidence.");
    }

    /// <summary>
    /// Card-detail projection: why a Human Review card that needs integration
    /// can only be accepted with an operator override. Null when the lineage
    /// is intact or the rule does not apply.
    /// </summary>
    public static AcceptanceAttemptGap? ReadGap(TaskInfo task, AttemptAuthorityService? authority)
    {
        if (authority is null
            || task.State != TaskStates.HumanReview
            || !AcceptanceIntegrationPolicy.IsIntegrationRequired(task))
            return null;
        var subject = TaskIntegrationStatusService.CurrentReviewSubject(task);
        if (subject is null) return null;
        try
        {
            var check = ReviewSubjectStore.CheckCurrentAttempt(task.FolderPath, subject, authority, out var error);
            if (check != ReviewSubjectAttemptCheck.NoCurrentRunAttempt) return null;
            var epoch = authority.AuthorityEpoch;
            var decision = Decide(check, operatorOverride: false, task.TaskKey, subject.RunAttemptId, epoch, error);
            return new AcceptanceAttemptGap(epoch, subject.RunAttemptId, subject.ResultSha, decision.Message!);
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "AcceptanceAttemptLineagePolicy: gap projection is best-effort");
            return null;
        }
    }
}

/// <summary>Why acceptance of this card needs an operator override.</summary>
public sealed record AcceptanceAttemptGap(
    long AuthorityEpoch,
    string SubjectRunAttemptId,
    string ResultSha,
    string Reason);
