using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public class RunnerOptionsTests
{
    [Fact]
    public void Positional_argument_is_the_task_key()
    {
        var (_, taskKey, once, help) = RunnerOptions.Parse(["AGT-1939"]);
        Assert.Equal("AGT-1939", taskKey);
        Assert.True(once);
        Assert.False(help);
    }

    [Fact]
    public void Flags_override_task_and_server()
    {
        using var token = new TemporaryTokenFile();
        var (options, taskKey, _, _) = RunnerOptions.Parse(
            ["--task", "AGT-1", "--server", "https://central/", "--runner-name", "agent-runner-01", "--auth-token-file", token.Path]);
        Assert.Equal("AGT-1", taskKey);
        Assert.Equal("https://central", options.ServerUrl); // trailing slash trimmed
        Assert.Equal("agent-runner-01", options.RunnerName);
    }

    [Fact]
    public void Help_flag_is_detected_without_a_task()
    {
        var (_, taskKey, _, help) = RunnerOptions.Parse(["--help"]);
        Assert.True(help);
        Assert.Null(taskKey);
    }

    [Fact]
    public void Poll_flag_disables_once()
    {
        var (_, _, once, _) = RunnerOptions.Parse(["AGT-1", "--poll"]);
        Assert.False(once);
    }

    [Fact]
    public void Daemon_slot_flags_are_parsed()
    {
        var (options, _, _, _) = RunnerOptions.Parse(
            [
                "--poll",
                "--max-parallelism", "4",
                "--poll-seconds", "9",
                "--server-request-timeout-seconds", "17",
                "--idle-watchdog-minutes", "3",
            ]);
        Assert.Equal(4, options.HostMaxParallelism);
        Assert.Equal(9, options.PollSeconds);
        Assert.Equal(17, options.ServerRequestTimeoutSeconds);
        Assert.Equal(3, options.IdleWatchdogMinutes);
    }

    /// <summary>
    /// AGT-2866: both roles must derive the same cores-per-slot budget, so both
    /// slot counts are host configuration, not per-role configuration. The
    /// default is this service's own ceiling on both sides, which matches the
    /// symmetric two-coding-plus-two-review install.
    /// </summary>
    [Fact]
    public void Host_slot_split_defaults_to_this_service_ceiling_on_both_sides()
    {
        using var environment = new EnvironmentVariableScope(
            ("RUNNER_HOST_CODING_SLOTS", null),
            ("RUNNER_HOST_REVIEW_SLOTS", null),
            ("RUNNER_WORKER_CPU_BURST", null),
            ("RUNNER_WORKER_ENVELOPE", null));

        var (options, _, _, _) = RunnerOptions.Parse(["--poll", "--max-parallelism", "2"]);

        Assert.Equal(2, options.HostCodingSlots);
        Assert.Equal(2, options.HostReviewSlots);
        Assert.Equal(WorkerResourceEnvelope.DefaultCpuBurst, options.WorkerCpuBurst);
        Assert.True(options.WorkerEnvelopeEnabled);
    }

    [Fact]
    public void Host_slot_split_burst_and_envelope_switch_are_configurable()
    {
        using var environment = new EnvironmentVariableScope(
            ("RUNNER_HOST_CODING_SLOTS", "3"),
            // A coding-only host declares zero review slots so its workers get
            // the whole machine's budget instead of reserving half of it.
            ("RUNNER_HOST_REVIEW_SLOTS", "0"),
            ("RUNNER_WORKER_CPU_BURST", "1.5"),
            ("RUNNER_WORKER_ENVELOPE", "0"));

        var (options, _, _, _) = RunnerOptions.Parse(["--poll"]);

        Assert.Equal(3, options.HostCodingSlots);
        Assert.Equal(0, options.HostReviewSlots);
        Assert.Equal(1.5, options.WorkerCpuBurst);
        Assert.False(options.WorkerEnvelopeEnabled);
    }

    [Fact]
    public void Agent_host_environment_aliases_are_accepted()
    {
        using var environment = new EnvironmentVariableScope(
            ("RUNNER_SERVER_URL", null),
            ("RUNNER_ID", null),
            ("RUNNER_MAX_PARALLELISM", null),
            ("AGENT_HOST_SERVER_URL", "http://127.0.0.1:5031"),
            ("AGENT_HOST_ID", "agent-runner-01"),
            ("AGENT_HOST_MAX_PARALLELISM", "3"));

        var (options, _, _, _) = RunnerOptions.Parse(["--poll"]);

        Assert.Equal("http://127.0.0.1:5031", options.ServerUrl);
        Assert.Equal("agent-runner-01", options.RunnerId);
        Assert.Equal(3, options.HostMaxParallelism);
    }

    [Fact]
    public void Bootstrap_runner_environment_takes_precedence_over_agent_host_alias()
    {
        using var environment = new EnvironmentVariableScope(
            ("RUNNER_NAME", "agent-runner-01"),
            ("AGENT_HOST_NAME", "new-host-name"));

        var (options, _, _, _) = RunnerOptions.Parse(["--poll"]);

        Assert.Equal("agent-runner-01", options.RunnerName);
    }

    [Fact]
    public void Provider_specific_resume_args_require_and_preserve_session_placeholder()
    {
        var (options, _, _, _) = RunnerOptions.Parse(
            ["--cli-resume-args", "exec resume {sessionId} --json"]);

        Assert.Equal("exec resume {sessionId} --json", options.CliResumeArgs);
        Assert.Throws<ArgumentException>(() => RunnerOptions.Parse(
            ["--cli-resume-args", "exec resume fixed-session --json"]));
    }

    [Fact]
    public void Provider_specific_card_binaries_are_configurable_in_both_directions()
    {
        var (options, _, _, _) = RunnerOptions.Parse([
            "--cli", "/opt/bin/codex",
            "--claude-cli", "/opt/bin/claude",
            "--codex-cli", "/opt/bin/codex-card",
        ]);

        Assert.Equal("/opt/bin/codex", options.CliBin);
        Assert.Equal("/opt/bin/claude", options.ClaudeCliBin);
        Assert.Equal("/opt/bin/codex-card", options.CodexCliBin);
    }

    [Fact]
    public void Durable_state_defaults_below_the_configured_work_directory()
    {
        var work = Path.Combine("runner", "persistent-work");

        var (options, _, _, _) = RunnerOptions.Parse(["--poll", "--workdir", work]);

        Assert.Equal(Path.Combine(work, ".runner-state"), options.StateDir);
    }

    [Fact]
    public void Durable_state_directory_can_be_configured_separately()
    {
        var state = Path.Combine("runner", "persistent-state");

        var (options, _, _, _) = RunnerOptions.Parse(["--poll", "--state-dir", state]);

        Assert.Equal(state, options.StateDir);
    }

    [Fact]
    public void Existing_client_identity_can_be_pinned_from_the_cli()
    {
        var (options, _, _, _) = RunnerOptions.Parse(["--client-id", "  agent-runner-01  "]);

        Assert.Equal("agent-runner-01", options.ClientId);
    }

    [Fact]
    public void Fetch_and_push_remotes_can_be_configured_separately()
    {
        var (options, _, _, _) = RunnerOptions.Parse([
            "--git-remote", "https://github.com/acme/repo.git",
            "--git-push-remote", "git@github.com:acme/repo.git"]);

        Assert.Equal("https://github.com/acme/repo.git", options.GitRemote);
        Assert.Equal("git@github.com:acme/repo.git", options.GitPushRemote);
    }

    [Theory]
    [InlineData("AGT-20", "AGT-20")]
    [InlineData("project/task 20", "project-task-20")]
    public void Worktree_segment_is_filesystem_safe(string input, string expected)
        => Assert.Equal(expected, GitWorkspace.SafeSegment(input));

    [Fact]
    public void Project_id_maps_to_an_isolated_shared_clone_cache()
    {
        var root = Path.Combine("runner", "work");

        Assert.Equal(
            Path.Combine(root, "PROJ-042"),
            GitWorkspace.CachePathForProject(root, "PROJ-042"));
        Assert.NotEqual(
            GitWorkspace.CachePathForProject(root, "PROJ-042"),
            GitWorkspace.CachePathForProject(root, "PROJ-043"));
    }

    [Fact]
    public void Missing_project_id_keeps_the_legacy_single_repo_cache_path()
        => Assert.Equal("runner-work", GitWorkspace.CachePathForProject("runner-work", null));

    [Fact]
    public void Health_check_flag_sets_health_check_only_and_needs_no_task_key()
    {
        var (options, taskKey, _, help) = RunnerOptions.Parse(["--health-check"]);
        Assert.True(options.HealthCheckOnly);
        Assert.Null(taskKey);
        Assert.False(help);
    }

    [Fact]
    public void Health_check_only_defaults_false_for_a_normal_run()
    {
        var (options, _, _, _) = RunnerOptions.Parse(["AGT-1"]);
        Assert.False(options.HealthCheckOnly);
    }

    [Fact]
    public void Non_loopback_server_requires_https_and_service_credential()
    {
        Assert.Throws<ArgumentException>(() => RunnerOptions.Parse(["--server", "http://tasks.example.com"]));
        Assert.Throws<ArgumentException>(() => RunnerOptions.Parse(["--server", "https://tasks.example.com"]));
        using var token = new TemporaryTokenFile();
        var (options, _, _, _) = RunnerOptions.Parse([
            "--server", "https://tasks.example.com", "--auth-token-file", token.Path]);
        Assert.Equal("https://tasks.example.com", options.ServerUrl);
    }

    [Fact]
    public void Insecure_http_needs_an_explicit_opt_in_outside_loopback()
    {
        using var token = new TemporaryTokenFile();
        // A container-network Task Server is addressed by service name over plain
        // HTTP and never published outside that network. Without the opt-in this
        // stays refused; the error names the way out.
        var refused = Assert.Throws<ArgumentException>(() => RunnerOptions.Parse([
            "--server", "http://orchestrator-api:5031", "--auth-token-file", token.Path]));
        Assert.Contains("RUNNER_ALLOW_INSECURE_HTTP", refused.Message, StringComparison.Ordinal);

        using var environment = new EnvironmentVariableScope(("RUNNER_ALLOW_INSECURE_HTTP", "1"));
        var (options, _, _, _) = RunnerOptions.Parse([
            "--server", "http://orchestrator-api:5031", "--auth-token-file", token.Path]);

        Assert.True(options.AllowInsecureHttp);
        Assert.Equal("http://orchestrator-api:5031", options.ServerUrl);
    }

    [Fact]
    public void Insecure_http_opt_in_stays_off_by_default_and_never_admits_another_scheme()
    {
        using var token = new TemporaryTokenFile();
        var (secure, _, _, _) = RunnerOptions.Parse([
            "--server", "https://tasks.example.com", "--auth-token-file", token.Path]);
        Assert.False(secure.AllowInsecureHttp);

        using var environment = new EnvironmentVariableScope(("RUNNER_ALLOW_INSECURE_HTTP", "true"));
        Assert.Throws<ArgumentException>(() => RunnerOptions.Parse([
            "--server", "ftp://tasks.example.com", "--auth-token-file", token.Path]));
    }

    [Fact]
    public void Command_line_secret_is_rejected_to_keep_it_out_of_process_diagnostics()
        => Assert.Throws<ArgumentException>(() => RunnerOptions.Parse([
            "--server", "https://tasks.example.com", "--auth-token", "rnr.test.secret-value-long-enough"]));

    [Fact]
    public void Private_ca_certificate_pin_is_not_treated_as_a_secret()
    {
        using var token = new TemporaryTokenFile();
        var fingerprint = new string('A', 64);
        var (options, _, _, _) = RunnerOptions.Parse([
            "--server", "https://tasks.example.com",
            "--auth-token-file", token.Path,
            "--tls-certificate-sha256", fingerprint]);

        Assert.Equal(fingerprint, options.TlsServerCertificateSha256);
    }

    private sealed class TemporaryTokenFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "runner-token-" + Guid.NewGuid().ToString("N"));

        public TemporaryTokenFile()
        {
            File.WriteAllText(Path, "rnr.test.secret-value-long-enough");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        public void Dispose() => File.Delete(Path);
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly (string Name, string? Value)[] _original;

        public EnvironmentVariableScope(params (string Name, string? Value)[] values)
        {
            _original = values
                .Select(value => (value.Name, Environment.GetEnvironmentVariable(value.Name)))
                .ToArray();
            foreach (var (name, value) in values)
                Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            foreach (var (name, value) in _original)
                Environment.SetEnvironmentVariable(name, value);
        }
    }
}

public class AgentCliArgsTests
{
    [Fact]
    public void Simple_args_split_on_whitespace()
    {
        Assert.Equal(["-p", "--verbose"], AgentCliProcess.SplitArgs("-p --verbose"));
    }

    [Fact]
    public void Quoted_segment_stays_together()
    {
        Assert.Equal(["--flag", "two words"], AgentCliProcess.SplitArgs("--flag \"two words\""));
    }

    [Fact]
    public void Empty_args_yield_empty_list()
    {
        Assert.Empty(AgentCliProcess.SplitArgs(""));
    }
}
