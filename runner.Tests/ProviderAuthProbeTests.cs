using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// Stage S2 of docs/operations/token-refresh-ohne-tunnel.md: the runner may only
/// advertise <c>provider-auth</c> as ready when it actually asked the CLI. Every
/// test drives the probe through an injected launcher, so the suite never needs a
/// real claude/codex installation and never starts a process.
/// </summary>
public sealed class ProviderAuthProbeTests
{
    private static ProviderAuthLauncher Answers(int exitCode, string stdout = "", string stderr = "")
        => (_, _, _) => Task.FromResult(new ProcessResult(exitCode, stdout, stderr));

    private static ProviderAuthProbe Probe(
        ProviderAuthLauncher launcher,
        bool binaryExists = true,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? ttl = null,
        TimeSpan? timeout = null,
        int negativeConfirmations = ProviderAuthProbe.DefaultNegativeConfirmations,
        Action<string>? diagnosticLog = null,
        Func<string, ProviderCredentialFreshness>? credentialFreshness = null)
        => new(
            launcher,
            _ => binaryExists,
            clock,
            ttl,
            timeout,
            negativeConfirmations,
            diagnosticLog,
            credentialFreshness ?? (_ => new ProviderCredentialFreshness(
                null,
                null,
                "Credential metadata fixture has no expiry.")));

