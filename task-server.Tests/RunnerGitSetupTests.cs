using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace TaskServer.Tests;

public sealed class RunnerGitSetupTests
{
    [Fact]
    public void Token_is_handed_to_bound_runner_once_and_never_returned_to_owner()
    {
        var store = new RunnerGitSetupStore(Clock());
        var token = "test-" + new string('x', 36);
        var started = store.Start(new StartRunnerGitSetupRequest(
            "runner-1", "https://github.com/example/demo.git", "token", token));
        Assert.DoesNotContain(token, System.Text.Json.JsonSerializer.Serialize(started));

        var work = store.Claim("runner-1");
        Assert.NotNull(work);
        Assert.Equal(token, work.Token);
        Assert.Null(store.Claim("runner-1"));
        Assert.True(store.Configured("runner-1", started.Id, true));
        Assert.True(store.Verify(started.Id));
        Assert.Equal("verify", store.Claim("runner-1")!.Action);
        Assert.True(store.Proof("runner-1", started.Id, new(true, true)));
        Assert.Equal("verified", store.Get(started.Id)!.State);
        Assert.DoesNotContain(token, System.Text.Json.JsonSerializer.Serialize(store.Get(started.Id)));
    }

    [Fact]
    public void Deploy_key_requires_runner_key_and_both_live_git_proofs()
    {
        var store = new RunnerGitSetupStore(Clock());
        var started = store.Start(new StartRunnerGitSetupRequest(
            "runner-2", "git@github.com:example/demo.git", "deploy-key"));
        Assert.False(store.Verify(started.Id));
        Assert.Null(store.Claim("other-runner"));
        Assert.NotNull(store.Claim("runner-2"));
        Assert.True(store.Key("runner-2", started.Id, "ssh-ed25519 " + new string('a', 56)));
        Assert.Equal("awaiting-github", store.Get(started.Id)!.State);
        Assert.True(store.Verify(started.Id));
        Assert.NotNull(store.Claim("runner-2"));
        Assert.True(store.Proof("runner-2", started.Id, new(true, false)));
        Assert.Equal("denied", store.Get(started.Id)!.State);
        Assert.True(store.Verify(started.Id));
        Assert.NotNull(store.Claim("runner-2"));
        Assert.True(store.Proof("runner-2", started.Id, new(true, true)));
        Assert.Equal("verified", store.Get(started.Id)!.State);
    }

    [Theory]
    [InlineData("https://evil.example/example/demo")]
    [InlineData("git@github.com:example/demo/extra")]
    public void Credential_bearing_or_wrong_origins_are_rejected(string origin)
        => Assert.False(RunnerGitSetupStore.ValidGitHubOrigin(origin));

    [Fact]
    public void Embedded_credentials_are_rejected()
    {
        var value = "test-" + new string('x', 24);
        Assert.False(RunnerGitSetupStore.ValidGitHubOrigin(
            $"https://user:{value}@github.com/example/demo"));
        Assert.False(RunnerGitSetupStore.ValidGitHubOrigin(
            $"https://github.com/example/demo?token={value}"));
    }

    [Fact]
    public void Unclaimed_token_expires_under_the_injected_clock()
    {
        var clock = Clock();
        var store = new RunnerGitSetupStore(clock);
        var started = store.Start(new StartRunnerGitSetupRequest("runner-1",
            "https://github.com/example/demo.git", "token", "test-" + new string('x', 36)));
        clock.Advance(TimeSpan.FromHours(1).Add(TimeSpan.FromSeconds(1)));
        Assert.Null(store.Claim("runner-1"));
        Assert.Equal("expired", store.Get(started.Id)?.State);
    }

    private static ManualTimeProvider Clock()
        => new(new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero));
}
