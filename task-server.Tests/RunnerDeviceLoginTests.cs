using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace TaskServer.Tests;

public sealed class RunnerDeviceLoginTests
{
    [Fact]
    public void Runner_claim_and_verified_result_are_bound_to_one_host()
    {
        var clock = Clock();
        var store = new RunnerDeviceLoginStore(clock);
        var login = store.Start(new StartRunnerDeviceLoginRequest("runner-a", "codex"));
        Assert.Null(store.Claim("runner-b"));
        Assert.Equal(login.Id, store.Claim("runner-a")?.Id);
        Assert.False(store.Instructions("runner-b", login.Id,
            new ReportRunnerDeviceInstructionsRequest("https://auth.openai.com/device", "ABCD-EFGH")));
        Assert.True(store.Instructions("runner-a", login.Id,
            new ReportRunnerDeviceInstructionsRequest("https://auth.openai.com/device", "ABCD-EFGH")));
        Assert.False(store.Result("runner-b", login.Id, true));
        Assert.True(store.Result("runner-a", login.Id, true));
        Assert.Equal("completed", store.Get(login.Id)?.State);
        Assert.Null(store.Get(login.Id)?.UserCode);
    }

    [Theory]
    [InlineData("https://evil.example/device")]
    public void Rejects_untrusted_or_credential_bearing_urls(string url)
    {
        var store = new RunnerDeviceLoginStore(Clock());
        var login = store.Start(new StartRunnerDeviceLoginRequest("runner-a", "codex"));
        store.Claim("runner-a");
        Assert.Throws<ArgumentException>(() => store.Instructions("runner-a", login.Id,
            new ReportRunnerDeviceInstructionsRequest(url, "ABCD-EFGH")));
    }

    [Fact]
    public void Rejects_urls_with_embedded_credentials()
    {
        var store = new RunnerDeviceLoginStore(Clock());
        var login = store.Start(new StartRunnerDeviceLoginRequest("runner-a", "codex"));
        store.Claim("runner-a");
        var secret = "test-" + new string('x', 24);
        foreach (var url in new[] {
            $"https://auth.openai.com/device?token={secret}",
            $"https://user:{secret}@auth.openai.com/device",
        })
            Assert.Throws<ArgumentException>(() => store.Instructions("runner-a", login.Id,
                new ReportRunnerDeviceInstructionsRequest(url, "ABCD-EFGH")));
    }

    [Fact]
    public void Pending_login_expires_with_injected_clock()
    {
        var clock = Clock();
        var store = new RunnerDeviceLoginStore(clock);
        var login = store.Start(new StartRunnerDeviceLoginRequest("runner-a", "claude"));
        clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Null(store.Claim("runner-a"));
        Assert.Equal("expired", store.Get(login.Id)?.State);
    }

    private static ManualTimeProvider Clock()
        => new(new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero));
}
