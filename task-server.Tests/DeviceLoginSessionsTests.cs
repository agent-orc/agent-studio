using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace TaskServer.Tests;

public sealed class DeviceLoginSessionsTests
{
    [Fact]
    public async Task Completed_transition_is_idempotent_and_audit_failure_is_retried_separately()
    {
        var calls = 0;
        var sessions = new DeviceLoginSessions(Clock(), (_, _, _) =>
        {
            if (++calls == 1) throw new IOException("Audit store unavailable.");
            return Task.CompletedTask;
        });
        var login = sessions.Start("host-a", "claude", "owner");
        sessions.Claim("host-a", "runner-a");
        var completion = new DeviceLoginCompletionRequest("completed", "Probe succeeded.");

        Assert.True(await sessions.CompleteAsync(login.Handle, "runner-a", completion, default));
        Assert.Equal("completed", sessions.RunnerState(login.Handle, "runner-a"));
        Assert.True(await sessions.CompleteAsync(login.Handle, "runner-a", completion, default));
        Assert.False(await sessions.CompleteAsync(login.Handle, "runner-a",
            new DeviceLoginCompletionRequest("failed", "Different result."), default));
        await sessions.SweepAsync(default);
        Assert.Equal(2, calls);
    }

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

    [Fact]
    public async Task Runner_claim_and_verified_result_are_bound_to_one_host()
    {
        var sessions = Sessions(Clock());
        var code = new string('A', 4) + "-" + new string('B', 4);
        var login = sessions.Start("host-a", "codex", "owner");
        Assert.Null(sessions.Claim("host-b", "runner-b"));
        Assert.Equal(login.Handle, sessions.Claim("host-a", "runner-a")?.Handle);
        Assert.False(sessions.Instructions(login.Handle, "runner-b",
            new DeviceLoginInstructionsRequest("https://auth.openai.com/device", code)));
        Assert.True(sessions.Instructions(login.Handle, "runner-a",
            new DeviceLoginInstructionsRequest("https://auth.openai.com/device", code)));
        Assert.False(await sessions.CompleteAsync(login.Handle, "runner-b",
            new DeviceLoginCompletionRequest("completed", "Probe succeeded."), default));
        Assert.True(await sessions.CompleteAsync(login.Handle, "runner-a",
            new DeviceLoginCompletionRequest("completed", "Probe succeeded."), default));
        var completed = sessions.Get(login.Handle, login.SessionKey!);
        Assert.Equal("completed", completed?.State);
        Assert.Null(completed?.UserCode);
        Assert.Null(completed?.VerificationUrl);
    }

    [Fact]
    public void Only_the_target_runner_of_a_shared_host_claims_the_sign_in()
    {
        var sessions = Sessions(Clock());
        var login = sessions.Start("host-a", "codex", "owner", targetRunnerId: "review-runner");
        Assert.Throws<InvalidOperationException>(() =>
            sessions.Start("host-a", "codex", "owner", targetRunnerId: "coding-runner"));
        Assert.Null(sessions.Claim("host-a", "coding-runner"));
        Assert.Equal(login.Handle, sessions.Claim("host-a", "review-runner")?.Handle);
        Assert.Null(sessions.Claim("host-a", "review-runner"));
        var untargeted = sessions.Start("host-a", "claude", "owner");
        Assert.Equal(untargeted.Handle, sessions.Claim("host-a", "coding-runner")?.Handle);
    }

    [Fact]
    public async Task Claiming_runner_reads_back_only_its_session_state_and_sees_cancel_and_timeout()
    {
        var clock = Clock();
        var sessions = Sessions(clock);
        var cancelled = sessions.Start("host-a", "codex", "owner");
        Assert.Null(sessions.RunnerState(cancelled.Handle, "runner-a"));
        sessions.Claim("host-a", "runner-a");
        Assert.Equal("running", sessions.RunnerState(cancelled.Handle, "runner-a"));
        Assert.Null(sessions.RunnerState(cancelled.Handle, "runner-b"));
        Assert.True(await sessions.CancelAsync(cancelled.Handle, cancelled.SessionKey!, default));
        Assert.Equal("failed", sessions.RunnerState(cancelled.Handle, "runner-a"));

        var expiring = sessions.Start("host-a", "claude", "owner");
        sessions.Claim("host-a", "runner-a");
        clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal("failed", sessions.RunnerState(expiring.Handle, "runner-a"));
        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Null(sessions.RunnerState(expiring.Handle, "runner-a"));
    }

