using AgentStudio.Management;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ProviderAuthProvisioningTests
{
    [Fact]
    public async Task ProviderDeviceAuth_FakeSshTranscriptReturnsInstructionsAndAuditsOneTerminalOutcome()
    {
        var transport = new FakeProviderDeviceAuthTransport();
        var audit = new RecordingProviderSignInAudit();
        var coordinator = new ProviderSignInCoordinator(transport, audit);

        var started = await coordinator.StartAsync(
            "agent-runner-01",
            new ProviderSignInRequest("codex", "agent@runner-01"),
            "operator-7",
            CancellationToken.None);

        Assert.Equal("pending", started.State);
        Assert.Equal("https://auth.openai.com/codex/device", started.VerificationUrl);
        Assert.Equal("ABCD-EFGH", started.UserCode);
        Assert.Equal("agent@runner-01", transport.SshTarget);
        Assert.DoesNotContain(started.UserCode, transport.ProcessArguments, StringComparison.Ordinal);

        transport.Complete(new ProviderDeviceAuthTransportResult(
            0,
            LoginStatusVerified: true,
            RestartedServices: ["agent-host.service"]));

        ProviderSignInStatusResponse? status = null;
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
    public async Task ProviderDeviceAuth_FailedStatusProducesOneSanitizedAuditOutcome()
    {
        var transport = new FakeProviderDeviceAuthTransport();
        var audit = new RecordingProviderSignInAudit();
        var coordinator = new ProviderSignInCoordinator(transport, audit);
        var started = await coordinator.StartAsync(
            "agent-runner-01",
            new ProviderSignInRequest("codex", "runner-01"),
            "local-default",
            CancellationToken.None);

        transport.Complete(new ProviderDeviceAuthTransportResult(42, false, []));
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
        var startInfo = SshProviderDeviceAuthTransport.BuildStartInfo("agent@runner-01");

        Assert.Equal(TimeSpan.FromMinutes(15), ProviderSignInCoordinator.SessionTimeout);
        Assert.Equal("ssh", startInfo.FileName);
        Assert.Contains("BatchMode=yes", startInfo.ArgumentList);
        Assert.Contains("-T", startInfo.ArgumentList);
        Assert.DoesNotContain("device-auth", startInfo.ArgumentList);
        Assert.Equal("bash", startInfo.ArgumentList[^2]);
        Assert.Equal("-s", startInfo.ArgumentList[^1]);
    }

    private sealed class FakeProviderDeviceAuthTransport : IProviderDeviceAuthTransport
    {
        private readonly TaskCompletionSource<ProviderDeviceAuthTransportResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string SshTarget { get; private set; } = "";
        public string ProcessArguments { get; private set; } = "ssh -T";

        public ProviderDeviceAuthTransportSession Start(
            string provider,
            string sshTarget,
            Action<string> onOutput,
            CancellationToken cancellationToken)
        {
            SshTarget = sshTarget;
            onOutput("Open this URL in your browser:");
            onOutput("https://auth.openai.com/codex/device");
            onOutput("Enter this one-time code: ABCD-EFGH");
            cancellationToken.Register(() => _completion.TrySetCanceled(cancellationToken));
            return new ProviderDeviceAuthTransportSession(
                _completion.Task,
                () => _completion.TrySetCanceled());
        }

        public void Complete(ProviderDeviceAuthTransportResult result) => _completion.TrySetResult(result);
    }

    [Fact]
    public async Task ClaudeBrowserAuth_AcceptsAHostOwnedUrlWithoutADeviceCode()
    {
        var transport = new FakeClaudeBrowserAuthTransport();
        var audit = new RecordingProviderSignInAudit();
        var coordinator = new ProviderSignInCoordinator(transport, audit);

        var started = await coordinator.StartAsync(
            "agent-runner-01",
            new ProviderSignInRequest("claude", "agent@runner-01"),
            "operator-7",
            CancellationToken.None);

        Assert.Equal("claude", started.Provider);
        Assert.Equal("https://claude.ai/oauth/authorize", started.VerificationUrl);
        Assert.Null(started.UserCode);
        Assert.Equal("claude", transport.Provider);
    }

    private sealed class FakeClaudeBrowserAuthTransport : IProviderDeviceAuthTransport
    {
        private readonly TaskCompletionSource<ProviderDeviceAuthTransportResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Provider { get; private set; } = "";

        public ProviderDeviceAuthTransportSession Start(
            string provider,
            string sshTarget,
            Action<string> onOutput,
            CancellationToken cancellationToken)
        {
            Provider = provider;
            onOutput("Open https://claude.ai/oauth/authorize in your browser.");
            return new ProviderDeviceAuthTransportSession(
                _completion.Task,
                () => _completion.TrySetCanceled());
        }
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
