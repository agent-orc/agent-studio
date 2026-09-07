using AgentStudio.Management;
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
            "sk-ant-oat01-provider-secret-fixture");

        Assert.Null(ProviderAuthProvisioningPolicy.Validate(request));
        Assert.Equal("claude", ProviderAuthProvisioningPolicy.ProviderFor(environmentVariable));
    }

    [Fact]
    public void SshTransport_KeepsSecretOutOfEveryProcessArgument()
    {
        const string secret = "sk-ant-oat01-never-on-the-command-line";
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
        Assert.DoesNotContain("claude.env", standardInput, StringComparison.Ordinal);
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
}