    [Theory]
    [InlineData(0, "Not logged in. Run `claude auth login` to sign in.")]
    [InlineData(1, "Error: login required")]
    [InlineData(1, "HTTP 401 Unauthorized")]
    [InlineData(1, "OAuth token expired")]
    public async Task Two_explicit_dead_session_answers_are_unavailable_whatever_the_exit_code(
        int exitCode,
        string output)
    {
        var probe = Probe(Answers(exitCode, output));

        var first = await probe.RefreshAsync("claude", CancellationToken.None);
        var status = await probe.RefreshAsync("claude", CancellationToken.None);

        Assert.Equal(ProviderAuthProbe.Ready, first.Status);
        Assert.True(first.ProbeDegraded);
        Assert.Equal(ProviderAuthProbe.Unavailable, status.Status);
        Assert.Equal(ProviderAuthProbe.SignalSignedOut, status.Signal);
        Assert.Contains("no usable session", status.Detail, StringComparison.Ordinal);
        Assert.Contains("claude auth status --text", status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_live_session_is_the_only_thing_that_earns_ready()
    {
        var status = await Probe(Answers(0, "Logged in as Agent Studio (subscription)"))
            .RefreshAsync("codex", CancellationToken.None);

        Assert.Equal(ProviderAuthProbe.Ready, status.Status);
        Assert.True(status.IsReady);
        Assert.Contains("codex login status", status.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("unverified", status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_binary_is_unavailable_and_nothing_is_launched()
    {
        var launched = false;
        var probe = Probe(
            (_, _, _) => { launched = true; return Task.FromResult(new ProcessResult(0, "", "")); },
            binaryExists: false);

        var status = await probe.RefreshAsync("claude", CancellationToken.None);

        Assert.Equal(ProviderAuthProbe.Unavailable, status.Status);
        Assert.Contains("was not found", status.Detail, StringComparison.Ordinal);
        Assert.False(launched);
    }

    [Fact]
    public async Task An_indeterminate_busy_probe_retains_last_good_and_logs_degraded()
    {
        var logs = new List<string>();
        var calls = 0;
        var probe = Probe(
            async (_, _, ct) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                    return new ProcessResult(0, "Login method: Claude Max account", "");
                await Task.Delay(TimeSpan.FromMinutes(5), ct);
                return new ProcessResult(0, "", "");
            },
            timeout: TimeSpan.FromMilliseconds(50),
            diagnosticLog: logs.Add);

        var lastGood = await probe.RefreshAsync("claude", CancellationToken.None);
        var status = await probe.RefreshAsync("claude", CancellationToken.None);

        Assert.Equal(ProviderAuthProbe.Ready, lastGood.Status);
        Assert.Equal(ProviderAuthProbe.Ready, status.Status);
        Assert.True(status.ProbeDegraded);
        Assert.Contains("did not answer", status.Detail, StringComparison.Ordinal);
        Assert.Contains(logs, line => line.StartsWith(
            "runner-provider-auth-probe-degraded ",
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_launcher_that_throws_is_indeterminate_and_retains_presence_status()
    {
        var probe = Probe((_, _, _) => throw new InvalidOperationException("spawn refused by the host"));

        var status = await probe.RefreshAsync("claude", CancellationToken.None);

        Assert.Equal(ProviderAuthProbe.Ready, status.Status);
        Assert.True(status.ProbeDegraded);
        Assert.Contains("could not be started", status.Detail, StringComparison.Ordinal);
        Assert.Contains("spawn refused by the host", status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unsupported_status_subcommand_stays_ready_and_admits_it_proved_nothing()
    {
        // A CLI version that renamed the subcommand must not drain the host: an
        // argument-parser rejection is "could not ask", not "the login is gone".
        var status = await Probe(Answers(2, "", "error: unrecognized subcommand 'auth'\nUsage: claude [OPTIONS]"))
            .RefreshAsync("claude", CancellationToken.None);

        Assert.Equal(ProviderAuthProbe.Ready, status.Status);
        Assert.True(status.ProbeDegraded);
        Assert.Contains("unverified", status.Detail, StringComparison.Ordinal);
        Assert.Contains(ProviderAuthProbe.ConceptPath, status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_provider_keeps_the_presence_check_instead_of_guessing_a_command()
    {
        var launched = false;
        var probe = Probe((_, _, _) => { launched = true; return Task.FromResult(new ProcessResult(0, "", "")); });

        var status = await probe.RefreshAsync("agent-wrapper.sh", CancellationToken.None);

        Assert.Equal(ProviderAuthProbe.Ready, status.Status);
        Assert.Contains("no auth status command is known", status.Detail, StringComparison.Ordinal);
        Assert.False(launched);
        Assert.Null(ProviderAuthProbe.AuthStatusArguments("agent-wrapper"));
    }

    [Fact]
    public void Without_a_wired_launcher_the_status_degrades_to_the_path_check_and_says_so()
    {
        var present = new ProviderAuthProbe(launcher: null, executableExists: _ => true).Current("claude");
        Assert.Equal(ProviderAuthProbe.Ready, present.Status);
        Assert.Contains("no auth probe is wired", present.Detail, StringComparison.Ordinal);
        Assert.Contains(ProviderAuthProbe.ConceptPath, present.Detail, StringComparison.Ordinal);

        var absent = new ProviderAuthProbe(launcher: null, executableExists: _ => false).Current("claude");
        Assert.Equal(ProviderAuthProbe.Unavailable, absent.Status);
    }

    [Fact]
    public async Task The_verdict_is_cached_for_the_ttl_and_refreshed_behind_the_advertisement()
    {
        var calls = 0;
        var now = new DateTimeOffset(2026, 7, 28, 8, 0, 0, TimeSpan.Zero);
        var probe = Probe(
            (_, _, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(new ProcessResult(0, "Logged in", ""));
            },
            clock: () => now,
            ttl: TimeSpan.FromMinutes(5));

        await probe.RefreshAsync("claude", CancellationToken.None);
        Assert.Equal(1, Volatile.Read(ref calls));

        // Four minutes and a dozen advertisements later: still one child process.
        now = now.AddMinutes(4);
        for (var i = 0; i < 12; i++) Assert.Equal(ProviderAuthProbe.Ready, probe.Current("claude").Status);
        Assert.Equal(1, Volatile.Read(ref calls));

        // Past the TTL the stale verdict is still served, and the refresh happens
        // behind it - the caller is never blocked on the CLI.
        now = now.AddMinutes(2);
        Assert.Equal(ProviderAuthProbe.Ready, probe.Current("claude").Status);
        await WaitUntil(() => Volatile.Read(ref calls) == 2);
    }

    [Fact]
    public async Task The_advertisement_carries_the_observed_status_and_detail()
    {
        var probe = Probe(Answers(1, "", "Not logged in"));
        var options = CodingOptions();

        await probe.RefreshAsync(options.ClaudeCliBin, CancellationToken.None);
        await probe.RefreshAsync(options.ClaudeCliBin, CancellationToken.None);
        var advertised = RunnerCapabilityProbe.Advertise(options, gitPushReady: true, providerAuth: probe);

        var auth = Assert.Single(
            advertised,
            item => item.Key == CapabilityProtocol.ProviderAuthentication("claude"));
        Assert.Equal(ProviderAuthProbe.Unavailable, auth.Status);
        Assert.Contains("no usable session", auth.Detail!, StringComparison.Ordinal);
        // The status only bites because the capability stays a claim requirement:
        // the task server admits a claim while every required key reads "ready".
        Assert.Contains(
            CapabilityProtocol.ProviderAuthentication("claude"),
            RunnerCapabilityProbe.CodingRequirements(options));
    }

    [Fact]
    public async Task The_detail_never_carries_a_token_shaped_string()
    {
        var probe = Probe(Answers(1, "", "invalid api key sk-ant-api03-AAAABBBBCCCCDDDDEEEEFFFF"));
        await probe.RefreshAsync("claude", CancellationToken.None);
        var status = await probe.RefreshAsync("claude", CancellationToken.None);

        Assert.Equal(ProviderAuthProbe.Unavailable, status.Status);
        Assert.Contains("[redacted]", status.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-ant-api03", status.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// AGT-2823: an isolation gap let the idle probe's stdout pick up a
    /// stream-json frame from a real, concurrently running agent session
    /// instead of a `claude auth status --text` answer. The fragment's text
    /// (a tool_result carrying "Exit code 2" and a `git log` line mentioning
    /// "timeouts") is exactly what accidentally matched the transient-signal
    /// list and produced the reported `outcome=transient` log line. The fix
    /// must recognise the stream-json shape before classification runs, so a
    /// leaked frame is never read as a logout signal and never echoed back
    /// verbatim in the advertised detail.
    /// </summary>
    [Fact]
    public async Task Leaked_agent_stream_json_frame_is_indeterminate_and_never_leaks_content()
    {
        const string leakedFrame =
            "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\"," +
            "\"content\":\"Exit code 2\\nb5c278564 fix(frontend-tests): raise Vitest test and hook " +
            "timeouts to 30 s\\ndfc478ad7 fix(prepare): give the Windows preparation run the " +
            "environment NuGet, npm and MSBuild need\"}]}}";
        var probe = Probe(Answers(1, leakedFrame));

        var first = await probe.RefreshAsync("claude", CancellationToken.None);
        var second = await probe.RefreshAsync("claude", CancellationToken.None);

        foreach (var status in new[] { first, second })
        {
            Assert.Equal(ProviderAuthProbe.Ready, status.Status);
            Assert.True(status.ProbeDegraded);
            Assert.NotEqual(ProviderAuthProbe.SignalSignedOut, status.Signal);
            Assert.DoesNotContain("tool_result", status.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain("b5c278564", status.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain("Exit code 2", status.Detail, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Empty_success_output_is_indeterminate_and_does_not_erase_last_good()
    {
        var calls = 0;
        var probe = Probe((_, _, _) => Task.FromResult(
            Interlocked.Increment(ref calls) == 1
                ? new ProcessResult(0, "Login method: Claude Max account", "")
                : new ProcessResult(0, "", "")));

        Assert.Equal(
            ProviderAuthProbe.Ready,
            (await probe.RefreshAsync("claude", CancellationToken.None)).Status);
        var indeterminate = await probe.RefreshAsync("claude", CancellationToken.None);

        Assert.Equal(ProviderAuthProbe.Ready, indeterminate.Status);
        Assert.True(indeterminate.ProbeDegraded);
        Assert.Contains("empty output", indeterminate.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_successful_probe_recovers_confirmed_logout_without_restart()
    {
        var answers = new Queue<ProcessResult>(
        [
            new ProcessResult(1, "Not logged in", ""),
            new ProcessResult(1, "Not logged in", ""),
            new ProcessResult(0, "Login method: Claude Max account", ""),
        ]);
        var logs = new List<string>();
        var probe = Probe(
            (_, _, _) => Task.FromResult(answers.Dequeue()),
            diagnosticLog: logs.Add);

        Assert.Equal(
            ProviderAuthProbe.Ready,
            (await probe.RefreshAsync("claude", CancellationToken.None)).Status);
        Assert.Equal(
            ProviderAuthProbe.Unavailable,
            (await probe.RefreshAsync("claude", CancellationToken.None)).Status);

        var recovered = await probe.RefreshAsync("claude", CancellationToken.None);

        Assert.Equal(ProviderAuthProbe.Ready, recovered.Status);
        Assert.False(recovered.ProbeDegraded);
        Assert.Contains(logs, line => line.StartsWith(
            "provider-auth status=ready binary=claude ",
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task Single_transient_runtime_failure_keeps_last_good_and_next_probe_recovers()
    {
        var probe = Probe(Answers(0, "Logged in"));
        await probe.RefreshAsync("codex", CancellationToken.None);

        var transient = probe.RecordProcessResult(
            "codex",
            new ProcessResult(1, "", "network error while token refresh in progress"));

        Assert.Equal(ProviderAuthProbe.Ready, transient.Status);
        Assert.Equal(ProviderAuthProbe.SignalTransient, transient.Signal);
        Assert.True(transient.ProbeDegraded);

        var recovered = await probe.RefreshAsync("codex", CancellationToken.None);
        Assert.Equal(ProviderAuthProbe.Ready, recovered.Status);
        Assert.Equal(ProviderAuthProbe.SignalOk, recovered.Signal);
        Assert.False(recovered.ProbeDegraded);
    }

    [Fact]
    public async Task Rate_limit_exit_one_is_limited_and_never_reads_as_signed_out()
    {
        var now = new DateTimeOffset(2026, 9, 1, 17, 0, 0, TimeSpan.Zero);
        var probe = Probe(Answers(0, "Logged in"), clock: () => now);
        await probe.RefreshAsync("codex", CancellationToken.None);

        var limited = probe.RecordProcessResult(
            "codex",
            new ProcessResult(1, "", "rate limit exceeded; resets at 2026-09-01T18:00:00Z"));

        Assert.Equal(ProviderAuthProbe.Limited, limited.Status);
        Assert.Equal(ProviderAuthProbe.SignalLimited, limited.Signal);
        Assert.Equal(
            new DateTimeOffset(2026, 9, 1, 18, 0, 0, TimeSpan.Zero),
            limited.LimitedUntil);
        Assert.False(RunnerCapabilityProbe.IsProviderAuthenticationFailure(
            new ProcessResult(1, "", "rate limit exceeded")));

        var beforeReset = await probe.RefreshAsync("codex", CancellationToken.None);
        Assert.Equal(ProviderAuthProbe.Limited, beforeReset.Status);
        now = now.AddHours(2);
        var recovered = await probe.RefreshAsync("codex", CancellationToken.None);
        Assert.Equal(ProviderAuthProbe.Ready, recovered.Status);
        Assert.Equal(ProviderAuthProbe.SignalOk, recovered.Signal);
    }

    [Fact]
    public async Task Successful_run_whose_output_mentions_rate_limits_keeps_the_capability_ready()
    {
        // 15.09.2026: an agent read runner docs containing "rate-limited" and a
        // rate_limit_event warning; the exit-0 run marked claude limited for
        // 15 minutes and every Claude card on the host was refused.
        const string agentOutput =
            "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"content\":\"Rate limits retain a provider-scoped Limited state; a rate-limited provider shows usage limit\"}]}}\n"
            + "{\"type\":\"rate_limit_event\",\"rate_limit_info\":{\"status\":\"allowed_warning\",\"rateLimitType\":\"seven_day\",\"utilization\":0.9}}";
        var probe = Probe(Answers(0, "Logged in"));
        var ready = await probe.RefreshAsync("claude", CancellationToken.None);

        var afterRun = probe.RecordProcessResult("claude", new ProcessResult(0, agentOutput, "HTTP 429 in a quoted log line"));

        Assert.Equal(ready, afterRun);
        Assert.Equal(ProviderAuthProbe.Ready, afterRun.Status);
        Assert.Null(afterRun.LimitedUntil);
    }

    [Fact]
    public async Task Allowed_warning_event_at_84_percent_weekly_is_authenticated()
    {
        const string eventLine =
            "{\"type\":\"rate_limit_event\",\"rate_limit_info\":{\"status\":\"allowed_warning\"," +
            "\"rateLimitType\":\"seven_day\",\"utilization\":0.84,\"resetsAt\":1790000000}}";
        var probe = Probe(Answers(0, "Logged in"));
        await probe.RefreshAsync("claude", CancellationToken.None);

        var afterRun = probe.RecordProcessResult(
            "claude",
            new ProcessResult(1, eventLine, "usage limit warning"),
            evidenceId: "run-84-percent");

        Assert.Equal(ProviderAuthProbe.Ready, afterRun.Status);
        Assert.Null(afterRun.LimitedUntil);
    }

    [Theory]
    [InlineData(143, 15)]
    [InlineData(137, 9)]
    public async Task Recorded_signal_terminated_run_never_contributes_rate_limit_evidence(
        int exitCode,
        int signal)
    {
        var probe = Probe(Answers(0, "Logged in"));
        var ready = await probe.RefreshAsync("claude", CancellationToken.None);

        var afterRun = probe.RecordProcessResult(
            "claude",
            new ProcessResult(exitCode, "", "usage limit reached; resets at 2026-09-19T12:40:00Z"),
            evidenceId: "operator-stopped-run",
            signal: signal);

        Assert.Equal(ready, afterRun);
        Assert.Equal(ProviderAuthProbe.Ready, afterRun.Status);
    }

    [Fact]
    public async Task High_nonzero_exit_without_recorded_signal_keeps_provider_refusal_evidence()
    {
        var probe = Probe(Answers(0, "Logged in"));
        await probe.RefreshAsync("claude", CancellationToken.None);

        var afterRun = probe.RecordProcessResult(
            "claude",
            new ProcessResult(143, "", "usage limit reached; resets at 2026-09-19T12:40:00Z"),
            evidenceId: "ordinary-high-exit");

        Assert.Equal(ProviderAuthProbe.Limited, afterRun.Status);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Operator_or_host_terminated_run_never_contributes_rate_limit_evidence(
        bool operatorStopped,
        bool hostShutdown)
    {
        var probe = Probe(Answers(0, "Logged in"));
        var ready = await probe.RefreshAsync("claude", CancellationToken.None);

        var afterRun = probe.RecordProcessResult(
            "claude",
            new ProcessResult(1, "", "usage limit reached; resets at 2026-09-19T12:40:00Z"),
            evidenceId: "terminated-run",
            operatorStopped: operatorStopped,
            hostShutdown: hostShutdown);

        Assert.Equal(ready, afterRun);
    }

    [Fact]
    public async Task Limit_transition_logs_and_advertises_scrubbed_evidence()
    {
        var logs = new List<string>();
        var now = new DateTimeOffset(2026, 9, 18, 10, 0, 0, TimeSpan.Zero);
        var probe = Probe(Answers(0, "Logged in"), clock: () => now, diagnosticLog: logs.Add);
        await probe.RefreshAsync("claude", CancellationToken.None);

        var status = probe.RecordProcessResult(
            "claude",
            new ProcessResult(1, "", "usage limit reached; resets at 2026-09-18T12:40:00Z"),
            evidenceId: "run-2870");

        Assert.Equal("run-2870", status.EvidenceId);
        Assert.Contains("usage limit reached", status.EvidenceExcerpt, StringComparison.Ordinal);
        Assert.Contains(logs, line =>
            line.Contains("provider-auth status=limited binary=claude", StringComparison.Ordinal)
            && line.Contains("until=2026-09-18T12:40:00.0000000Z", StringComparison.Ordinal)
            && line.Contains("evidence=run-2870", StringComparison.Ordinal));
        var advertised = RunnerCapabilityProbe.Advertise(
            CodingOptions(),
            gitPushReady: true,
            providerAuth: probe);
        var capability = Assert.Single(advertised, item =>
            item.Key == CapabilityProtocol.ProviderAuthentication("claude"));
        Assert.Equal("run-2870", capability.EvidenceId);
        Assert.Contains("usage limit reached", capability.EvidenceExcerpt, StringComparison.Ordinal);
        Assert.Equal(status.LimitedUntil?.UtcDateTime, capability.LimitedUntil);
    }

    [Fact]
    public async Task Active_same_provider_run_downgrades_limit_to_claimable_degraded()
    {
        var probe = Probe(Answers(0, "Logged in"));
        await probe.RefreshAsync("claude", CancellationToken.None);
        probe.RecordRunStarted("claude");
        try
        {
            var status = probe.RecordProcessResult(
                "claude",
                new ProcessResult(1, "", "usage limit reached; resets at 2026-09-19T12:40:00Z"),
                evidenceId: "other-run");

            Assert.Equal(ProviderAuthProbe.Degraded, status.Status);
            Assert.Contains("counter-evidence", status.Detail, StringComparison.Ordinal);
        }
        finally
        {
            probe.RecordRunCompleted("claude");
        }
    }

    [Fact]
    public async Task Expired_limit_becomes_claimable_and_forces_auth_reprobe()
    {
        var calls = 0;
        var now = new DateTimeOffset(2026, 9, 18, 10, 0, 0, TimeSpan.Zero);
        var probe = Probe(
            (_, _, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(new ProcessResult(0, "Logged in", ""));
            },
            clock: () => now);
        await probe.RefreshAsync("claude", CancellationToken.None);
        probe.RecordProcessResult(
            "claude",
            new ProcessResult(1, "", "usage limit reached; resets at 2026-09-18T10:01:00Z"),
            evidenceId: "run-expiring");

        now = now.AddMinutes(2);
        var duringReprobe = probe.Current("claude");

        Assert.Equal(ProviderAuthProbe.Degraded, duringReprobe.Status);
        await WaitUntil(() => probe.Current("claude").Status == ProviderAuthProbe.Ready);
        Assert.Equal(ProviderAuthProbe.Ready, probe.Current("claude").Status);
    }

    [Fact]
    public async Task Apply_patch_tool_failure_does_not_change_available_capability()
    {
        const string toolError = "ERROR codex_core::tools::router: error=apply_patch verification failed: "
            + "Failed to find context 'public sealed class V1ReviewExecutorRegistry' in "
            + "/home/agent/runner-work/PROJ-002/worktrees/AGT-2694/backend/Features/Runner/V1ReviewPlaneEndpoints.cs";
        var probe = Probe(Answers(0, "Logged in"));
        var ready = await probe.RefreshAsync("codex", CancellationToken.None);

        var afterToolError = probe.RecordProcessResult("codex", new ProcessResult(1, "", toolError));

        Assert.Equal(ready, afterToolError);
        Assert.Equal(ProviderAuthProbe.Ready, afterToolError.Status);
        Assert.Equal(ProviderAuthProbe.SignalOk, afterToolError.Signal);
    }

    [Fact]
    public async Task Credential_expiry_warning_is_quiet_and_does_not_block_claims()
    {
        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var expiresAt = now.AddDays(10);
        var probe = Probe(
            Answers(0, "Logged in"),
            clock: () => now,
            credentialFreshness: _ => new ProviderCredentialFreshness(
                expiresAt,
                now.AddDays(-20),
                "Credential expiry metadata was read."));

        var status = await probe.RefreshAsync("codex", CancellationToken.None);

        Assert.Equal(ProviderAuthProbe.Ready, status.Status);
        Assert.Equal(ProviderAuthProbe.SignalExpiring, status.Signal);
        Assert.Equal(expiresAt, status.ExpiresAt);
        Assert.Contains("re-authentication may be needed soon", status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Codex_auth_json_jwt_expiry_is_read_without_exposing_the_token()
    {
        var home = Path.Combine(Path.GetTempPath(), $"provider-auth-home-{Guid.NewGuid():N}");
        var expiresAt = new DateTimeOffset(2026, 9, 12, 8, 30, 0, TimeSpan.Zero);
        try
        {
            Directory.CreateDirectory(Path.Combine(home, ".codex"));
            var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{{\"exp\":{expiresAt.ToUnixTimeSeconds()}}}"))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var token = $"fixture-header.{payload}.fixture-signature";
            File.WriteAllText(
                Path.Combine(home, ".codex", "auth.json"),
                JsonSerializer.Serialize(new { tokens = new { access_token = token } }));

            var freshness = ProviderCredentialMonitor.Inspect("codex", home);

            Assert.Equal(expiresAt, freshness.ExpiresAt);
            Assert.DoesNotContain(token, freshness.Detail, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void Claude_credentials_json_millisecond_expiry_is_read_without_values()
    {
        var home = Path.Combine(Path.GetTempPath(), $"provider-auth-home-{Guid.NewGuid():N}");
        var expiresAt = new DateTimeOffset(2026, 9, 8, 18, 0, 0, TimeSpan.Zero);
        try
        {
            Directory.CreateDirectory(Path.Combine(home, ".claude"));
            File.WriteAllText(
                Path.Combine(home, ".claude", ".credentials.json"),
                JsonSerializer.Serialize(new
                {
                    claudeAiOauth = new
                    {
                        accessToken = "secret-fixture",
                        expiresAt = expiresAt.ToUnixTimeMilliseconds(),
                    },
                }));

            var freshness = ProviderCredentialMonitor.Inspect("claude", home);

            Assert.Equal(expiresAt, freshness.ExpiresAt);
            Assert.DoesNotContain("secret-fixture", freshness.Detail, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task Expired_unavailable_cache_recovers_in_the_background_without_restart()
    {
        var calls = 0;
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var probe = Probe(
            (_, _, _) => Task.FromResult(
                Interlocked.Increment(ref calls) <= 2
                    ? new ProcessResult(1, "Not logged in", "")
                    : new ProcessResult(0, "Login method: Claude Max account", "")),
            clock: () => now,
            ttl: TimeSpan.FromMinutes(5));

        await probe.RefreshAsync("claude", CancellationToken.None);
        Assert.Equal(
            ProviderAuthProbe.Unavailable,
            (await probe.RefreshAsync("claude", CancellationToken.None)).Status);

        now = now.AddMinutes(6);
        Assert.Equal(ProviderAuthProbe.Unavailable, probe.Current("claude").Status);
        await WaitUntil(() => probe.Current("claude").Status == ProviderAuthProbe.Ready);

        Assert.Equal(3, Volatile.Read(ref calls));
        Assert.Equal(ProviderAuthProbe.Ready, probe.Current("claude").Status);
    }

    [Fact]
    public async Task An_indeterminate_probe_breaks_the_consecutive_logout_sequence()
    {
        var answers = new Queue<ProcessResult>(
        [
            new ProcessResult(1, "Not logged in", ""),
            new ProcessResult(1, "", "ordinary startup failure"),
            new ProcessResult(1, "Not logged in", ""),
        ]);
        var probe = Probe((_, _, _) => Task.FromResult(answers.Dequeue()));

        await probe.RefreshAsync("claude", CancellationToken.None);
        await probe.RefreshAsync("claude", CancellationToken.None);
        var status = await probe.RefreshAsync("claude", CancellationToken.None);

        Assert.Equal(ProviderAuthProbe.Ready, status.Status);
        Assert.True(status.ProbeDegraded);
        Assert.Contains("1/2", status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Default_probe_timeout_allows_slow_node_cli_startup()
        => Assert.Equal(TimeSpan.FromSeconds(30), ProviderAuthProbe.DefaultTimeout);

    [Fact]
    public void Linux_auth_probe_invocation_uses_nice_without_shell_parsing()
    {
        var invocation = ProviderAuthProbe.LowPriorityInvocation(
            "/opt/claude cli/bin/claude",
            ["auth", "status", "--text"],
            path => path == "/usr/bin/nice");

        if (!OperatingSystem.IsLinux())
        {
            Assert.False(invocation.LowerPriority);
            return;
        }

        Assert.True(invocation.LowerPriority);
        Assert.Equal("/usr/bin/nice", invocation.FileName);
        Assert.Equal(
            ["-n", "10", "--", "/opt/claude cli/bin/claude", "auth", "status", "--text"],
            invocation.Arguments);
    }

    [SkippableFact]
    [Trait("Category", "MachineBound")]
    [Trait("Category", "ReviewFlaky")]
    public async Task Artificial_host_load_and_a_timed_out_status_process_do_not_flip_last_good()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "The production low-priority probe applies on Linux hosts.");
        using var load = new CancellationTokenSource();
        var burners = Enumerable.Range(0, Math.Clamp(Environment.ProcessorCount, 2, 8))
            .Select(_ => ProcessRunner.RunAsync(
                "/bin/sh",
                ["-c", "while :; do :; done"],
                ct: load.Token))
            .ToArray();
        var calls = 0;
        var logs = new List<string>();
        var probe = Probe(
            async (_, _, ct) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                    return new ProcessResult(0, "Login method: Claude Max account", "");
                var invocation = ProviderAuthProbe.LowPriorityInvocation(
                    "/bin/sh",
                    ["-c", "sleep 2; printf 'Login method: Claude Max account\\n'"]);
                return await ProcessRunner.RunAsync(invocation.FileName, invocation.Arguments, ct: ct);
            },
            timeout: TimeSpan.FromMilliseconds(100),
            diagnosticLog: logs.Add);

        try
        {
            Assert.Equal(
                ProviderAuthProbe.Ready,
                (await probe.RefreshAsync("claude", CancellationToken.None)).Status);

            var underLoad = await probe.RefreshAsync("claude", CancellationToken.None);

            Assert.Equal(ProviderAuthProbe.Ready, underLoad.Status);
            Assert.True(underLoad.ProbeDegraded);
            Assert.Contains("did not answer", underLoad.Detail, StringComparison.Ordinal);
            Assert.Contains(logs, line => line.Contains(
                "outcome=indeterminate retainedStatus=ready",
                StringComparison.Ordinal));
        }
        finally
        {
            load.Cancel();
            try { await Task.WhenAll(burners); }
            catch (OperationCanceledException) { }
        }
    }

    private static RunnerOptions CodingOptions() => new()
    {
        ServerUrl = "http://task-server",
        RunnerId = "runner-test",
        RunnerName = "runner-test",
        Hostname = "test-host",
        BackendName = "test",
        GitRemote = "https://github.com/example/repo.git",
        WorkDir = Path.GetTempPath(),
        BaseBranch = "main",
        ClaudeCliBin = "claude",
    };

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The background refresh did not run within 5s.");
            await Task.Delay(10);
        }
    }
}
