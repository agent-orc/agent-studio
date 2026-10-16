using System.Net;
using System.Net.Http.Json;
using AgentStudio.Setup;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentOrchestratorSetup.Tests;

public sealed class JoinTokenIssuerTests
{
    private static readonly DateTime IssuedAt = new(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("reverse-tunnel", "http://127.0.0.1:15031", true)]
    [InlineData("lan", "https://192.168.1.10:5071", true)]
    [InlineData("https", "https://tasks.example.test", true)]
    [InlineData("lan", "http://192.168.1.10:5071", false)]
    [InlineData("reverse-tunnel", "https://tasks.example.test", false)]
    public async Task Route_choice_controls_token_url_and_rejects_insecure_paths(
        string route, string url, bool valid)
    {
        using var temp = new TestFiles();
        var request = temp.Request(route, url);
        using var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK));
        if (!valid)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => JoinTokenIssuer.IssueAsync(request, client));
            Assert.False(File.Exists(request.OutputFile));
        }
        else
        {
            using var protocol = Client(message => message.RequestUri!.AbsolutePath switch
            {
                "/healthz" => new HttpResponseMessage(HttpStatusCode.OK),
                "/api/v1/management/enrolments" => new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = JsonContent.Create(Enrollment()),
                },
                _ => throw new InvalidOperationException("Unexpected request."),
            });
            var payload = await JoinTokenIssuer.IssueAsync(request, protocol, new FixedClock());
            Assert.Equal(url, payload.ServerUrl);
            Assert.Equal(2, JoinTokenCodec.Decode(await File.ReadAllTextAsync(request.OutputFile)).SchemaVersion);
            Assert.True(SetupSecrets.IsProtected(request.OutputFile));
        }
    }

    [Fact]
    public async Task Windows_issuer_logs_in_as_owner_and_writes_only_the_one_time_code()
    {
        using var temp = new TestFiles();
        var request = temp.Request("reverse-tunnel", "http://127.0.0.1:15031") with
        {
            OwnerSessionFile = null,
            OwnerName = "owner",
            OwnerPassword = new string('p', 40),
        };
        var calls = new List<string>();
        using var client = Client(message =>
        {
            calls.Add(message.RequestUri!.AbsolutePath);
            if (calls.Count > 1)
            {
                Assert.Equal("Bearer", message.Headers.Authorization?.Scheme);
                Assert.Equal(new string('m', 64), message.Headers.Authorization?.Parameter);
            }
            if (calls.Count == 3)
            {
                Assert.Equal("owner-session", message.Headers.GetValues("X-Studio-Session-Token").Single());
                Assert.NotNull(message.Headers.GetValues(TaskServerProtocol.HeaderName).Single());
            }
            return message.RequestUri.AbsolutePath switch
            {
                "/healthz" => new HttpResponseMessage(HttpStatusCode.OK),
                "/api/v1/studio/auth/login" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new StudioAuthSessionDto("owner-session", "csrf",
                        new StudioAuthStatusDto(false, true))),
                },
                "/api/v1/management/enrolments" => new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = JsonContent.Create(Enrollment()),
                },
                _ => throw new InvalidOperationException("Unexpected request."),
            };
        });

        var issued = await JoinTokenIssuer.IssueAsync(request, client, new FixedClock());

        Assert.Equal(["/healthz", "/api/v1/studio/auth/login", "/api/v1/management/enrolments"], calls);
        Assert.Equal("host-a", issued.RunnerId);
        var contents = await File.ReadAllTextAsync(request.OutputFile);
        Assert.DoesNotContain(new string('m', 64), contents);
        Assert.DoesNotContain("owner-session", contents);
        Assert.DoesNotContain(request.OwnerPassword!, contents);
        Assert.Throws<IOException>(() => JoinTokenIssuer.IssueAsync(request, client).GetAwaiter().GetResult());
    }

    [Fact]
    public async Task Linux_exchange_uses_token_once_and_keeps_service_credential_separate()
    {
        var enrollment = Enrollment();
        var payload = new JoinPayload(2, "https://tasks.example.test", enrollment.EnrolmentCode,
            "1.2.0", IssuedAt, enrollment.InstallationId, enrollment.RunnerId, enrollment.ExpiresAt);
        var issued = new IssuedPrincipalCredential(new PrincipalDto("runner:host-a",
            TaskServerPrincipalKinds.Runner, [TaskServerScopes.RunsClaim], "host-a", IssuedAt, null, null),
            new string('r', 64), IssuedAt);
        var calls = 0;
        using var client = Client(message =>
        {
            Assert.Equal("/api/v1/enrolments/exchange", message.RequestUri!.AbsolutePath);
            Assert.Null(message.Headers.Authorization);
            calls++;
            return new HttpResponseMessage(calls == 1 ? HttpStatusCode.Created : HttpStatusCode.Unauthorized)
            {
                Content = calls == 1 ? JsonContent.Create(new ExchangedEnrolment(enrollment.InstallationId, issued)) : null,
            };
        });

        var credential = await SetupApplication.MintRunnerCredentialAsync(payload, "host-a", default, client,
            new FixedClock());
        Assert.Equal(issued.Credential, credential);
        Assert.NotEqual(payload.Credential, credential);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SetupApplication.MintRunnerCredentialAsync(payload, "host-a", default, client, new FixedClock()));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Expired_join_token_is_rejected_using_the_injected_clock()
    {
        var enrollment = Enrollment();
        var payload = new JoinPayload(2, "https://tasks.example.test", enrollment.EnrolmentCode,
            "1.2.0", IssuedAt, enrollment.InstallationId, enrollment.RunnerId, enrollment.ExpiresAt);
        using var client = Client(_ => throw new InvalidOperationException("Expired token reached the server."));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SetupApplication.MintRunnerCredentialAsync(payload, "host-a", default, client,
                new FixedClock(enrollment.ExpiresAt)));
        Assert.Contains("Join token expired", error.Message);
    }

    [Fact]
    public async Task Retry_after_service_failure_reuses_host_credential_without_exchanging_again()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var temp = new TestFiles();
        var enrollment = Enrollment();
        var payload = new JoinPayload(2, "https://tasks.example.test", enrollment.EnrolmentCode,
            "1.2.0", IssuedAt, enrollment.InstallationId, enrollment.RunnerId, enrollment.ExpiresAt);
        var issued = new IssuedPrincipalCredential(new PrincipalDto("runner:host-a",
            TaskServerPrincipalKinds.Runner, [TaskServerScopes.RunsClaim], "host-a", IssuedAt, null, null),
            new string('r', 64), IssuedAt);
        var exchanges = 0;
        using var client = Client(_ =>
        {
            exchanges++;
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonContent.Create(new ExchangedEnrolment(enrollment.InstallationId, issued)),
            };
        });
        var config = Path.GetDirectoryName(temp.Request("https", payload.ServerUrl).OutputFile)!;

        var first = await SetupApplication.GetOrMintJoinCredentialAsync(payload, "host-a", "coding", config,
            default, client, new FixedClock());
        // A later systemd failure leaves the protected credential available to a retry.
        var retry = await SetupApplication.GetOrMintJoinCredentialAsync(payload, "host-a", "coding", config,
            default, client, new FixedClock());

        Assert.Equal(issued.Credential, first);
        Assert.Equal(first, retry);
        Assert.Equal(1, exchanges);
        Assert.True(SetupSecrets.IsProtected(Path.Combine(config, "runner.join")));
        Assert.DoesNotContain(payload.Credential, await File.ReadAllTextAsync(Path.Combine(config, "runner.token")));
    }

    [Fact]
    public void Reverse_tunnel_uses_a_distinct_keeper_task_for_each_host()
    {
        Assert.Equal("AgentRunner-TunnelKeeper-host-a", JoinTokenIssuer.TunnelTaskName("host-a"));
        Assert.Equal("AgentRunner-TunnelKeeper-host-b", JoinTokenIssuer.TunnelTaskName("host-b"));
        Assert.Throws<ArgumentException>(() => JoinTokenIssuer.TunnelTaskName("host a"));
    }

    [Fact]
    public void Host_join_rejects_legacy_management_bearer_tokens()
    {
        using var temp = new TestFiles();
        var path = temp.Request("reverse-tunnel", "http://127.0.0.1:15031").OutputFile;
        File.WriteAllText(path, JoinTokenCodec.Encode(new JoinPayload(1,
            "https://tasks.example.test", new string('m', 64), "1.2.0", IssuedAt)));
        Assert.Contains("single-use", Assert.Throws<ArgumentException>(() =>
            ProductSetup.ReadJoinToken(path)).Message);
    }

    private static IssuedEnrolment Enrollment()
        => new("enl_example", "runner:host-a", TaskServerPrincipalKinds.Runner,
            "host-a", "installation-one", "enr_" + new string('e', 64), IssuedAt.AddMinutes(10));

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> reply)
        => new(new Handler(reply)) { BaseAddress = new Uri("http://127.0.0.1:5071") };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(reply(request));
    }

    private sealed class FixedClock(DateTime? now = null) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now ?? IssuedAt);
    }

    private sealed class TestFiles : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory().FullName;

        public JoinTokenIssuer.Request Request(string route, string url)
        {
            var management = Path.Combine(_root, "management.token");
            var owner = Path.Combine(_root, "owner.session");
            File.WriteAllText(management, new string('m', 64));
            File.WriteAllText(owner, "owner-session");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(management, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.SetUnixFileMode(owner, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            return new JoinTokenIssuer.Request("http://127.0.0.1:5071", url, route, "host-a",
                "1.2.0", management, owner, Path.Combine(_root, "join.token"));
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
