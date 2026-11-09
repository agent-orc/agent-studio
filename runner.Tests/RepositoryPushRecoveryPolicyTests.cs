using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// Which push failures are transient, and how long the retained worktree waits.
/// </summary>
public sealed class RepositoryPushRecoveryPolicyTests
{
    [Theory]
    [InlineData("remote: Internal Server Error", true)]
    [InlineData("remote: fatal error in commit_refs", true)]
    [InlineData("fatal: unable to access 'https://github.com/owner/site.git/': The requested URL returned error: 503", true)]
    [InlineData("error: RPC failed; HTTP 500 curl 22", true)]
    [InlineData("send-pack: connection reset by peer", true)]
    [InlineData("remote: Write access to repository not granted.\nfatal: unable to access 'https://github.com/owner/site.git/': The requested URL returned error: 403", false)]
    [InlineData("remote: Permission to owner/site.git denied to reader.\nfatal: unable to access 'https://github.com/owner/site.git/': The requested URL returned error: 403", false)]
    [InlineData("fatal: unable to access 'https://github.com/owner/site.git/': The requested URL returned error: 401", false)]
    [InlineData("remote: Invalid username or password.\nfatal: Authentication failed for 'https://github.com/owner/site.git/'", false)]
    [InlineData("remote: error: GH013: Repository rule violations found\nremote: - GITHUB PUSH PROTECTION\nremote: Push cannot contain secrets\nremote: -- Mailgun API Key --\nremote:   path: fixture.cs:1", false)]
    [InlineData("! [remote rejected] HEAD -> topic (refusing to allow a GitHub App to create or update workflow `.github/workflows/ci.yml` without `workflows` permission)", false)]
    [InlineData("fatal: unable to access 'https://github.com/owner/site.git/': Could not resolve host: github.com", false)]
    [InlineData("! [rejected] HEAD -> topic (non-fast-forward)", false)]
    [InlineData("", false)]
    public void Only_transient_server_faults_are_retried(string failure, bool retried)
    {
        Assert.Equal(retried, RepositoryPushRecoveryPolicy.IsTransient(failure));
        Assert.Equal(retried, RepositoryPushRecoveryPolicy.NextDelay(failure, 0) is not null);
    }

    [Fact]
    public void The_retries_follow_a_fixed_schedule_and_end()
    {
        const string refused = "The requested URL returned error: 503";
        var schedule = Enumerable.Range(0, RepositoryPushRecoveryPolicy.Delays.Count)
            .Select(retry => RepositoryPushRecoveryPolicy.NextDelay(refused, retry))
            .ToArray();

        Assert.Equal(RepositoryPushRecoveryPolicy.Delays.Cast<TimeSpan?>(), schedule);
        Assert.Null(RepositoryPushRecoveryPolicy.NextDelay(refused, RepositoryPushRecoveryPolicy.Delays.Count));
        Assert.Equal([TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)], RepositoryPushRecoveryPolicy.Delays);
    }
}
