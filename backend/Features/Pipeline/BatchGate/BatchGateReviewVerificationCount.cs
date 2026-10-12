using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

public sealed record BatchGateReviewVerificationMinutes(
    double CandidateMinutes, double BaselineMinutes, int CountedCommands);

/// <summary>
/// Pure host-minute count of the review-side verification of a batch-deferred
/// review: tool commands with aspect <c>review-verification</c>. Candidate
/// minutes cover the candidate and its clean-repeat retries; baseline minutes
/// cover the <c>baseline-*</c> workspaces except cache hits, which never ran.
/// </summary>
public static class BatchGateReviewVerificationCount
{
    public const string Aspect = "review-verification";

    public static BatchGateReviewVerificationMinutes Count(
        IReadOnlyList<ReviewCommandEvidenceDto> commands)
    {
        var candidate = 0d;
        var baseline = 0d;
        var counted = 0;
        foreach (var command in commands)
        {
            if (!string.Equals(command.Aspect, Aspect, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(command.ExecutionKind, ReviewCommandKinds.Tool,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            var minutes = Math.Max(0d, (command.FinishedAt - command.StartedAt).TotalMinutes);
            if (command.WorkspaceRole is "candidate" or "clean-repeat")
            {
                // A candidate compared against a cached baseline still ran.
                candidate += minutes;
                counted++;
            }
            else if (command.WorkspaceRole.StartsWith("baseline-", StringComparison.Ordinal)
                     && command.BaselineReusedFromAttemptId is null)
            {
                baseline += minutes;
                counted++;
            }
        }
        return new(candidate, baseline, counted);
    }
}
