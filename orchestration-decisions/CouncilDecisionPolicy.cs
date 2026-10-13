using AgentStudio.Review;
namespace AgentStudio.Orchestration.Decisions;

public static class CouncilDecisionPolicy
{
    public static CouncilReviewReaction Derive(
        string reviewFileName,
        string gradeToken,
        IReadOnlyList<string> findings,
        int priorReissues,
        int maxReissues,
        string jobId,
        int? targetRunAttempt = null,
        string? executionError = null,
        DateTime now = default)
    {
        var createdAt = now;

        if (!string.IsNullOrWhiteSpace(executionError))
        {
            return new CouncilReviewReaction(createdAt, reviewFileName, gradeToken,
                CouncilReactionDisposition.Accept,
                "Accept grade as unavailable: no review finding was produced; the remaining gates decide. " + executionError.Trim(),
                Array.Empty<CouncilFindingAssessment>(), false, null, null);
        }

        if (findings.Count == 0 && gradeToken == "A")
        {
            return new CouncilReviewReaction(createdAt, reviewFileName, gradeToken,
                CouncilReactionDisposition.Accept,
                "Accept, nothing open.",
                Array.Empty<CouncilFindingAssessment>(), false, null, null);
        }

        if (findings.Count == 0)
        {
            var finding = $"Quality grade {gradeToken} indicates an open gap, but the reviewer emitted no concrete finding sentence.";
            return new CouncilReviewReaction(createdAt, reviewFileName, gradeToken,
                CouncilReactionDisposition.Escalate,
                $"Escalate: grade {gradeToken} is not clean, but its required finding handoff is missing.",
                new[]
                {
                    new CouncilFindingAssessment(
                        finding,
                        CouncilFindingAction.Escalate,
                        "A targeted automatic round cannot start without the concrete deficiency and required outcome.")
                },
                false, null, null);
        }

        if (priorReissues >= maxReissues)
        {
            var assessments = findings.Select(finding => new CouncilFindingAssessment(
                finding, CouncilFindingAction.Escalate,
                $"The automatic review-loop budget is exhausted ({priorReissues}/{maxReissues})."))
                .ToList();
            return new CouncilReviewReaction(createdAt, reviewFileName, gradeToken,
                CouncilReactionDisposition.Escalate,
                $"Escalate {assessments.Count} open review finding(s); loop budget exhausted.",
                assessments, false, null, null);
        }

        var fixes = findings.Select(finding => new CouncilFindingAssessment(
            finding, CouncilFindingAction.FixNextRound,
            "Concrete review deficiency; fix it in the next bounded round and provide focused evidence."))
            .ToList();
        return new CouncilReviewReaction(createdAt, reviewFileName, gradeToken,
            CouncilReactionDisposition.Reissue,
            $"Fix {fixes.Count} review finding(s) in the next round.",
            fixes, true, jobId, targetRunAttempt ?? priorReissues + 2);
    }

    public static CouncilReviewReaction EscalateBecause(
        CouncilReviewReaction reaction,
        string reason)
    {
        var assessments = reaction.Assessments.Select(assessment => assessment with
        {
            Action = CouncilFindingAction.Escalate,
            Reason = reason,
        }).ToList();
        return reaction with
        {
            Disposition = CouncilReactionDisposition.Escalate,
            Summary = "Escalate: " + reason,
            Assessments = assessments,
            StartsNewRound = false,
            TargetJobId = null,
            TargetRunAttempt = null,
        };
    }
}
