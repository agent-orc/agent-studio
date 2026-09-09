using AgentStudio.Setup;
using AgentStudio.TaskServer.Contracts;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace AgentOrchestratorSetup.Tests;

public sealed class SetupContractTests
{
    [Fact]
    public async Task Agent_host_join_mints_a_bound_runner_credential()
    {
        var joinCredential = new string('j', 64);
        var runnerCredential = "ats_0011223344556677." + new string('r', 64);
        HttpRequestMessage? captured = null;
        var handler = new StubHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonContent.Create(new IssuedPrincipalCredential(
                    new PrincipalDto(
                        "runner:runner-one",
                        TaskServerPrincipalKinds.Runner,
                        [TaskServerScopes.RunsClaim],
                        "runner-one",
                        DateTime.UtcNow,
                        null,
                        null),
                    runnerCredential,
                    DateTime.UtcNow)),
            };
        });
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://tasks.example.test"),
        };
        var payload = new JoinPayload(
            1,
            "https://tasks.example.test",
            joinCredential,
            "0.4.0",
            DateTime.UtcNow);

        var actual = await SetupApplication.MintRunnerCredentialAsync(
            payload,
            "runner-one",
            default,
            client);

        Assert.Equal(runnerCredential, actual);
        Assert.NotEqual(joinCredential, actual);
        Assert.Equal("Bearer", captured!.Headers.Authorization!.Scheme);
        Assert.Equal(joinCredential, captured.Headers.Authorization.Parameter);
        Assert.Contains(
            "\"runnerId\":\"runner-one\"",
            await captured.Content!.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void JoinToken_RoundTripsAndDetectsCopyDamage()
    {
        var payload = new JoinPayload(
            1,
            "https://tasks.example.test",
            new string('a', 64),
            "0.4.0",
            new DateTime(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc));

        var token = JoinTokenCodec.Encode(payload);
        var decoded = JoinTokenCodec.Decode(token);

        Assert.Equal(payload, decoded);
        Assert.StartsWith("aosj1.", token);
        var damaged = token[..^1] + (token[^1] == 'a' ? "b" : "a");
        Assert.Contains(
            "checksum",
            Assert.Throws<ArgumentException>(() => JoinTokenCodec.Decode(damaged)).Message,
            StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> response)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(response(request));
    }

    [Fact]
    public void JoinToken_RejectsInsecureNonLoopbackServer()
    {
        var payload = new JoinPayload(
            1,
            "http://tasks.example.test:5071",
            new string('b', 64),
            "0.4.0",
            DateTime.UtcNow);

        Assert.Contains(
            "HTTPS",
            Assert.Throws<ArgumentException>(() => JoinTokenCodec.Encode(payload)).Message);
    }

    [Fact]
    public void Arguments_NeverAcceptJoinSecretDirectly()
    {
        var options = SetupOptions.Parse(
        [
            "--join",
            "--join-token-file",
            "/secure/join.token",
            "--runner-name",
            "agent-runner-01",
        ]);

        Assert.Equal(SetupMode.AgentHost, options.Mode);
        Assert.Equal("/secure/join.token", options.JoinTokenFile);
        Assert.Throws<ArgumentException>(() =>
            SetupOptions.Parse(["--join-token", "aosj1.secret"]));
    }

    [Fact]
    public void DemoCompose_IsPinnedAndMountsNoHostRepository()
    {
        var compose = DemoInstaller.BuildCompose(
            "0.4.0",
            4011,
            "ghcr.io/agent-orc");

        Assert.Contains("agent-studio-api:v0.4.0", compose);
        Assert.Contains("agent-studio-web:v0.4.0", compose);
        Assert.Contains("\"127.0.0.1:4011:80\"", compose);
        Assert.Contains("demo-workspace:/data/workspace", compose);
        Assert.DoesNotContain("./", compose);
        Assert.DoesNotContain("/home/", compose);
        Assert.DoesNotContain("agent-host", compose);
    }

    [Fact]
    public void ReleaseChecksum_RequiresExactAssetName()
    {
        var hash = new string('c', 64);
        var sums = $"{hash}  agent-host-0.4.0.tar.gz\n";

        Assert.Equal(
            hash,
            ReleaseArtifacts.ParseExpectedHash(
                sums,
                "agent-host-0.4.0.tar.gz"));
        Assert.Throws<InvalidDataException>(() =>
            ReleaseArtifacts.ParseExpectedHash(sums, "agent-host-0.4.1.tar.gz"));
    }

    [Theory]
    [InlineData("ready", "ready", "ready")]
    [InlineData("ready", "ready-no-workflow-scope", "ready-no-workflow-scope")]
    [InlineData("unavailable", "unavailable", "read-only")]
    public void GitStartupStatus_ExplainsAdmissionState(
        string push,
        string workflow,
        string expected)
    {
        Assert.Equal(
            expected,
            NativeInstaller.ClassifyGitStatus(push, workflow));
    }

    [Fact]
    public void AgentHostEnvironment_PreservesBothDiscoveredCardCliPaths()
    {
        var environment = NativeInstaller.BuildRunnerEnvironment(
            new HostConfiguration(
                "https://tasks.example.test",
                "credential",
                "0.4.0",
                "agent-runner-01",
                "agent-runner-01",
                "coding",
                "agent",
                "agent",
                "/home/agent",
                "/usr/bin/codex",
                "/usr/bin/claude",
                "/usr/bin/codex",
                "exec -",
                null,
                null,
                4),
            "/etc/agent-runner/runner.token",
            "/var/lib/agent-runner/work",
            "/var/lib/agent-runner/state");

        Assert.Contains("RUNNER_CLI_BIN=/usr/bin/codex", environment, StringComparison.Ordinal);
        Assert.Contains("RUNNER_CLAUDE_CLI_BIN=/usr/bin/claude", environment, StringComparison.Ordinal);
        Assert.Contains("RUNNER_CODEX_CLI_BIN=/usr/bin/codex", environment, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://github.com/agent-orc/agent-studio.git")]
    [InlineData("ssh://git@github.com/agent-orc/agent-studio.git")]
    [InlineData("git@github.com:agent-orc/agent-studio.git")]
    public void GitRemote_AcceptsCredentialFreeSupportedForms(string remote)
    {
        Assert.Equal(remote, SetupValidation.RequireGitRemote(remote));
    }

    [Theory]
    [InlineData("demo", "Demo")]
    [InlineData("single", "SingleMachine")]
    [InlineData("control-plane", "ControlPlane")]
    [InlineData("agent-host", "AgentHost")]
    public void Modes_AreStableCommandLineContracts(
        string value,
        string expected)
    {
        Assert.Equal(expected, SetupOptions.ParseMode(value).ToString());
    }

    [Theory]
    [InlineData("systemd", "Systemd")]
    [InlineData("docker", "Docker")]
    public void Targets_AreStableCommandLineContracts(
        string value,
        string expected)
    {
        Assert.Equal(expected, SetupOptions.ParseTarget(value).ToString());
    }

    [Fact]
    public void Target_DefaultsToSystemd()
    {
        var options = SetupOptions.Parse(["--mode", "control-plane"]);

        Assert.Equal(SetupTarget.Systemd, options.Target);
    }

    [Fact]
    public void Target_DockerRequiresControlPlaneMode()
    {
        Assert.Throws<ArgumentException>(() =>
            SetupOptions.Parse(["--mode", "single", "--target", "docker"]));
    }

    [Fact]
    public void Target_DockerIsAcceptedWithControlPlaneMode()
    {
        var options = SetupOptions.Parse(
            ["--mode", "control-plane", "--target", "docker", "--server-url", "task-server-01.wg.internal"]);

        Assert.Equal(SetupTarget.Docker, options.Target);
        Assert.Equal("task-server-01.wg.internal", options.ServerUrl);
    }
}
