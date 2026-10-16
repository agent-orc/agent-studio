extern alias Runner;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using AgentStudio.Security;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

using RClient = Runner::AgentRunner.TaskServerClient;
using ROptions = Runner::AgentRunner.RunnerOptions;
using RLogRequest = Runner::AgentRunner.LogIngestRequest;
using RLogLine = Runner::AgentRunner.CliOutputLine;

namespace AgentStudio.Tests;

/// <summary>
/// Genuine TLS transport evidence for the networked security profile. Unlike
/// <see cref="NetworkedSecurityEndpointTests"/> — which drives the real endpoints
/// through the in-memory <c>WebApplicationFactory</c> / <c>TestServer</c> and never
/// performs a TLS handshake — this test binds real Kestrel loopback listeners
/// (one HTTPS with an in-test self-signed certificate, one cleartext HTTP) and
/// exchanges bytes over an actual socket. The self-signed certificate is the
/// evidence: no production certificate is needed, and the assertions prove the
/// contract the review flagged as unexercised:
/// <list type="bullet">
///   <item><description>a real TLS handshake completes against the self-signed cert;</description></item>
///   <item><description>anonymous application reads fail closed over TLS (401);</description></item>
///   <item><description>a cleartext request to the same server is rejected (426 upgrade-required);</description></item>
///   <item><description>the real Runner <c>TaskServerClient</c> connects outbound over HTTPS and
///     authenticates with its service credential;</description></item>
///   <item><description>the same client without a credential, and a credential missing the
///     route scope, both fail closed (401 / 403) over TLS.</description></item>
/// </list>
/// </summary>
// MachineBound 20.07.: bindet echte Loopback-TLS-Sockets (HTTPS + Cleartext) und
// fährt einen echten TLS-Handshake gegen ein Self-Signed-Testzertifikat. Kein
// Timing-/Lastfenster, aber sockettbindungs-/umgebungsabhängig — daher aus dem
// parallelen Karten-Gate gehalten und als deterministische TLS-Evidenz gezielt
// ausgeführt.
[Trait("Category", "MachineBound")]
public sealed class RealTlsTransportTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "studio-real-tls-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Runner_connects_over_real_tls_with_service_credential_and_cleartext_is_rejected()
    {
        using var cert = CreateSelfSignedTestCertificate();
        await using var host = await StartHttpsHostAsync(cert);

        // Bootstrap the owner, mint a one-time enrollment, and enroll a Runner so
        // we have a real service credential to present over the wire.
        var store = host.Store;
        store.Bootstrap(new BootstrapRequest("first.owner", "correct horse battery staple!", "First Owner"));
        var enrollment = store.CreateEnrollment(new RunnerEnrollmentRequest("tls-runner-01", null, null, null));
        var enrolled = store.EnrollRunner(enrollment.Code);

        var tlsHandshakeSeen = false;
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, presented, _, _) =>
            {
                if (presented is not null && string.Equals(presented.Thumbprint, cert.Thumbprint, StringComparison.OrdinalIgnoreCase))
                {
                    tlsHandshakeSeen = true;
                    return true;
                }
                return false;
            }
        };

        // 1) A real TLS handshake to /healthz over HTTPS succeeds.
        using (var tls = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(host.HttpsBase) })
        {
            var health = await tls.GetAsync("/healthz");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            Assert.Equal("ok", (await health.Content.ReadAsStringAsync()).Trim());
            Assert.True(tlsHandshakeSeen, "The custom certificate validation callback never fired: no real TLS handshake happened.");

            // 2) Anonymous application reads fail closed over TLS.
            var anonymous = await tls.GetAsync("/api/tasks");
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        // 3) A cleartext request to the same server is rejected with 426.
        using (var clear = new HttpClient { BaseAddress = new Uri(host.HttpBase) })
        {
            var cleartext = await clear.GetAsync("/api/tasks");
            Assert.Equal((HttpStatusCode)426, cleartext.StatusCode);
        }

        // 4) The real Runner client connects outbound over HTTPS and authenticates
        //    with its service credential — the acceptance evidence for
        //    "a real Runner connects outbound over HTTPS with its service credential".
        using (var runnerHttp = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(host.HttpsBase) })
        {
            using var runner = new RClient(runnerHttp, enrolled.Runner.Id, authToken: enrolled.Secret);
            var response = await runner.IngestLogsAsync(
                new RLogRequest("AGT-TLS-1", [new RLogLine(DateTime.UtcNow, "stdout", "hello over real tls")], RunnerId: enrolled.Runner.Id),
                CancellationToken.None);
            Assert.NotNull(response);
            Assert.Equal("AGT-TLS-1", response!.TaskKey);
            Assert.Equal(1, response.Appended);
        }

        // 5a) The same client without a credential fails closed over TLS.
        using (var anonHttp = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(host.HttpsBase) })
        {
            using var anonymousRunner = new RClient(anonHttp, enrolled.Runner.Id);
            var ex = await Assert.ThrowsAsync<Runner::AgentRunner.TaskServerException>(() =>
                anonymousRunner.IngestLogsAsync(
                    new RLogRequest("AGT-TLS-2", [new RLogLine(DateTime.UtcNow, "stdout", "no credential")], RunnerId: enrolled.Runner.Id),
                    CancellationToken.None));
            Assert.Equal(401, ex.StatusCode);
        }

        // 5b) A credential that lacks the route scope is denied over TLS (403).
        var claimOnly = store.EnrollRunner(
            store.CreateEnrollment(new RunnerEnrollmentRequest("tls-claim-only", [RunnerScopes.Claim], null, null)).Code);
        using (var scopedHttp = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(host.HttpsBase) })
        {
            scopedHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", claimOnly.Secret);
            var denied = await scopedHttp.PostAsJsonAsync("/api/runner/logs", new { taskKey = "AGT-TLS-3", lines = Array.Empty<object>() });
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        // A private installation may pin the presented certificate. The
        // production client accepts the exact fingerprint and rejects a change.
        var pin = Convert.ToHexString(SHA256.HashData(cert.RawData));
        ROptions Options(string fingerprint) => new()
        {
            ServerUrl = host.HttpsBase,
            RunnerId = enrolled.Runner.Id,
            RunnerName = enrolled.Runner.Name,
            Hostname = "tls-test",
            BackendName = "test",
            WorkDir = _workspace,
            BaseBranch = "main",
            AuthToken = enrolled.Secret,
            TlsServerCertificateSha256 = fingerprint,
        };
        using (var pinned = new RClient(Options(pin)))
        {
            var response = await pinned.IngestLogsAsync(
                new RLogRequest("AGT-TLS-PIN", [new RLogLine(DateTime.UnixEpoch, "stdout", "pinned")], RunnerId: enrolled.Runner.Id),
                CancellationToken.None);
            Assert.Equal(1, response!.Appended);
        }
        using (var wrongPin = new RClient(Options(new string('0', pin.Length))))
            await Assert.ThrowsAsync<HttpRequestException>(() => wrongPin.IngestLogsAsync(
                new RLogRequest("AGT-TLS-WRONG-PIN", [], RunnerId: enrolled.Runner.Id), CancellationToken.None));
    }

    private async Task<RunningHost> StartHttpsHostAsync(X509Certificate2 cert)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:Profile"] = "networked",
            ["TaskRepository"] = _workspace,
        });
        builder.Logging.ClearProviders();
        // Explicit loopback listeners only; suppress the default localhost:5000/5001
        // binding so the test never contends with a running backend.
        builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, string.Empty);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(cert));
            options.Listen(IPAddress.Loopback, 0);
        });

        var app = builder.Build();

        // The networked authentication/authorization boundary, constructed directly
        // (its optional scanner/lease/project dependencies are not needed for a
        // transport-level test) so it runs in front of the terminal endpoints.
        var store = new AccessSecurityStore(app.Configuration, NullLogger<AccessSecurityStore>.Instance, TimeProvider.System);
        app.Use(next =>
        {
            var middleware = new AccessSecurityMiddleware(next, app.Configuration, store);
            return context => middleware.InvokeAsync(context);
        });

        app.MapGet("/healthz", () => Results.Text("ok"));
        app.MapGet("/api/tasks", () => Results.Ok(new[] { "task" }));
        app.MapPost("/api/runner/logs", (AgentStudio.Shared.LogIngestRequest req) =>
            Results.Ok(new AgentStudio.Shared.LogIngestResponse(req.TaskKey, req.Lines?.Count ?? 0)));

        await app.StartAsync();

        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses;
        var https = addresses.First(a => a.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        var http = addresses.First(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
        return new RunningHost(app, store, https, http);
    }

    private static X509Certificate2 CreateSelfSignedTestCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=agent-taskboard-tls-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], false)); // serverAuth

        using var ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        // Round-trip through PKCS#12 so Kestrel receives an exportable private key
        // on every OS (Windows Schannel needs a persisted key handle).
        return X509CertificateLoader.LoadPkcs12(
            ephemeral.Export(X509ContentType.Pfx, "tls-test"), "tls-test", X509KeyStorageFlags.Exportable);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, true); } catch { }
    }

    private sealed class RunningHost(WebApplication app, AccessSecurityStore store, string httpsBase, string httpBase) : IAsyncDisposable
    {
        public AccessSecurityStore Store { get; } = store;
        public string HttpsBase { get; } = httpsBase;
        public string HttpBase { get; } = httpBase;

        public async ValueTask DisposeAsync()
        {
            try { await app.StopAsync(TimeSpan.FromSeconds(5)); } catch { /* best-effort teardown */ }
            await app.DisposeAsync();
        }
    }
}
