using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace TaskServer.Tests;

public sealed class DeviceLoginSessionsTests
{
    [Fact]
    public async Task One_host_provider_login_claims_once_and_hides_code_from_other_sessions()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
        var code = new string('A', 4) + "-" + new string('B', 4);
        var audit = new List<(string Host, string Provider, string Actor, string Result)>();
        var sessions = new DeviceLoginSessions(clock, (evt, actor, _) =>
        {
            audit.Add((evt.HostId, evt.Provider, actor, evt.Status));
            return Task.CompletedTask;
        });
        var started = sessions.Start("host-a", "codex", "operator-a");
        Assert.Throws<InvalidOperationException>(() => sessions.Start("host-a", "codex", "operator-b"));
        Assert.Null(sessions.Get(started.Handle, "another-session"));
        var claim = sessions.Claim("host-a", "coding-runner");
        Assert.NotNull(claim);
        Assert.Null(sessions.Claim("host-a", "review-runner"));
        Assert.False(sessions.Instructions(started.Handle, "review-runner",
            new DeviceLoginInstructionsRequest("https://auth.openai.com/codex/device", code)));
        Assert.True(sessions.Instructions(started.Handle, "coding-runner",
            new DeviceLoginInstructionsRequest("https://auth.openai.com/codex/device", code)));
        Assert.Equal(code, sessions.Get(started.Handle, started.SessionKey!)?.UserCode);
        Assert.True(await sessions.CompleteAsync(started.Handle, "coding-runner",
            new DeviceLoginCompletionRequest("completed", "Probe succeeded."), default));
        Assert.Null(sessions.Get(started.Handle, started.SessionKey!)?.UserCode);
        Assert.Equal(("host-a", "codex", "operator-a", "completed"), Assert.Single(audit));
        Assert.NotNull(sessions.Start("host-a", "codex", "operator-b"));
    }

    [Fact]
    public async Task Expiry_discards_the_one_time_code_and_releases_the_host()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
        var code = new string('A', 4) + "-" + new string('B', 4);
        var audit = new List<string>();
        var sessions = new DeviceLoginSessions(clock, (evt, _, _) =>
        {
            audit.Add(evt.Status);
            return Task.CompletedTask;
        });
        var started = sessions.Start("host-a", "codex", "operator");
        Assert.NotNull(sessions.Claim("host-a", "runner"));
        Assert.True(sessions.Instructions(started.Handle, "runner",
            new DeviceLoginInstructionsRequest("https://auth.openai.com/codex/device", code)));
        clock.Advance(TimeSpan.FromMinutes(16));
        var expired = sessions.Get(started.Handle, started.SessionKey!);
        Assert.Equal("failed", expired?.State);
        Assert.Null(expired?.UserCode);
        await sessions.SweepAsync(default);
        Assert.Equal("timeout", Assert.Single(audit));
        Assert.NotNull(sessions.Start("host-a", "codex", "operator"));
    }
}
