extern alias Runner;

using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using AgentStudio.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class RunnerCredentialRenewalEndToEndTests
{
    [Fact]
    public async Task Enrolled_host_proves_three_successors_and_owner_revocation_stops_claims()
    {
        var root = Path.Combine(Path.GetTempPath(), "runner-renewal-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var ct = deadline.Token;
        var clock = new AuthorityClock(new DateTimeOffset(2026, 11, 28, 9, 0, 0, TimeSpan.Zero));
        using var certificate = CreateCertificate(clock);
        var claimResponses = Channel.CreateUnbounded<int>();
        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseTestSettings(new Dictionary<string, string?>
                {
                    ["TaskRepository"] = Path.Combine(root, "workspace"),
                    ["Security:Profile"] = "networked",
                    ["Security:RunnerCredentialLifetimeMinutes"] = "3",
                    ["AllowedHosts"] = "127.0.0.1",
                    ["TaskServer:BaseUrl"] = null,
                });
                builder.UseKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
                builder.ConfigureTestServices(services =>
                {
                    services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock));
                    services.AddSingleton<IStartupFilter>(new ClaimObserver(claimResponses.Writer));
                });
            });
            factory.UseKestrel(0);
            factory.StartServer();
            var server = factory.Services.GetRequiredService<IServer>();
            var address = server.Features.Get<IServerAddressesFeature>()!.Addresses.Single(url => url.StartsWith("https://", StringComparison.Ordinal));
            var fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));
            using var http = new HttpClient(new HttpClientHandler
            {
                UseCookies = false,
                ServerCertificateCustomValidationCallback = (_, remote, _, _) =>
                    remote?.GetCertHashString(HashAlgorithmName.SHA256) == fingerprint,
            }) { BaseAddress = new Uri(address) };
            using (var health = await http.GetAsync("/healthz", ct))
            {
                health.EnsureSuccessStatusCode();
                clock.Set(health.Headers.Date ?? throw new InvalidOperationException("Kestrel did not report its time."));
            }
            var store = factory.Services.GetRequiredService<AccessSecurityStore>();
            var owner = store.Bootstrap(new("renewal.owner", "a strong test password", null));
            var enrollment = store.CreateEnrollment(new("renewal-loop", RunnerScopes.Minimum, null, null, []));
            var hostFiles = Path.Combine(root, "host");
            var exit = await Runner::AgentRunner.RunnerEnrollmentCommand.RunAsync(address, enrollment.Code,
                TextWriter.Null, TextWriter.Null, ct, http, hostFiles);
            Assert.Equal(0, exit);
            var identity = Assert.Single(store.ListRunners());
            var original = Assert.Single(identity.Credentials);
            Assert.Equal(TimeSpan.FromMinutes(3), original.ExpiresAt - original.CreatedAt);
            var tokenPath = Path.Combine(hostFiles, "runner.token");

            using var daemon = StartAgent(root, address, fingerprint, identity.Id, tokenPath);
            var stdout = daemon.StandardOutput.ReadToEndAsync();
            var stderr = daemon.StandardError.ReadToEndAsync();
            try
            {
                await WaitForClaimAsync(claimResponses.Reader, 200, daemon, ct);
                for (var generation = 1; generation <= 3; generation++)
                {
                    clock.Advance(TimeSpan.FromMinutes(2));
                    do { await WaitForClaimAsync(claimResponses.Reader, 200, daemon, ct); }
                    while (store.ListRunners().Single().Credentials.Count(item => item.RenewalProvenAt is not null) < generation);
                    var credentials = store.ListRunners().Single().Credentials;
                    Assert.Equal(generation + 1, credentials.Count);
                    Assert.All(credentials.Take(generation), retired => Assert.NotNull(retired.SupersededAt));
                    Assert.Equal(clock.GetUtcNow().UtcDateTime, credentials[^1].RenewalProvenAt);
                    var installed = File.ReadAllText(tokenPath).Trim();
                    Assert.True(AgentStudio.TaskServer.Contracts.RunnerCredentialRenewalProtocol.TryReadCredentialId(installed, out var id));
                    Assert.Equal(credentials[^1].Id, id);
                }

                using var revoke = new HttpRequestMessage(HttpMethod.Delete, $"/api/auth/runners/{identity.Id}");
                revoke.Headers.Add("Cookie", $"{AccessSecurityStore.SessionCookieName}={owner.SessionToken}");
                revoke.Headers.Add("Origin", address);
                revoke.Headers.Add("Sec-Fetch-Site", "same-origin");
                revoke.Headers.Add("X-CSRF-Token", owner.CsrfToken);
                using var revoked = await http.SendAsync(revoke, ct);
                Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
                await WaitForClaimAsync(claimResponses.Reader, 401, daemon, ct);
                Assert.Equal(4, store.ListRunners().Single().Credentials.Count);
            }
            catch (Exception error) when (error is OperationCanceledException or InvalidOperationException)
            {
                await StopAgentAsync(daemon);
                throw new InvalidOperationException(
                    $"Host did not complete the renewal exercise.\n{await stdout}\n{await stderr}", error);
            }
            finally
            {
                await StopAgentAsync(daemon);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Process StartAgent(string root, string address, string fingerprint, string runnerId, string tokenFile)
    {
        var runnerAssembly = typeof(Runner::AgentRunner.RunnerOptions).Assembly.Location;
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("RUNNER_", StringComparison.Ordinal)).ToArray())
            start.Environment.Remove(key);
        start.Environment["RUNNER_AUTH_TOKEN_FILE"] = tokenFile;
        start.Environment["RUNNER_TLS_CERTIFICATE_SHA256"] = fingerprint;
        start.Environment["RUNNER_CLAUDE_CLI_BIN"] = Path.Combine(root, "missing-claude");
        start.Environment["RUNNER_CODEX_CLI_BIN"] = Path.Combine(root, "missing-codex");
        start.Environment["RUNNER_SALVAGE_DIR"] = Path.Combine(root, "salvage");
        start.Environment["RUNNER_SALVAGE_RETENTION"] = "off";
        start.Environment["RUNNER_CLAIM_MAX_LOAD_PER_CORE"] = "1000";
        foreach (var argument in new[]
        {
            runnerAssembly, "--poll", "--server", address, "--runner-id", runnerId,
            "--runner-name", "renewal-loop", "--state-dir", Path.Combine(root, "state"),
            "--workdir", Path.Combine(root, "work"), "--poll-seconds", "1",
        })
            start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("The test host did not start.");
    }

    private static async Task StopAgentAsync(Process daemon)
    {
        if (daemon.HasExited) return;
        daemon.Kill(entireProcessTree: true);
        await daemon.WaitForExitAsync();
    }

    private static async Task WaitForClaimAsync(ChannelReader<int> claims, int status, Process daemon, CancellationToken ct)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var exit = daemon.WaitForExitAsync(wait.Token);
        try
        {
            while (true)
            {
                var claim = claims.ReadAsync(wait.Token).AsTask();
                if (await Task.WhenAny(claim, exit) == exit && !claim.IsCompletedSuccessfully)
                {
                    await exit;
                    throw new InvalidOperationException($"Host exited with code {daemon.ExitCode} before claim HTTP {status}.");
                }
                if (await claim == status) return;
            }
        }
        finally { await wait.CancelAsync(); }
    }

    private static X509Certificate2 CreateCertificate(TimeProvider clock)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        return request.CreateSelfSigned(clock.GetUtcNow().AddYears(-25), clock.GetUtcNow().AddYears(30));
    }

    // Calibrate against the same system clock the separate runner process uses,
    // then move only the authority clock to each renewal boundary.
    private sealed class AuthorityClock(DateTimeOffset start) : TimeProvider
    {
        private long _ticks = start.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Set(DateTimeOffset value) => Interlocked.Exchange(ref _ticks, value.UtcTicks);
        public void Advance(TimeSpan time) => Interlocked.Add(ref _ticks, time.Ticks);
    }

    private sealed class ClaimObserver(ChannelWriter<int> responses) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, proceed) =>
            {
                var path = context.Request.Path.Value;
                await proceed(context);
                if (HttpMethods.IsPost(context.Request.Method) && (path == "/api/runner/claim"
                    || path?.StartsWith("/api/v1/runners/", StringComparison.Ordinal) == true && path.EndsWith("/claims", StringComparison.Ordinal)))
                    responses.TryWrite(context.Response.StatusCode);
            });
            next(app);
        };
    }
}