    [Theory]
    [InlineData("codex", "https://evil.example/device")]
    [InlineData("codex", "https://claude.ai/oauth/authorize")]
    [InlineData("claude", "https://auth.openai.com/device")]
    [InlineData("codex", "http://auth.openai.com/device")]
    [InlineData("codex", "https://auth.openai.com/device#fragment")]
    public void Rejects_untrusted_or_credential_bearing_urls(string provider, string url)
    {
        var sessions = Sessions(Clock());
        var login = sessions.Start("host-a", provider, "owner");
        sessions.Claim("host-a", "runner-a");
        Assert.Throws<ArgumentException>(() => sessions.Instructions(login.Handle, "runner-a",
            new DeviceLoginInstructionsRequest(url, "ABCD-EFGH")));
    }

    [Fact]
    public void Rejects_urls_with_embedded_credentials()
    {
        var sessions = Sessions(Clock());
        var login = sessions.Start("host-a", "codex", "owner");
        sessions.Claim("host-a", "runner-a");
        var secret = "test-" + new string('x', 24);
        foreach (var url in new[] {
            $"https://auth.openai.com/device?token={secret}",
            $"https://user:{secret}@auth.openai.com/device",
        })
            Assert.Throws<ArgumentException>(() => sessions.Instructions(login.Handle, "runner-a",
                new DeviceLoginInstructionsRequest(url, "ABCD-EFGH")));
    }

    [Fact]
    public void Claude_accepts_url_only_instructions_and_codex_requires_a_code()
    {
        var sessions = Sessions(Clock());
        var claude = sessions.Start("host-a", "claude", "owner");
        var codex = sessions.Start("host-a", "codex", "owner");
        Assert.Throws<InvalidOperationException>(() => sessions.Start("host-a", "claude", "owner"));
        Assert.Equal("claude", sessions.Claim("host-a", "runner-a")?.Provider);
        Assert.Equal("codex", sessions.Claim("host-a", "runner-a")?.Provider);
        Assert.Throws<ArgumentException>(() => sessions.Instructions(claude.Handle, "runner-a",
            new DeviceLoginInstructionsRequest("https://claude.ai/oauth/authorize?code=grant-value", null)));
        Assert.True(sessions.Instructions(claude.Handle, "runner-a",
            new DeviceLoginInstructionsRequest("https://claude.ai/oauth/authorize?code=true&client_id=studio", null)));
        Assert.Throws<ArgumentException>(() => sessions.Instructions(codex.Handle, "runner-a",
            new DeviceLoginInstructionsRequest("https://auth.openai.com/device", null)));
        var view = sessions.Get(claude.Handle, claude.SessionKey!);
        Assert.Equal(("host-a", "claude"), (view?.HostId, view?.Provider));
        Assert.Equal("https://claude.ai/oauth/authorize?code=true&client_id=studio", view?.VerificationUrl);
        Assert.Throws<ArgumentException>(() => sessions.Start("host-a", "gemini", "owner"));
    }

    [Fact]
    public async Task Pending_login_expires_with_injected_clock()
    {
        var clock = Clock();
        var audit = new List<(string Provider, string Result)>();
        var sessions = new DeviceLoginSessions(clock, (evt, _, _) =>
        {
            audit.Add((evt.Provider, evt.Status));
            return Task.CompletedTask;
        });
        var login = sessions.Start("host-a", "claude", "owner");
        clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Null(sessions.Claim("host-a", "runner-a"));
        var expired = sessions.Get(login.Handle, login.SessionKey!);
        Assert.Equal("failed", expired?.State);
        Assert.Equal("The sign-in timed out.", expired?.Detail);
        await sessions.SweepAsync(default);
        Assert.Equal(("claude", "timeout"), Assert.Single(audit));
    }

    private static DeviceLoginSessions Sessions(ManualTimeProvider clock)
        => new(clock, (_, _, _) => Task.CompletedTask);

    private static ManualTimeProvider Clock()
        => new(new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero));
}
