using AgentStudio.Git;
using AgentStudio.Runner;

namespace AgentStudio.Tasks;

/// <summary>
/// The live facts a condition is evaluated against. Resolved per sweep tick from
/// the card and its project, so a marker written weeks ago is judged against
/// today's repository rather than against a snapshot taken at park time.
/// </summary>
/// <param name="RepositoryRoot">Product checkout for the card's project.</param>
/// <param name="TaskBranch">The card's delivery branch, when one is known.</param>
/// <param name="IntegrationBranch">The branch the card integrates into.</param>
public sealed record ParkedBlockerContext(
    string? RepositoryRoot = null,
    string? TaskBranch = null,
    string? IntegrationBranch = null,
    string? TaskKey = null);

/// <summary>
/// Decides whether a park condition still holds. Separated behind an interface
/// because this is the only part of the recall sweep that touches the outside
/// world; the sweep's own logic stays deterministic and testable.
/// </summary>
public interface IParkedBlockerProbe
{
    ParkedBlockerEvaluation Evaluate(
        ParkedBlockerCondition condition,
        ParkedBlockerContext context,
        DateTime now);
}

/// <summary>
/// Built-in probe for the condition kinds in
/// <see cref="ParkedBlockerConditionKinds"/>.
///
/// <para>Fails to <see cref="ParkedBlockerStatuses.Undeterminable"/>, never to
/// <see cref="ParkedBlockerStatuses.Recallable"/>. A probe that cannot read the
/// repository must not claim the blocker is gone: the whole point of the recall
/// sweep is that its "this is resolvable" signal is trustworthy enough for an
/// operator to act on without re-verifying.</para>
/// </summary>
public sealed class ParkedBlockerProbe : IParkedBlockerProbe
{
    private readonly GitService? _git;
    private readonly AttemptAuthorityService? _attempts;

    public ParkedBlockerProbe(GitService? git = null, AttemptAuthorityService? attempts = null)
    {
        _git = git;
        _attempts = attempts;
    }

    public ParkedBlockerEvaluation Evaluate(
        ParkedBlockerCondition condition,
        ParkedBlockerContext context,
        DateTime now)
        => condition.Kind switch
        {
            ParkedBlockerConditionKinds.GitAncestor => EvaluateGitAncestor(condition, context, now),
            ParkedBlockerConditionKinds.ReviewRetryAvailable => EvaluateReviewRetry(context, now),
            _ => Undeterminable(now, "The blocker has no automatic condition; a person has to decide."),
        };

    private ParkedBlockerEvaluation EvaluateReviewRetry(ParkedBlockerContext context, DateTime now)
    {
        if (_attempts is null || string.IsNullOrWhiteSpace(context.TaskKey))
            return Undeterminable(now, "Review attempt authority is unavailable.");
        var projection = _attempts.GetTaskProjection(context.TaskKey);
        var run = projection.CurrentRunAttempt;
        var review = projection.CurrentReviewAttempt;
        return run is { State: AttemptLifecycleState.Completed, ResultEnvelope: not null, ResultSha: not null }
            && review?.State is not (AttemptLifecycleState.Pending or AttemptLifecycleState.Leased)
            ? Recallable(now, "The completed delivery is available for Retry review.")
            : Blocked(now, "The completed delivery is unavailable or a review already owns it.");
    }

    private ParkedBlockerEvaluation EvaluateGitAncestor(
        ParkedBlockerCondition condition,
        ParkedBlockerContext context,
        DateTime now)
    {
        if (_git is null)
            return Undeterminable(now, "No Git reader is available to check branch ancestry.");

        var root = condition.Parameter(ParkedBlockerParameters.RepositoryRoot) ?? context.RepositoryRoot;
        var ancestor = condition.Parameter(ParkedBlockerParameters.Ancestor) ?? context.IntegrationBranch;
        var descendant = condition.Parameter(ParkedBlockerParameters.Descendant) ?? context.TaskBranch;

        if (string.IsNullOrWhiteSpace(root)
            || string.IsNullOrWhiteSpace(ancestor)
            || string.IsNullOrWhiteSpace(descendant))
        {
            return Undeterminable(
                now,
                "The repository root or one of the two branches is unknown, so ancestry cannot be checked.");
        }

        bool contained;
        try
        {
            contained = _git.IsAncestor(root!, ancestor!, descendant!);
        }
        catch (Exception)
        {
            return Undeterminable(now, $"Ancestry of '{ancestor}' in '{descendant}' could not be read.");
        }

        // GitService.IsAncestor answers false both for "not contained" and for a
        // ref it cannot resolve. Both stay blocked here, which errs toward
        // keeping the human in the loop; the card still ages visibly, so it
        // cannot go quiet the way AGT-2220 did.
        return contained
            ? Recallable(now, $"'{descendant}' now contains '{ancestor}'.")
            : Blocked(now, $"'{descendant}' does not contain '{ancestor}' (or one of the refs is gone).");
    }

    private static ParkedBlockerEvaluation Recallable(DateTime now, string detail)
        => new() { Status = ParkedBlockerStatuses.Recallable, At = now, Detail = detail };

    private static ParkedBlockerEvaluation Blocked(DateTime now, string detail)
        => new() { Status = ParkedBlockerStatuses.Blocked, At = now, Detail = detail };

    private static ParkedBlockerEvaluation Undeterminable(DateTime now, string detail)
        => new() { Status = ParkedBlockerStatuses.Undeterminable, At = now, Detail = detail };
}
