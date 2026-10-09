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
        Assert.Contains("mv -fT -- \"$restore_tmp\" \"$provider_auth_file\"", script);
        Assert.Contains("systemctl restart \"$unit\"", script);
        Assert.Contains("install_committed=1", script);
        var trapStart = script.IndexOf("rollback_on_failure() {", StringComparison.Ordinal);
        var trapEnd = script.IndexOf("trap rollback_on_failure EXIT", trapStart, StringComparison.Ordinal);
        var trap = script[trapStart..trapEnd];
        Assert.Contains("systemctl stop \"$unit\"", trap);
        Assert.Contains("/proc/$1/environ", trap);
        Assert.Contains("printf 'recovery-required\\n' >\"$receipt_file\"", trap);
        Assert.Contains("if ((result == 0 && verified_all == 1)); then\n    rm -f -- \"$rollback_file\"", trap);
        Assert.Contains("for dropin in \"${created_dropins[@]}\"", trap);
        Assert.True(trap.IndexOf("/proc/$1/environ", StringComparison.Ordinal)
            < trap.IndexOf("provider-auth-rollback-restored", StringComparison.Ordinal));
        Assert.Contains("install -m 0600 -o root -g root \"$provider_auth_file\" \"$rollback_file\"", script);
        Assert.Contains("install -m 0600 -o root -g root /dev/null \"$rollback_file\"", script);
        Assert.True(script.IndexOf("verified_all=1", StringComparison.Ordinal) <
            script.LastIndexOf("install_committed=1", StringComparison.Ordinal));
        Assert.Contains("installed-awaiting-runner\\n%s\\n%s\\n%s\\n", script);
        Assert.Contains("printf 'pending=%s\\n' \"$unit\"", script);
    }

    [Fact]
    public void Pending_provisioning_keeps_copy_until_host_verifies_or_restores_every_unit()
    {
        var provision = SshProviderAuthProvisioner.BuildStandardInput(
            "ANTHROPIC_API_KEY", "fixture-provider-key-value",
            new ProviderAuthRenewalFence("renewal_fixture", "generation-a"));
        var finalizer = SshProviderAuthProvisioner.BuildPendingFinalizationScriptForTest("renewal_fixture");
        Assert.Contains("if ((result == 0 && verified_all == 1))", provision);
        Assert.Contains("\"$rollback_file\" \"$had_prior_file\"", provision);
        Assert.Contains("printf 'pending=%s\\n' \"$unit\"", provision);
        Assert.Contains("/proc/${pid}/environ", finalizer);
        Assert.Contains("[[ \"$generation\" == \"$installed_generation\" ]]", finalizer);
        Assert.True(finalizer.IndexOf("/proc/${pid}/environ", StringComparison.Ordinal) <
            finalizer.LastIndexOf("rm -f -- \"$rollback_file\"", StringComparison.Ordinal));
        Assert.Contains("trap recover ERR", finalizer);
        Assert.Contains("mv -fT -- \"$restore_tmp\" \"$provider_auth_file\"", finalizer);
        Assert.Contains("systemctl stop \"$unit\"", finalizer);
        Assert.Contains("printf 'recovery-required\\n' >\"$receipt_file\"", finalizer);
    }

    [Theory]
    [InlineData("staged", "install -m 0600 -o root -g root \"$provider_auth_file\" \"$rollback_file\"")]
    [InlineData("installed", "installed=1")]
    [InlineData("unit-restarted", "systemctl restart \"$unit\"")]
    [InlineData("unit-verified", "/proc/${main_pid}/environ")]
    [InlineData("unit-pending", "installed-awaiting-runner\\n%s\\n%s\\n%s\\n")]
    [InlineData("journal-present", "receipt_file=\"$receipt_dir/$operation_id\"")]
    [InlineData("interrupted-install", "printf 'started\\n%s\\n%s\\n%s\\n' \"$rollback_file\"")]
    [InlineData("pending-without-central-operation", "operation_id=\"local_${generation}\"")]
    [InlineData("protected-copy", "install -m 0600 -o root -g root")]
    [InlineData("rollback-failure", "systemctl stop \"$unit\"")]
    public void Renewal_lifecycle_states_have_host_receipts(string state, string required)
    {
        var script = SshProviderAuthProvisioner.BuildStandardInput(
            "ANTHROPIC_API_KEY", "fixture-provider-key-value");
        Assert.True(script.Contains(required, StringComparison.Ordinal),
            $"Missing host lifecycle state: {state}");
    }

    [Theory]
    [InlineData("pending-finalization", "installed-awaiting-runner")]
    [InlineData("interrupted-install", "[[ \"${receipt[0]}\" == started ]]")]
    [InlineData("verify-live-unit", "/proc/${pid}/environ")]
    [InlineData("retire-copy", "rm -f -- \"$rollback_file\"")]
    [InlineData("restore-prior", "mv -fT -- \"$restore_tmp\" \"$provider_auth_file\"")]
    [InlineData("drain-on-failure", "systemctl stop \"$unit\"")]
    public void Renewal_lifecycle_finalization_transitions_are_recoverable(string state, string required)
    {
        var script = SshProviderAuthProvisioner.BuildPendingFinalizationScriptForTest("renewal_fixture");
        Assert.True(script.Contains(required, StringComparison.Ordinal),
            $"Missing host finalization transition: {state}");
    }

    [Fact]
    public void Unfenced_pending_install_rolls_back_instead_of_orphaning_the_copy()
    {
        var script = SshProviderAuthProvisioner.BuildStandardInput(
            "ANTHROPIC_API_KEY", "fixture-provider-key-value");
        Assert.Contains("operation_id=\"local_${generation}\"", script);
        Assert.Contains("[[ -n \"$expected_generation\" ]] || { echo 'provider-auth-unverified-local-install' >&2; exit 75; }", script);
        Assert.True(script.IndexOf("receipt_file=\"$receipt_dir/$operation_id\"", StringComparison.Ordinal) <
            script.IndexOf("installed=1", StringComparison.Ordinal));
        Assert.Contains("printf 'rollback-restored\\n' >\"$receipt_file\"", script);
        Assert.Contains("printf '%s\\n' \"$rollback_file\" >>\"$receipt_file\"", script);
    }

    [Fact]
    public void Pending_host_finalizer_discovers_late_units_and_cleans_copy_before_verification_receipt()
    {
        var script = SshProviderAuthProvisioner.BuildPendingFinalizationScriptForTest("renewal_fixture");
        Assert.Contains("if ((${#units[@]} == 0)); then", script);
        Assert.Contains("systemctl cat agent-runner.service", script);
        Assert.Contains("pending=(\"${units[@]}\")", script);
        Assert.True(script.LastIndexOf("rm -f -- \"$rollback_file\"", StringComparison.Ordinal) <
            script.LastIndexOf("printf 'verified\\n' >\"$receipt_file\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Interrupted_install_keeps_a_discoverable_copy_and_drains_units()
    {
        var install = SshProviderAuthProvisioner.BuildStandardInput(
            "ANTHROPIC_API_KEY", "fixture-provider-key-value");
        var finalizer = SshProviderAuthProvisioner.BuildPendingFinalizationScriptForTest("renewal_fixture");
        Assert.True(install.IndexOf("printf 'started\\n%s\\n%s\\n%s\\n'", StringComparison.Ordinal) <
            install.IndexOf("install -m 0600 -o root -g root \"$provider_auth_file\" \"$rollback_file\"", StringComparison.Ordinal));
        Assert.Contains("if [[ \"${receipt[0]}\" == started ]]; then", finalizer);
        Assert.Contains("systemctl stop \"$unit\"", finalizer);
        Assert.Contains("printf 'recovery-required\\n%s\\n' \"$rollback_file\" >\"$receipt_file\"", finalizer);
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
                    script.LastIndexOf("agent-host --rebind-provider-auth claude", StringComparison.Ordinal));
    }

    [Fact]
    public void Claude_native_renewal_restores_prior_store_and_drains_units_if_recovery_fails()
    {
        var script = SshClaudeDeviceAuthTransport.BuildFencedScriptForTest(
            "renewal_fixture", "native-cli-store:1791460920000", native: true);

        Assert.Contains("cp -p -- \"$credential_store\" \"$rollback_store\"", script);
        Assert.Contains("chmod 0600 \"$rollback_store\"", script);
        Assert.Contains("trap rollback_native_on_failure EXIT", script);
        Assert.Contains("mv -f -- \"$restore_tmp\" \"$credential_store\"", script);
        Assert.Contains("== \"$current_generation\"", script);
        Assert.Contains("!= \"$previous_store_digest\"", script);
        Assert.Contains("agent-host --rebind-provider-auth claude --drained", script);
        Assert.Contains("sudo -n systemctl stop \"$unit\"", script);
        Assert.Contains("[[ \"$pid\" =~ ^[1-9][0-9]*$ ]] || rollback_ok=0", script);
        Assert.Contains("done <\"/proc/$1/environ\"", script);
        Assert.Contains("stat -Lc %d:%i \"$unit_store\"", script);
        Assert.Contains("sha256sum \"$unit_store\"", script);
        Assert.Contains("native-cli-store:$(date -r \"$unit_store\" +%s%3N)", script);
        Assert.Contains("claude-login-status=recovery-required", script);
        Assert.Contains("printf 'recovery-required\\n' >\"$receipt_file\"", script);
        Assert.True(script.IndexOf("cp -p -- \"$credential_store\"", StringComparison.Ordinal) <
                    script.IndexOf("claude /login", StringComparison.Ordinal));
        var restore = script.IndexOf("mv -f -- \"$restore_tmp\" \"$credential_store\"", StringComparison.Ordinal);
        Assert.True(restore < script.IndexOf("agent-host --rebind-provider-auth claude --drained", restore, StringComparison.Ordinal));
        Assert.True(script.IndexOf("trap rollback_native_on_failure EXIT", StringComparison.Ordinal) <
                    script.IndexOf("claude /login", StringComparison.Ordinal));
    }

    [Fact]
    public void Claude_environment_renewal_restores_prior_file_or_drains_units_after_restart_failure()
    {
        var script = SshClaudeDeviceAuthTransport.BuildFencedScriptForTest(
            "renewal_fixture", "generation-a", native: false);
        Assert.Contains("trap rollback_environment_on_failure EXIT", script);
        Assert.Contains("install -m 0640 -o root -g agent \"$rollback_file\" \"$restore_tmp\"", script);
        Assert.Contains("mv -fT -- \"$restore_tmp\" \"$provider_auth_file\"", script);
        Assert.Contains("done <\"/proc/$1/environ\"", script);
        Assert.Contains("sudo -n systemctl stop \"$unit\"", script);
        Assert.Contains("claude-login-status=recovery-required", script);
        Assert.Contains("printf 'recovery-required\\n' >\"$receipt_file\"", script);
        Assert.Contains("sudo -n systemctl restart \"$unit\" || exit 44", script);
    }

    [Fact]
    public void Codex_renewal_restores_prior_store_or_drains_units_after_partial_rebind()
    {
        var script = SshCodexDeviceAuthTransport.BuildFencedScriptForTest(
            "renewal_fixture", "native-cli-store:1791460920000");
        Assert.Contains("trap rollback_codex_on_failure EXIT", script);
        Assert.Contains("cp -p -- \"$credential_store\" \"$rollback_store\"", script);
        Assert.Contains("agent-host --rebind-provider-auth codex --drained", script);
        Assert.Contains("sudo -n systemctl stop \"$unit\"", script);
        Assert.Contains("[[ \"$pid\" =~ ^[1-9][0-9]*$ ]] || rollback_ok=0", script);
        Assert.Contains("done <\"/proc/$1/environ\"", script);
        Assert.Contains("stat -Lc %d:%i \"$unit_store\"", script);
        Assert.Contains("sha256sum \"$unit_store\"", script);
        Assert.Contains("native-cli-store:$(date -r \"$unit_store\" +%s%3N)", script);
        Assert.Contains("codex-login-status=recovery-required", script);
        Assert.Contains("printf 'recovery-required\\n' >\"$receipt_file\"", script);
        Assert.Contains("new_store_digest=", script);
        Assert.Contains("published_generation=", script);
        Assert.Contains("sudo -n systemctl restart \"$unit\" || exit 44", script);
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
