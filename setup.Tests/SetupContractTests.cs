using AgentStudio.Setup;
using AgentStudio.TaskServer.Contracts;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace AgentOrchestratorSetup.Tests;

public sealed class SetupContractTests
{
    [Fact]
    public async Task Single_use_join_token_exchanges_a_runner_enrolment_without_a_management_bearer()
    {
        var enrolmentCode = "enr_" + new string('a', 64);
        var runnerCredential = "ats_" + new string('b', 64);
        HttpRequestMessage? captured = null;
        var handler = new StubHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonContent.Create(new ExchangedEnrolment("ins_demo",
                    new IssuedPrincipalCredential(new PrincipalDto("runner:runner-one",
                        TaskServerPrincipalKinds.Runner, [TaskServerScopes.RunsWrite],
                        "runner-one", DateTime.UnixEpoch, null, null),
                        runnerCredential, DateTime.UnixEpoch))),
            };
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://tasks.example.test") };
        var payload = new JoinPayload(2, "https://tasks.example.test", "", "0.4.0",
            DateTime.UnixEpoch, enrolmentCode, "ins_demo", "runner-one");

        Assert.Equal(payload, JoinTokenCodec.Decode(JoinTokenCodec.Encode(payload)));
        Assert.Equal(runnerCredential,
            await SetupApplication.MintRunnerCredentialAsync(payload, "runner-one", default, client));
        Assert.Equal("/api/v1/enrolments/exchange", captured!.RequestUri!.AbsolutePath);
        Assert.Null(captured.Headers.Authorization);
    }

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
    public async Task Agent_host_rejoin_rotates_under_the_contract_and_retires_the_previous_credential()
    {
        var joinCredential = new string('j', 64);
        var runnerCredential = "ats_0011223344556677." + new string('r', 64);
        var issuedAt = new DateTime(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc);
        var requests = new List<(HttpMethod Method, string Path, string? Bearer, string? Consumer, string Body)>();
        var handler = new StubHandler(request =>
        {
            requests.Add((request.Method, request.RequestUri!.AbsolutePath,
                request.Headers.Authorization?.Parameter,
                request.Headers.TryGetValues("X-Principal-Consumer-Id", out var consumer) ? consumer.Single() : null,
                request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty));
            if (request.RequestUri.AbsolutePath == "/api/v1/management/principals")
                return new HttpResponseMessage(HttpStatusCode.Conflict);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new IssuedPrincipalCredential(
                    new PrincipalDto("runner:runner-one", TaskServerPrincipalKinds.Runner,
                        [TaskServerScopes.TasksRead], "runner-one", issuedAt, null, null),
                    runnerCredential,
                    issuedAt)),
            };
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://tasks.example.test") };
        var payload = new JoinPayload(1, "https://tasks.example.test", joinCredential, "0.4.0", issuedAt);

        var actual = await SetupApplication.MintRunnerCredentialAsync(payload, "runner-one", default, client);

        Assert.Equal(runnerCredential, actual);
        var rotate = requests[1];
        Assert.Equal("/api/v1/management/principals/runner%3Arunner-one/rotate", rotate.Path);
        Assert.Equal(joinCredential, rotate.Bearer);
        var body = JsonSerializer.Deserialize<RotatePrincipalRequest>(rotate.Body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            })!;
        Assert.Matches("^[A-Za-z0-9._:-]{1,128}$", body.OperationId!);
        Assert.Equal(new PrincipalRotationConsumer("runner-one", TaskServerScopes.TasksRead),
            Assert.Single(body.Consumers!));
        var operation = body.OperationId;
        Assert.Equal(
            [
                (HttpMethod.Post, $"/api/v1/principal-rotations/{operation}/delivered"),
                (HttpMethod.Get, "/api/v1/workspaces"),
                (HttpMethod.Post, $"/api/v1/principal-rotations/{operation}/ack"),
            ],
            requests.Skip(2).Select(request => (request.Method, request.Path)));
        Assert.All(requests.Skip(2), request =>
        {
            Assert.Equal(runnerCredential, request.Bearer);
            Assert.Equal("runner-one", request.Consumer);
        });
        Assert.Equal("runner-one", JsonSerializer.Deserialize<PrincipalRotationAcknowledgement>(
            requests[^1].Body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.ConsumerId);
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
        // The parser normalises the path with Path.GetFullPath, so the expected value
        // must be normalised the same way: on Windows "/secure/join.token" becomes
        // "C:\secure\join.token" (17.09.: the Windows pre-develop gate ran setup.Tests
        // for the first time and was red on this line).
        Assert.Equal(Path.GetFullPath("/secure/join.token"), options.JoinTokenFile);
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
                "codex",
                "/usr/bin/claude",
                "/usr/bin/codex",
                null,
                null,
                4),
            "/etc/agent-runner/runner.token",
            "/var/lib/agent-runner/work",
            "/var/lib/agent-runner/state");

        Assert.Contains("RUNNER_CLI_TYPE=codex", environment, StringComparison.Ordinal);
        Assert.Contains("RUNNER_CLAUDE_CLI_BIN=/usr/bin/claude", environment, StringComparison.Ordinal);
        Assert.Contains("RUNNER_CODEX_CLI_BIN=/usr/bin/codex", environment, StringComparison.Ordinal);
        // AGT-2373: the runner refuses to start when these removed settings are present.
        Assert.DoesNotContain("RUNNER_CLI_BIN=", environment, StringComparison.Ordinal);
        Assert.DoesNotContain("RUNNER_CLI_ARGS=", environment, StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewAgentHost_UsesAReviewOnlyManualStopGuardDropIn()
    {
        var guard = NativeInstaller.BuildReviewRestartGuard().ReplaceLineEndings("\n");

        Assert.Equal("20-agent-runner-review-restart-guard.conf", NativeInstaller.ReviewRestartGuardFileName);
        Assert.Contains("[Unit]\n", guard, StringComparison.Ordinal);
        Assert.Contains("RefuseManualStop=true\n", guard, StringComparison.Ordinal);
        Assert.Contains("[Service]\n", guard, StringComparison.Ordinal);
        Assert.Contains("Restart=on-failure\n", guard, StringComparison.Ordinal);
        Assert.DoesNotContain("KillMode", guard, StringComparison.Ordinal);
        Assert.DoesNotContain("PrivateTmp", guard, StringComparison.Ordinal);
        Assert.Equal(
            Path.Combine(
                "/etc/systemd/system",
                "agent-host-review.service.d",
                NativeInstaller.ReviewRestartGuardFileName),
            NativeInstaller.ResolveReviewRestartGuardPath(
                "review",
                "/etc/systemd/system",
                "agent-host-review.service"));
        Assert.Null(NativeInstaller.ResolveReviewRestartGuardPath(
            "coding",
            "/etc/systemd/system",
            "agent-host.service"));
        Assert.Equal(
            [
                "--restart-guard",
                "--hold-admission",
                "--role",
                "review",
                "--state-dir",
                "/var/lib/agent-runner/review-state",
            ],
            NativeInstaller.BuildReviewRestartGuardArguments(
                "/var/lib/agent-runner/review-state"));
    }

    [Theory]
    [InlineData("https://github.com/agent-orc/agent-studio.git")]
    [InlineData("ssh://git@github.com/agent-orc/agent-studio.git")]
    [InlineData("git@github.com:agent-orc/agent-studio.git")]
    [InlineData("git://127.0.0.1:9418/scenario.git")]
    public void GitRemote_AcceptsCredentialFreeSupportedForms(string remote)
    {
        Assert.Equal(remote, SetupValidation.RequireGitRemote(remote));
    }

    [Fact]
    public void GitRemote_RejectsNonLoopbackGitProtocol()
    {
        Assert.Throws<ArgumentException>(() => SetupValidation.RequireGitRemote("git://example.org/scenario.git"));
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
