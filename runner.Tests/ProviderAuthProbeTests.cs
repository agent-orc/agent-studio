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
    [InlineData("claude", 0, "Not logged in. Run `claude auth login` to sign in.")]
    [InlineData("codex", 1, "Error: login required")]
    [InlineData("claude", 1, "HTTP 401 Unauthorized")]
    [InlineData("codex", 1, "OAuth token expired")]
    public async Task One_explicit_dead_session_answer_is_unavailable_whatever_the_exit_code(
        string provider,
        int exitCode,
        string output)
    {
        var probe = Probe(Answers(exitCode, output));

        var status = await probe.RefreshAsync(provider, CancellationToken.None);

        Assert.Equal(ProviderAuthProbe.Unavailable, status.Status);
        Assert.Equal(ProviderAuthProbe.SignalSignedOut, status.Signal);
        Assert.Contains("no usable session", status.Detail, StringComparison.Ordinal);
        Assert.Contains(
            provider == "claude" ? "claude auth status --text" : "codex login status",
            status.Detail,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public async Task A_live_session_is_the_only_thing_that_earns_ready(string provider)
    {
        var status = await Probe(Answers(0, "Logged in as Agent Studio (subscription)"))
            .RefreshAsync(provider, CancellationToken.None);

        Assert.Equal(ProviderAuthProbe.Ready, status.Status);
        Assert.True(status.IsReady);
        Assert.Contains(
            provider == "claude" ? "claude auth status --text" : "codex login status",
            status.Detail,
            StringComparison.Ordinal);
        Assert.DoesNotContain("unverified", status.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public async Task A_missing_binary_is_unavailable_and_nothing_is_launched(string provider)
    {
        var launched = false;
        var probe = Probe(
            (_, _, _) => { launched = true; return Task.FromResult(new ProcessResult(0, "", "")); },
            binaryExists: false);

        var status = await probe.RefreshAsync(provider, CancellationToken.None);

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

        await probe.RefreshAsync(options.CliBin, CancellationToken.None);
        await probe.RefreshAsync(options.CliBin, CancellationToken.None);
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
            ProviderAuthProbe.Unavailable,
            (await probe.RefreshAsync("claude", CancellationToken.None)).Status);
        Assert.Equal(
            ProviderAuthProbe.Unavailable,
            (await probe.RefreshAsync("claude", CancellationToken.None)).Status);

        var recovered = await probe.RefreshAsync("claude", CancellationToken.None);

        Assert.Equal(ProviderAuthProbe.Ready, recovered.Status);
        Assert.False(recovered.ProbeDegraded);
        Assert.Contains(logs, line => line.StartsWith(
            "runner-provider-auth-probe-recovered ",
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

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public async Task Expired_credential_metadata_blocks_claims_even_when_status_output_is_stale(string provider)
    {
        var now = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
        var expiresAt = now.AddMinutes(-1);
        var probe = Probe(
            Answers(0, "Logged in"),
            clock: () => now,
            credentialFreshness: _ => new ProviderCredentialFreshness(
                expiresAt,
                now.AddDays(-30),
                "Credential expiry metadata was read."));

        var status = await probe.RefreshAsync(provider, CancellationToken.None);

        Assert.Equal(ProviderAuthProbe.Unavailable, status.Status);
        Assert.Equal(ProviderAuthProbe.SignalExpired, status.Signal);
        Assert.Equal(expiresAt, status.ExpiresAt);
        Assert.Contains("expired", status.Detail, StringComparison.OrdinalIgnoreCase);
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
        var probe = Probe(
            (_, _, _) => Task.FromResult(answers.Dequeue()),
            negativeConfirmations: 2);

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
        CliBin = "claude",
        CliArgs = "",
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
