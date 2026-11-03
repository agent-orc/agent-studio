using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// Which push failures fresh repository tokens can cure, and how long a host
/// keeps trying before it escalates the retained worktree.
/// </summary>
public sealed class RepositoryPushRecoveryPolicyTests
{
    [Theory]
    [InlineData("remote: Write access to repository not granted.\nfatal: unable to access 'https://github.com/owner/site.git/': The requested URL returned error: 403", true)]
    [InlineData("remote: Permission to owner/site.git denied to reader.\nfatal: unable to access 'https://github.com/owner/site.git/': The requested URL returned error: 403", true)]
    [InlineData("fatal: unable to access 'https://github.com/owner/site.git/': The requested URL returned error: 401", true)]
    [InlineData("remote: Invalid username or password.\nfatal: Authentication failed for 'https://github.com/owner/site.git/'", true)]
    [InlineData("fatal: could not read Username for 'https://github.com': terminal prompts disabled", true)]
    [InlineData("remote: error: GH013: Repository rule violations found\nremote: - GITHUB PUSH PROTECTION\nremote: Push cannot contain secrets\nremote: -- Mailgun API Key --\nremote:   path: fixture.cs:1", false)]
    [InlineData("! [remote rejected] HEAD -> topic (refusing to allow a GitHub App to create or update workflow `.github/workflows/ci.yml` without `workflows` permission)", false)]
    [InlineData("fatal: unable to access 'https://github.com/owner/site.git/': Could not resolve host: github.com", false)]
    [InlineData("! [rejected] HEAD -> topic (non-fast-forward)", false)]
    [InlineData("", false)]
    public void Only_an_authorization_refusal_is_retried(string failure, bool retried)
    {
        Assert.Equal(retried, RepositoryPushRecoveryPolicy.IsRefusal(failure));
        Assert.Equal(retried, RepositoryPushRecoveryPolicy.NextDelay(failure, 0) is not null);
    }

    [Fact]
    public void The_retries_follow_a_fixed_schedule_and_end()
    {
        const string refused = "The requested URL returned error: 403";
        var schedule = Enumerable.Range(0, RepositoryPushRecoveryPolicy.Delays.Count)
            .Select(retry => RepositoryPushRecoveryPolicy.NextDelay(refused, retry))
            .ToArray();

        Assert.Equal(RepositoryPushRecoveryPolicy.Delays.Cast<TimeSpan?>(), schedule);
        Assert.Null(RepositoryPushRecoveryPolicy.NextDelay(refused, RepositoryPushRecoveryPolicy.Delays.Count));
        Assert.True(RepositoryPushRecoveryPolicy.Delays.Aggregate(TimeSpan.Zero, (sum, delay) => sum + delay)
                    <= TimeSpan.FromMinutes(2));
    }
}
