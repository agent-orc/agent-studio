using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class RunnerCapabilityProbeTests
{
    [Theory]
    [InlineData(1, "", "HTTP 401 Missing bearer authentication", true)]
    [InlineData(1, "", "login required", true)]
    [InlineData(1, "", "ordinary product failure", false)]
    [InlineData(1, "", "rate limit exceeded; resets at 2026-09-01T18:00:00Z", false)]
    [InlineData(1, "", "ERROR codex_core::tools::router: error=apply_patch verification failed: Failed to find context 'public sealed class V1ReviewExecutorRegistry' in /home/agent/runner-work/PROJ-002/worktrees/AGT-2694/backend/Features/Runner/V1ReviewPlaneEndpoints.cs", false)]
    [InlineData(0, "", "HTTP 401 in historical output", false)]
    public void Provider_authentication_failure_requires_a_nonzero_typed_signal(
        int exitCode,
        string stdout,
        string stderr,
        bool expected)
        => Assert.Equal(
            expected,
            RunnerCapabilityProbe.IsProviderAuthenticationFailure(
                new ProcessResult(exitCode, stdout, stderr)));

    [Theory]
    [InlineData("Selected model is at capacity. Please try a different model.")]
    [InlineData("The model is at capacity right now.")]
    public void Provider_capacity_is_a_bounded_rate_limit_not_authentication_failure(string message)
    {
        var observedAt = new DateTimeOffset(2026, 8, 31, 6, 45, 42, TimeSpan.Zero);

        var evidence = ProviderAccessClassifier.Classify(
            1,
            stdout: null,
            stderr: message,
            observedAt: observedAt);

        Assert.Equal(ProviderAccessEvidenceKind.RateLimited, evidence.Kind);
        Assert.Equal(observedAt.Add(ProviderAccessClassifier.UnknownLimitRetry), evidence.LimitedUntil);
        Assert.False(evidence.ResetTimeReported);
        Assert.False(RunnerCapabilityProbe.IsProviderAuthenticationFailure(
            new ProcessResult(1, "", message)));
    }

    [Theory]
    [InlineData("/usr/local/bin/codex", "codex")]
    [InlineData("claude.exe", "claude")]
    public void Provider_identity_is_stable_across_binary_paths(string binary, string expected)
        => Assert.Equal(expected, RunnerCapabilityProbe.Provider(binary));

    [Theory]
    [InlineData(
        "remote: refusing to allow a Personal Access Token to create or update workflow `.github/workflows/release.yml` without `workflow` scope",
        true)]
    [InlineData(
        "remote: GitHub App is not permitted to update workflow file .github/workflows/release.yml without Workflows permission",
        true)]
    [InlineData("remote: permission denied to refs/heads/runner/test", false)]
    [InlineData("fatal: Authentication failed for https://github.com/example/repo.git", false)]
    public void Workflow_scope_failure_requires_a_workflow_specific_rejection(
        string error,
        bool expected)
        => Assert.Equal(expected, GitPushProbe.IsWorkflowScopeFailure(error));

    [Fact]
    public void Workflow_scope_failure_ignores_successful_historical_output()
        => Assert.False(GitPushProbe.IsWorkflowScopeFailure(new ProcessResult(
            0,
            "previous warning mentioned .github/workflows and workflow scope",
            "")));

    [Fact]
    public void Capability_advertisement_reports_workflow_scope_without_making_it_a_claim_requirement()
    {
        var options = new RunnerOptions
        {
            ServerUrl = "http://task-server",
            RunnerId = "runner-test",
            RunnerName = "runner-test",
            Hostname = "test-host",
            BackendName = "test",
            GitRemote = "https://github.com/example/repo.git",
            WorkDir = Path.GetTempPath(),
            BaseBranch = "main",
            CliBin = "codex",
            CliArgs = "",
        };

        var advertised = RunnerCapabilityProbe.Advertise(
            options,
            gitPushReady: true,
            gitWorkflowPushReady: false,
            gitDetail: "workflow scope missing");

        Assert.Equal(
            "ready",
            Assert.Single(advertised, item => item.Key == CapabilityProtocol.GitPush).Status);
        var workflow = Assert.Single(
            advertised,
            item => item.Key == CapabilityProtocol.GitWorkflowPush);
        Assert.Equal(GitPushProbe.ReadyNoWorkflowScope, workflow.Status);
        Assert.Equal("workflow scope missing", workflow.Detail);
        Assert.DoesNotContain(
            CapabilityProtocol.GitWorkflowPush,
            RunnerCapabilityProbe.CodingRequirements(options));
        Assert.DoesNotContain(
            CapabilityProtocol.GitPush,
            RunnerCapabilityProbe.CodingRequirements(options));
    }

    [Fact]
    public async Task Capability_advertisement_reports_each_executable_card_cli_independently()
    {
        using var temp = new TempDirectory();
        var claude = Path.Combine(temp.Path, "claude");
        var codex = Path.Combine(temp.Path, "codex");
        await File.WriteAllTextAsync(claude, "");
        await File.WriteAllTextAsync(codex, "");
        var options = new RunnerOptions
        {
            ServerUrl = "http://task-server",
            RunnerId = "runner-test",
            RunnerName = "runner-test",
            Hostname = "test-host",
            BackendName = "test",
            GitRemote = "https://github.com/example/repo.git",
            WorkDir = temp.Path,
            BaseBranch = "main",
            CliBin = codex,
            ClaudeCliBin = claude,
            CodexCliBin = codex,
            CliArgs = "",
        };
        var probe = new ProviderAuthProbe(
            (binary, _, _) => Task.FromResult(
                Path.GetFileName(binary) == "claude"
                    ? new ProcessResult(1, "", "Not logged in")
                    : new ProcessResult(0, "Logged in", "")),
            File.Exists);
        await probe.RefreshAsync(claude, CancellationToken.None);
        await probe.RefreshAsync(claude, CancellationToken.None);
        await probe.RefreshAsync(codex, CancellationToken.None);

        var advertised = RunnerCapabilityProbe.Advertise(
            options,
            gitPushReady: true,
            providerAuth: probe);

        Assert.Equal(
            "ready",
            Assert.Single(
                advertised,
                item => item.Key == CapabilityProtocol.CliExecution("claude")).Status);
        Assert.Equal(
            "ready",
            Assert.Single(
                advertised,
                item => item.Key == CapabilityProtocol.CliExecution("codex")).Status);
        Assert.Equal(
            ProviderAuthProbe.Unavailable,
            Assert.Single(
                advertised,
                item => item.Key == CapabilityProtocol.ProviderAuthentication("claude")).Status);
        Assert.Equal(
            ProviderAuthProbe.Ready,
            Assert.Single(
                advertised,
                item => item.Key == CapabilityProtocol.ProviderAuthentication("codex")).Status);
    }

    [Theory]
    [InlineData(true, ProviderAuthProbe.Ready)]
    [InlineData(false, ProviderAuthProbe.Unavailable)]
    public async Task Claude_setup_token_environment_drives_provider_auth_advertisement(
        bool tokenProvisioned,
        string expectedStatus)
    {
        using var temp = new TempDirectory();
        var claude = Path.Combine(temp.Path, "claude");
        await File.WriteAllTextAsync(claude, "");
        using var environment = new EnvironmentVariableScope(
            ProviderAuthEnvironment.ClaudeCodeOAuthToken,
            tokenProvisioned ? "dummy-claude-setup-token-for-unit-test" : null);
        var options = new RunnerOptions
        {
            ServerUrl = "http://task-server",
            RunnerId = "runner-test",
            RunnerName = "runner-test",
            Hostname = "test-host",
            BackendName = "test",
            GitRemote = "https://github.com/example/repo.git",
            WorkDir = temp.Path,
            BaseBranch = "main",
            CliBin = claude,
            ClaudeCliBin = claude,
            CliArgs = "",
        };
        var probe = new ProviderAuthProbe(
            (_, _, _) => Task.FromResult(
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                    ProviderAuthEnvironment.ClaudeCodeOAuthToken))
                    ? new ProcessResult(1, "", "Not logged in")
                    : new ProcessResult(0, "Authenticated", "")),
            File.Exists);
        await probe.RefreshAsync(claude, CancellationToken.None);
        if (!tokenProvisioned)
            await probe.RefreshAsync(claude, CancellationToken.None);

        var advertised = RunnerCapabilityProbe.Advertise(
            options,
            gitPushReady: true,
            providerAuth: probe);

        Assert.Equal(
            expectedStatus,
            Assert.Single(
                advertised,
                item => item.Key == CapabilityProtocol.ProviderAuthentication("claude")).Status);
    }

    [Fact]
    public void Car_engine_is_advertised_as_the_canary_capability_and_legacy_is_not()
    {
        RunnerOptions Options(string engine) => new()
        {
            ServerUrl = "http://task-server",
            RunnerId = "runner-test",
            RunnerName = "runner-test",
            Hostname = "test-host",
            BackendName = "test",
            GitRemote = "https://github.com/example/repo.git",
            WorkDir = Path.GetTempPath(),
            BaseBranch = "main",
            ExecEngine = engine,
            CliBin = "codex",
            CliArgs = "",
        };

        // The canary mechanism of the CAR migration (plan §4): cohort cards
        // request exactly this key via RequiredCapabilities, so only CAR-engined
        // hosts claim them - no special routing path.
        var car = RunnerCapabilityProbe.Advertise(Options(RunnerOptions.ExecEngineCar), gitPushReady: true);
        Assert.Equal("ready", Assert.Single(car, item => item.Key == "exec-engine:car").Status);

        var legacy = RunnerCapabilityProbe.Advertise(Options(RunnerOptions.ExecEngineLegacy), gitPushReady: true);
        Assert.DoesNotContain(legacy, item => item.Key == "exec-engine:car");
    }

    [Fact]
    public void Missing_secondary_cli_is_advertised_as_unavailable()
    {
        var options = new RunnerOptions
        {
            ServerUrl = "http://task-server",
            RunnerId = "runner-test",
            RunnerName = "runner-test",
            Hostname = "test-host",
            BackendName = "test",
            GitRemote = "https://github.com/example/repo.git",
            WorkDir = Path.GetTempPath(),
            BaseBranch = "main",
            CliBin = "/bin/sh",
            CodexCliBin = Path.Combine(
                Path.GetTempPath(),
                $"missing-codex-{Guid.NewGuid():N}"),
            CliArgs = "",
        };

        var advertised = RunnerCapabilityProbe.Advertise(options, gitPushReady: true);

        Assert.Equal(
            ProviderAuthProbe.Unavailable,
            Assert.Single(
                advertised,
                item => item.Key == CapabilityProtocol.CliExecution("codex")).Status);
    }

    [Fact]
    public void Connectivity_outage_is_an_unavailable_capability_with_route_context()
    {
        var options = new RunnerOptions
        {
            ServerUrl = "http://127.0.0.1:15031",
            RunnerId = "runner-test",
            RunnerName = "runner-test",
            Hostname = "test-host",
            BackendName = "test",
            WorkDir = Path.GetTempPath(),
            BaseBranch = "main",
            CliBin = "/bin/sh",
            CliArgs = "",
        };
        var failureAt = new DateTime(2026, 8, 1, 15, 30, 0, DateTimeKind.Utc);

        var advertised = RunnerCapabilityProbe.Advertise(
            options,
            gitPushReady: true,
            connectivity: new TaskServerConnectivitySnapshot(
                TaskServerConnectivityStates.Unreachable,
                failureAt.AddMinutes(5),
                failureAt,
                11,
                failureAt.AddMinutes(5),
                "connection refused",
                null));

        var route = Assert.Single(
            advertised,
            capability => capability.Key == CapabilityProtocol.TaskServerConnectivity);
        Assert.Equal("unavailable", route.Status);
        Assert.Equal("127.0.0.1:15031", route.Identity);
        Assert.Contains("11 consecutive request failures", route.Detail);
    }

    [Fact]
    public void Salvage_gate_adds_the_token_scope_fix_for_a_real_workflow_rejection()
    {
        var rejection = new InvalidOperationException(
            "remote: refusing to allow a Personal Access Token to update workflow " +
            ".github/workflows/release.yml without workflow scope");
        var exception = new WorktreeSalvageException(
            "/runner/worktrees/AGT-2347",
            "runner/host/AGT-2347",
            rejection,
            localCommitSha: "abc123",
            remoteCommitSha: "def456");

        var gate = RemoteTaskRunner.BuildUnsecuredWorktreeGate("runner-host", exception);

        Assert.Contains("host=runner-host", gate);
        Assert.Contains("worktree=/runner/worktrees/AGT-2347", gate);
        Assert.Contains("branch=runner/host/AGT-2347", gate);
        Assert.Contains("fine-grained Contents: Read and write", gate);
        Assert.Contains("classic repo plus workflow", gate);
        Assert.Contains(GitPushProbe.TokenRequirementsPath, gate);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"runner-capability-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* best effort */ }
        }
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _original;

        public EnvironmentVariableScope(string name, string? value)
        {
            _name = name;
            _original = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _original);
    }
}
