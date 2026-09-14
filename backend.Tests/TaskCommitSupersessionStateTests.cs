using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2817 - <c>next-attempt</c> is a placeholder, not a verdict. These pin
/// the distinction the data and the UI both have to keep: "requeued,
/// replacement not published yet" is not "replaced by this".
/// </summary>
public class TaskCommitSupersessionStateTests
{
    private static TaskCommitInfo Commit(string? attempt = null, string? sha = null)
        => new() { Sha = "79c2dcf8c", SupersededByAttempt = attempt, SupersededBySha = sha };

    [Fact]
    public void AnUnmarkedCommitIsTheLiveDelivery()
    {
        var commit = Commit();

        Assert.Equal(CommitSupersessionStates.Current, TaskCommitSupersession.State(commit));
        Assert.False(TaskCommitSupersession.IsReplaced(commit));
        Assert.True(TaskCommitSupersession.IsEffectiveDelivery(commit));
    }

    [Fact]
    public void ThePlaceholderIsPendingAndKeepsTheCommitEffective()
    {
        var commit = Commit(attempt: TaskCommitSupersession.PendingAttempt);

        Assert.Equal(CommitSupersessionStates.ReplacementPending, TaskCommitSupersession.State(commit));
        Assert.True(TaskCommitSupersession.IsReplacementPending(commit));
        Assert.False(TaskCommitSupersession.IsReplaced(commit));
        // AGT-2706: until a replacement publishes, this is still the only
        // delivery the card has, so it must keep counting as one.
        Assert.True(TaskCommitSupersession.IsEffectiveDelivery(commit));
        // The legacy reader stays unchanged, which is why the placeholder has
        // been read as a verdict everywhere it was surfaced.
        Assert.True(TaskCommitSupersession.IsSuperseded(commit));
    }

    [Fact]
    public void AResolvedAttemptIdIsAVerdict()
    {
        var commit = Commit(attempt: "run_e1fbb2898c6a4e99a0fd5612b94104c2");

        Assert.Equal(CommitSupersessionStates.Replaced, TaskCommitSupersession.State(commit));
        Assert.False(TaskCommitSupersession.IsReplacementPending(commit));
        Assert.False(TaskCommitSupersession.IsEffectiveDelivery(commit));
    }

    [Fact]
    public void AReplacementShaIsAVerdictEvenBesideThePlaceholder()
    {
        var commit = Commit(attempt: TaskCommitSupersession.PendingAttempt, sha: "ca70d1877");

        Assert.Equal(CommitSupersessionStates.Replaced, TaskCommitSupersession.State(commit));
        Assert.False(TaskCommitSupersession.IsReplacementPending(commit));
    }
}
