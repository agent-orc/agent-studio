using AgentStudio.Management;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ProviderAuthProvisioningTests
{
    [Theory]
    [InlineData("CLAUDE_CODE_OAUTH_TOKEN")]
    [InlineData("ANTHROPIC_API_KEY")]
    public void Policy_AcceptsOnlyTheTwoClaudeEnvironmentInputs(string environmentVariable)
    {
        var request = new ProviderAuthProvisioningRequest(
            "agent@runner-01",
            "agent-runner-01",
            environmentVariable,
            "fixture" + "-provider-secret-value");

        Assert.Null(ProviderAuthProvisioningPolicy.Validate(request));
        Assert.Equal("claude", ProviderAuthProvisioningPolicy.ProviderFor(environmentVariable));
    }

    [Fact]
    public void SshTransport_KeepsSecretOutOfEveryProcessArgument()
    {
        var secret = "fixture" + "-never-on-the-command-line";
        var startInfo = SshProviderAuthProvisioner.BuildStartInfo(
            "agent@runner-01",
            "CLAUDE_CODE_OAUTH_TOKEN");
        var standardInput = SshProviderAuthProvisioner.BuildStandardInput(
            "CLAUDE_CODE_OAUTH_TOKEN",
            secret);

        Assert.Equal("ssh", startInfo.FileName);
        Assert.DoesNotContain(startInfo.ArgumentList, argument => argument.Contains(secret, StringComparison.Ordinal));
        Assert.DoesNotContain(secret, standardInput, StringComparison.Ordinal);
        Assert.Contains(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(secret)), standardInput);
        Assert.Contains("/etc/agent-runner/provider-auth.env", standardInput);
        Assert.Contains("getent group agent >/dev/null || groupadd --system agent", standardInput);
        Assert.Contains("install -m 0640 -o root -g agent", standardInput);
        Assert.Contains("mv -fT -- \"$provider_auth_install_tmp\" \"$provider_auth_file\"", standardInput);
        Assert.Contains("units+=(agent-host.service)", standardInput);
        Assert.Contains("units+=(agent-runner-review.service)", standardInput);
        Assert.Contains("EnvironmentFile=%s", standardInput);
        Assert.Contains("/proc/${main_pid}/environ", standardInput);
        Assert.Contains("/usr/local/sbin/agent-runner-deploy restart-review", standardInput);
        Assert.Contains("provider-auth-unit-pending=", standardInput);
        Assert.DoesNotContain("claude.env", standardInput, StringComparison.Ordinal);
    }

    [Fact]
    public void Provisioning_failure_restores_prior_environment_and_restarts_changed_units()
    {
        var script = SshProviderAuthProvisioner.BuildStandardInput(
            "ANTHROPIC_API_KEY", "fixture-provider-key-value");
        Assert.Contains("trap rollback_on_failure EXIT", script);
        Assert.Contains("mv -fT -- \"$rollback_file\" \"$provider_auth_file\"", script);
        Assert.Contains("systemctl restart \"$unit\"", script);
        Assert.Contains("install_committed=1", script);
    }

    [Fact]
    public void Managed_api_key_renewal_fences_the_prior_generation_under_a_host_lock()
    {
        var script = SshProviderAuthProvisioner.BuildStandardInput(
            "ANTHROPIC_API_KEY", "fixture-provider-key-value",
            new ProviderAuthRenewalFence("renewal_fixture", "generation-a"));
        Assert.Contains("operation_id='renewal_fixture'", script);
        Assert.Contains("expected_generation='generation-a'", script);
        Assert.Contains("flock -n 9", script);
        Assert.Contains("current_generation\" == \"$expected_generation", script);
        Assert.Contains("provider-auth-recovery-required", script);
    }

    [Fact]
    public void Claude_native_fence_accepts_native_store_generation()
    {
        var script = SshClaudeDeviceAuthTransport.BuildFencedScriptForTest(
            "renewal_fixture", "native-cli-store:1791460920000", native: true);
        Assert.Contains("expected_generation='native-cli-store:1791460920000'", script);
    }

    [Fact]
    public void Claude_native_renewal_requires_changed_store_and_publishes_a_distinct_generation()
    {
        var script = SshClaudeDeviceAuthTransport.BuildFencedScriptForTest(
            "renewal_fixture", "native-cli-store:1791460920000", native: true);

        Assert.Contains("previous_store_digest=", script);
        Assert.Contains("new_store_digest=", script);
        Assert.Contains("[[ \"$previous_store_digest\" != \"$new_store_digest\" ]]", script);
        Assert.Contains("published_generation=", script);
        Assert.Contains("[[ \"$published_generation\" != \"$expected_generation\" ]]", script);
        Assert.Contains("claude-login-status=unchanged-generation", script);
        Assert.Contains("touch -m -d \"@$next_second\"", script);
        Assert.True(script.IndexOf("claude auth status --text", StringComparison.Ordinal) <
                    script.IndexOf("new_store_digest=", StringComparison.Ordinal));
        Assert.True(script.IndexOf("new_store_digest=", StringComparison.Ordinal) <
                    script.IndexOf("agent-host --rebind-provider-auth claude", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CodexDeviceAuth_FakeSshTranscriptReturnsInstructionsAndAuditsOneTerminalOutcome()
    {
        var transport = new FakeCodexDeviceAuthTransport();
        var audit = new RecordingProviderSignInAudit();
        var coordinator = new CodexSignInCoordinator(transport, audit);

        var started = await coordinator.StartAsync(
            "agent-runner-01",
            new CodexSignInRequest("agent@runner-01"),
            "operator-7",
            CancellationToken.None);

        Assert.Equal("pending", started.State);
        Assert.Equal("https://auth.openai.com/codex/device", started.VerificationUrl);
        Assert.Equal("ABCD-EFGH", started.UserCode);
        Assert.Equal("agent@runner-01", transport.SshTarget);
        Assert.DoesNotContain(started.UserCode, transport.ProcessArguments, StringComparison.Ordinal);

        transport.Complete(new CodexDeviceAuthTransportResult(
            0,
            LoginStatusVerified: true,
            RestartedServices: ["agent-host.service"]));

        CodexSignInStatusResponse? status = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            status = coordinator.Get("agent-runner-01", started.Handle);
            if (status?.State == "completed") break;
            await Task.Delay(10);
        }

        Assert.NotNull(status);
        Assert.Equal("completed", status!.State);
        Assert.Single(audit.Events);
        Assert.Equal(new ProviderSignInAuditEvent(
            "agent-runner-01",
            "codex",
            "operator-7",
            "completed"), audit.Events[0]);
        Assert.DoesNotContain("ABCD-EFGH", string.Join('|', audit.Events.Select(evt => evt.ToString())));
    }

    [Fact]
    public async Task CodexDeviceAuth_FailedStatusProducesOneSanitizedAuditOutcome()
    {
        var transport = new FakeCodexDeviceAuthTransport();
        var audit = new RecordingProviderSignInAudit();
        var coordinator = new CodexSignInCoordinator(transport, audit);
        var started = await coordinator.StartAsync(
            "agent-runner-01",
            new CodexSignInRequest("runner-01"),
            "local-default",
            CancellationToken.None);

        transport.Complete(new CodexDeviceAuthTransportResult(42, false, []));
        for (var attempt = 0; attempt < 50
             && coordinator.Get("agent-runner-01", started.Handle)?.State == "pending"; attempt++)
            await Task.Delay(10);

        var status = coordinator.Get("agent-runner-01", started.Handle);
        Assert.Equal("failed", status?.State);
        Assert.DoesNotContain("ABCD-EFGH", status?.Detail ?? string.Empty, StringComparison.Ordinal);
        Assert.Single(audit.Events);
        Assert.Equal("failed", audit.Events[0].Outcome);
    }

    [Fact]
    public void CodexSshTransport_UsesFixedScriptOverStdinAndNoInteractiveTerminal()
    {
        var startInfo = SshCodexDeviceAuthTransport.BuildStartInfo("agent@runner-01");

        Assert.Equal(TimeSpan.FromMinutes(15), CodexSignInCoordinator.SessionTimeout);
        Assert.Equal("ssh", startInfo.FileName);
        Assert.Contains("BatchMode=yes", startInfo.ArgumentList);
        Assert.Contains("-T", startInfo.ArgumentList);
        Assert.DoesNotContain("device-auth", startInfo.ArgumentList);
        Assert.Equal("bash", startInfo.ArgumentList[^2]);
        Assert.Equal("-s", startInfo.ArgumentList[^1]);
    }

    [Fact]
    public async Task Durable_codex_login_waits_for_real_proof_and_restart_status_reads_receipt()
    {
        var transport = new FakeCodexDeviceAuthTransport();
        var journal = new FakeRenewalJournal("R3");
        var coordinator = new CodexSignInCoordinator(transport, new RecordingProviderSignInAudit(), journal);
        var started = await coordinator.StartAsync("agent-runner-01",
            new CodexSignInRequest("runner-01", "operation-one"), "operator", default);
        transport.Complete(new CodexDeviceAuthTransportResult(0, true,
            ["agent-runner.service", "agent-runner-review.service"]));
        for (var attempt = 0; attempt < 50 && journal.Step != "installed"; attempt++)
            await Task.Delay(10);
        Assert.Equal("installed", journal.Step);
        Assert.Equal("pending", (await coordinator.GetAsync("agent-runner-01", started.Handle, default))?.State);

        journal.RealProof = true;
        var restarted = new CodexSignInCoordinator(new FakeCodexDeviceAuthTransport(),
            new RecordingProviderSignInAudit(), journal);
        Assert.Equal("completed", (await restarted.GetAsync("agent-runner-01", started.Handle, default))?.State);
        Assert.Equal("complete", journal.Step);
    }

    [Fact]
    public async Task ClaudeDeviceAuth_FakeSshTranscriptReturnsUrlAndAuditsOneTerminalOutcome()
    {
        var transport = new FakeClaudeDeviceAuthTransport();
        var audit = new RecordingProviderSignInAudit();
        var coordinator = new ClaudeSignInCoordinator(transport, audit);

        var started = await coordinator.StartAsync(
            "agent-runner-01",
            new ClaudeSignInRequest("agent@runner-01"),
            "operator-7",
            CancellationToken.None);

        Assert.Equal("pending", started.State);
        Assert.Equal("https://claude.ai/setup-token/abc123", started.VerificationUrl);
        Assert.Equal("agent@runner-01", transport.SshTarget);

        transport.Complete(new ClaudeDeviceAuthTransportResult(
            0,
            LoginStatusVerified: true,
            RestartedServices: ["agent-host.service"]));

        ClaudeSignInStatusResponse? status = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            status = coordinator.Get("agent-runner-01", started.Handle);
            if (status?.State == "completed") break;
            await Task.Delay(10);
        }

        Assert.NotNull(status);
        Assert.Equal("completed", status!.State);
        Assert.Single(audit.Events);
        Assert.Equal(new ProviderSignInAuditEvent(
            "agent-runner-01",
            "claude",
            "operator-7",
            "completed"), audit.Events[0]);
    }

    [Fact]
    public async Task ClaudeDeviceAuth_FailedStatusProducesOneAuditOutcome()
    {
        var transport = new FakeClaudeDeviceAuthTransport();
        var audit = new RecordingProviderSignInAudit();
        var coordinator = new ClaudeSignInCoordinator(transport, audit);
        var started = await coordinator.StartAsync(
            "agent-runner-01",
            new ClaudeSignInRequest("runner-01"),
            "local-default",
            CancellationToken.None);

        transport.Complete(new ClaudeDeviceAuthTransportResult(42, false, []));
        for (var attempt = 0; attempt < 50
             && coordinator.Get("agent-runner-01", started.Handle)?.State == "pending"; attempt++)
            await Task.Delay(10);

        var status = coordinator.Get("agent-runner-01", started.Handle);
        Assert.Equal("failed", status?.State);
        Assert.Single(audit.Events);
        Assert.Equal("failed", audit.Events[0].Outcome);
    }

    [Fact]
    public void ClaudeSshTransport_UsesFixedScriptOverStdinAndNoInteractiveTerminal()
    {
        var startInfo = SshClaudeDeviceAuthTransport.BuildStartInfo("agent@runner-01");

        Assert.Equal(TimeSpan.FromMinutes(15), ClaudeSignInCoordinator.SessionTimeout);
        Assert.Equal("ssh", startInfo.FileName);
        Assert.Contains("BatchMode=yes", startInfo.ArgumentList);
        Assert.Contains("-T", startInfo.ArgumentList);
        Assert.DoesNotContain("sudo", startInfo.ArgumentList);
        Assert.Equal("bash", startInfo.ArgumentList[^2]);
        Assert.Equal("-s", startInfo.ArgumentList[^1]);
    }

    private sealed class FakeClaudeDeviceAuthTransport : IClaudeDeviceAuthTransport
    {
        private readonly TaskCompletionSource<ClaudeDeviceAuthTransportResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string SshTarget { get; private set; } = "";

        public ClaudeDeviceAuthTransportSession Start(
            string sshTarget,
            Action<string> onOutput,
            CancellationToken cancellationToken)
        {
            SshTarget = sshTarget;
            onOutput("Please visit the following URL to authenticate:");
            onOutput("https://claude.ai/setup-token/abc123");
            onOutput("Waiting for authentication to complete in the browser...");
            cancellationToken.Register(() => _completion.TrySetCanceled(cancellationToken));
            return new ClaudeDeviceAuthTransportSession(
                _completion.Task,
                () => _completion.TrySetCanceled());
        }

        public void Complete(ClaudeDeviceAuthTransportResult result) => _completion.TrySetResult(result);
    }

    private sealed class FakeCodexDeviceAuthTransport : ICodexDeviceAuthTransport
    {
        private readonly TaskCompletionSource<CodexDeviceAuthTransportResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string SshTarget { get; private set; } = "";
        public string ProcessArguments { get; private set; } = "ssh -T";

        public CodexDeviceAuthTransportSession Start(
            string sshTarget,
            Action<string> onOutput,
            CancellationToken cancellationToken)
        {
            SshTarget = sshTarget;
            onOutput("Open this URL in your browser:");
            onOutput("https://auth.openai.com/codex/device");
            onOutput("Enter this one-time code: ABCD-EFGH");
            cancellationToken.Register(() => _completion.TrySetCanceled(cancellationToken));
            return new CodexDeviceAuthTransportSession(
                _completion.Task,
                () => _completion.TrySetCanceled());
        }

        public void Complete(CodexDeviceAuthTransportResult result) => _completion.TrySetResult(result);
    }

    private sealed class RecordingProviderSignInAudit : IProviderSignInAudit
    {
        public List<ProviderSignInAuditEvent> Events { get; } = [];

        public Task WriteAsync(ProviderSignInAuditEvent evt, CancellationToken cancellationToken = default)
        {
            Events.Add(evt);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRenewalJournal(string method) : IProviderRenewalJournal
    {
        private readonly DateTime _now = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
        private ProviderRenewalReceiptDto? _receipt;
        public string? Step => _receipt?.Step;
        public bool RealProof { get; set; }

        public Task<ProviderRenewalReceiptDto> BeginAsync(string hostId, string requestedMethod,
            string actorId, string? key, CancellationToken ct)
        {
            Assert.Equal(method, requestedMethod);
            _receipt = new("renewal_fixture", "installation", hostId, "credential", "generation-a",
                method, key ?? "operation-one", actorId, _now.AddMinutes(15), "requested", null,
                null, [], false, [], _now);
            return Task.FromResult(_receipt);
        }

        public Task<ProviderRenewalReceiptDto> AdvanceAsync(string operationId, string step,
            CancellationToken ct)
        {
            _receipt = _receipt! with { Step = step };
            return Task.FromResult(_receipt);
        }

        public Task<ProviderRenewalReceiptDto?> GetAsync(string operationId, CancellationToken ct)
            => Task.FromResult(_receipt);

        public Task<ProviderRenewalReceiptDto> TryVerifyAsync(ProviderRenewalReceiptDto receipt,
            CancellationToken ct)
        {
            if (RealProof) _receipt = receipt with { Step = "complete", RealRequestSucceeded = true };
            return Task.FromResult(_receipt!);
        }
    }
}
