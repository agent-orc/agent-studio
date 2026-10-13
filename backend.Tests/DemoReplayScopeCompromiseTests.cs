using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Compromise coverage for the public-demo replay credential (AGT-W34 slice S3).
/// These tests assume the replay process is fully owned by an attacker: it holds
/// the credential and the signed trace. It must still be unable to claim work,
/// take a lease, report a completion, or move a card, and it must be unable to
/// invent a frame the server accepts.
/// </summary>
public sealed class DemoReplayScopeCompromiseTests : IDisposable
{
    private const string ProjectName = "Demo App";
    private const string TaskKey = "DEMO-1";
    private const string TraceId = "demo-scene-reports-export";

    private readonly string _workspace;
    private readonly string _watchPath;
    private readonly string _taskFolder;
    private readonly ECDsa _signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly DemoReplaySignedTrace _trace;

    public DemoReplayScopeCompromiseTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "agt-demo-replay-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", "demo-app");
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        // Seeded in Ready: a Progress card with no live run is relocated by
        // startup reconciliation, which would move the folder under the test.
        _taskFolder = Path.Combine(_watchPath, TaskStates.Ready, TaskKey);
        Directory.CreateDirectory(_taskFolder);
        File.WriteAllText(
            Path.Combine(_taskFolder, "task.json"),
            JsonSerializer.Serialize(new
            {
                id = TaskKey,
                key = TaskKey,
                title = "Fix large avatar uploads",
                state = TaskStates.Ready,
                order = 1,
                agent = "claude",
                cliType = "claude",
                createdAt = "2026-08-09T08:00:00Z",
            }));
        File.WriteAllText(Path.Combine(_taskFolder, "prompt.md"), "Pinned demo fixture.");

        _trace = DemoReplayTraceSignature.Sign(
            new DemoReplayTrace(
                DemoReplayTraceDigest.CurrentSchemaVersion,
                TraceId,
                "reports-export",
                [TaskKey],
                [
                    new DemoReplayFrame(1, 0, TaskKey, DemoReplayFrameKinds.SessionStarted, "Simulated run opened", Cli: "claude"),
                    new DemoReplayFrame(2, 12, TaskKey, DemoReplayFrameKinds.TurnStarted, "Simulated turn"),
                    new DemoReplayFrame(3, 40, TaskKey, DemoReplayFrameKinds.TurnCompleted, "Simulated turn done", OutputTokens: 1200),
                ]),
            _signingKey,
            "demo-release-2026-08");
    }

    public void Dispose()
    {
        _signingKey.Dispose();
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task A_replay_credential_is_refused_by_every_execution_and_mutation_route()
    {
        using var factory = BuildFactory();
        using var anonymous = NewClient(factory);
        var secret = await MintReplayCredentialAsync(factory, anonymous);

        // Runner-scoped execution routes: the credential authenticates but has
        // no execution scope, so each one is a typed scope denial.
        foreach (var route in new[]
                 {
                     "/api/runner/claim",
                     "/api/runner/lease/acquire",
                     "/api/runner/lease/renew",
                     "/api/runner/lease/release",
                     "/api/runner/logs",
                     "/api/runner/events",
                     "/api/runner/artifacts",
                     "/api/runner/completion",
                 })
        {
            var denial = await PostAsync(anonymous, secret, route, new { taskKey = TaskKey });
            Assert.Equal(HttpStatusCode.Forbidden, denial.StatusCode);
            Assert.Equal("runner-scope-denied", await ErrorCodeAsync(denial));
        }

        // Studio routes are not runner-scoped at all. A service credential is
        // not a session, so lane and lifecycle mutations never even resolve.
        foreach (var route in new[]
                 {
                     $"/api/tasks/{TaskKey}/start",
                     $"/api/tasks/{TaskKey}/continue",
                     $"/api/tasks/{TaskKey}/stop",
                     $"/api/tasks/{TaskKey}/move",
                     "/api/tasks/batch-move",
                 })
        {
            var denial = await PostAsync(anonymous, secret, route, new { state = TaskStates.Completed });
            Assert.Equal(HttpStatusCode.Unauthorized, denial.StatusCode);
            Assert.Equal("authentication-required", await ErrorCodeAsync(denial));
        }

        // It cannot read the board either, so it cannot even enumerate targets.
        using var read = new HttpRequestMessage(HttpMethod.Get, "/api/tasks");
        read.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(read)).StatusCode);

        // The task folder is untouched: no lease, no lane change, no journal.
        Assert.False(File.Exists(Path.Combine(_taskFolder, "logs", "runner-events.jsonl")));
        Assert.True(Directory.Exists(_taskFolder));
    }

    [Fact]
    public async Task A_sealed_frame_lands_as_a_simulated_event_and_cannot_be_replayed()
    {
        using var factory = BuildFactory();
        using var anonymous = NewClient(factory);
        var secret = await MintReplayCredentialAsync(factory, anonymous);

        var accepted = await PostFrameAsync(anonymous, secret, Request(epoch: 1, index: 0));
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var body = await accepted.Content.ReadFromJsonAsync<DemoReplayEventAccepted>();
        Assert.Equal(DemoReplayOrigins.Simulated, body!.Origin);

        var journal = Path.Combine(_taskFolder, "logs", "runner-events.jsonl");
        Assert.True(File.Exists(journal));
        var recorded = JsonDocument.Parse(File.ReadAllLines(journal).Single()).RootElement;
        Assert.Equal(DemoReplayOrigins.Simulated, recorded.GetProperty("origin").GetString());
        Assert.Equal(DemoReplayFrameKinds.SessionStarted, recorded.GetProperty("kind").GetString());

        // The same sealed frame offered again inside the same epoch is refused.
        var replayed = await PostFrameAsync(anonymous, secret, Request(epoch: 1, index: 0));
        Assert.Equal(HttpStatusCode.Forbidden, replayed.StatusCode);
        Assert.Equal(DemoReplayDenialCodes.SequenceNotMonotonic, await ErrorCodeAsync(replayed));

        // An epoch the instance has already left behind is refused as well.
        Assert.Equal(HttpStatusCode.Accepted, (await PostFrameAsync(anonymous, secret, Request(epoch: 2, index: 1))).StatusCode);
        var stale = await PostFrameAsync(anonymous, secret, Request(epoch: 1, index: 2));
        Assert.Equal(HttpStatusCode.Forbidden, stale.StatusCode);
        Assert.Equal(DemoReplayDenialCodes.EpochStale, await ErrorCodeAsync(stale));
    }

    [Fact]
    public async Task A_compromised_replay_process_cannot_forge_a_frame_the_server_accepts()
    {
        using var factory = BuildFactory();
        using var anonymous = NewClient(factory);
        var secret = await MintReplayCredentialAsync(factory, anonymous);

        // Editing a sealed frame invalidates the seal it was shipped with.
        var original = Request(epoch: 1, index: 2);
        var tampered = original with { Frame = original.Frame with { Message = "Deploying to production" } };
        var tamperedResponse = await PostFrameAsync(anonymous, secret, tampered);
        Assert.Equal(HttpStatusCode.Forbidden, tamperedResponse.StatusCode);
        Assert.Equal(DemoReplayDenialCodes.SignatureInvalid, await ErrorCodeAsync(tamperedResponse));

        // Re-signing with an attacker key does not help: the server pins the
        // release public key, not whatever the caller used.
        using var attackerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var forgedFrame = original.Frame with { Message = "Deploying to production" };
        var forged = original with
        {
            Frame = forgedFrame,
            Signature = Convert.ToBase64String(attackerKey.SignData(
                DemoReplayTraceSignature.FramePayload(TraceId, _trace.Digest, forgedFrame),
                HashAlgorithmName.SHA256)),
        };
        var forgedResponse = await PostFrameAsync(anonymous, secret, forged);
        Assert.Equal(HttpStatusCode.Forbidden, forgedResponse.StatusCode);
        Assert.Equal(DemoReplayDenialCodes.SignatureInvalid, await ErrorCodeAsync(forgedResponse));

        // Even a correctly sealed frame cannot leave the pinned demo scene.
        var outsideFrame = new DemoReplayFrame(9, 0, "AGT-2668", DemoReplayFrameKinds.TurnStarted, "Escape");
        var outside = new DemoReplayEventRequest(
            TraceId,
            _trace.Digest,
            1,
            Convert.ToBase64String(_signingKey.SignData(
                DemoReplayTraceSignature.FramePayload(TraceId, _trace.Digest, outsideFrame),
                HashAlgorithmName.SHA256)),
            outsideFrame,
            DateTime.UtcNow);
        var outsideResponse = await PostFrameAsync(anonymous, secret, outside);
        Assert.Equal(HttpStatusCode.Forbidden, outsideResponse.StatusCode);
        Assert.Equal(DemoReplayDenialCodes.SceneKeyDenied, await ErrorCodeAsync(outsideResponse));

        // A frame from a differently pinned bundle is refused by digest.
        var wrongDigest = original with { TraceDigest = new string('a', 64) };
        var wrongDigestResponse = await PostFrameAsync(anonymous, secret, wrongDigest);
        Assert.Equal(HttpStatusCode.Forbidden, wrongDigestResponse.StatusCode);
        Assert.Equal(DemoReplayDenialCodes.DigestMismatch, await ErrorCodeAsync(wrongDigestResponse));

        Assert.False(File.Exists(Path.Combine(_taskFolder, "logs", "runner-events.jsonl")));
    }

    [Fact]
    public async Task A_replay_credential_cannot_be_widened_into_an_execution_credential()
    {
        using var factory = BuildFactory();
        using var anonymous = NewClient(factory);
        using var browser = NewClient(factory, cookies: true);
        var csrf = await BootstrapOwnerAsync(browser);

        var widened = await EnrollAsync(browser, csrf, "widened-replay", [RunnerScopes.DemoReplay, RunnerScopes.Claim]);
        Assert.Equal(HttpStatusCode.BadRequest, widened.StatusCode);
        Assert.Equal("invalid-scope", await ErrorCodeAsync(widened));

        var everything = await EnrollAsync(browser, csrf, "everything-replay", [.. RunnerScopes.Minimum, RunnerScopes.DemoReplay]);
        Assert.Equal(HttpStatusCode.BadRequest, everything.StatusCode);
        Assert.Equal("invalid-scope", await ErrorCodeAsync(everything));

        // A default credential never silently gains the replay scope.
        var standard = await EnrollAsync(browser, csrf, "standard-runner", null);
        standard.EnsureSuccessStatusCode();
        var enrollment = await standard.Content.ReadFromJsonAsync<OneTimeEnrollmentResponse>();
        Assert.DoesNotContain(RunnerScopes.DemoReplay, enrollment!.Scopes);

        // ...and an execution credential cannot reach the replay ingest.
        var enrolled = await anonymous.PostAsJsonAsync("/api/auth/runner-enroll", new { code = enrollment.EnrollmentCode });
        enrolled.EnsureSuccessStatusCode();
        var credential = await enrolled.Content.ReadFromJsonAsync<OneTimeSecretResponse>();
        var denial = await PostFrameAsync(anonymous, credential!.Secret, Request(epoch: 1, index: 0));
        Assert.Equal(HttpStatusCode.Forbidden, denial.StatusCode);
        Assert.Equal("runner-scope-denied", await ErrorCodeAsync(denial));
    }

    private DemoReplayEventRequest Request(long epoch, int index)
    {
        var frame = _trace.Trace.Frames[index];
        return new DemoReplayEventRequest(
            TraceId,
            _trace.Digest,
            epoch,
            _trace.Seals.Single(seal => seal.Sequence == frame.Sequence).Signature,
            frame,
            new DateTime(2026, 8, 9, 8, 0, 0, DateTimeKind.Utc).AddSeconds(frame.OffsetSeconds));
    }

    private static Task<HttpResponseMessage> PostFrameAsync(HttpClient client, string secret, DemoReplayEventRequest request)
        => PostAsync(client, secret, "/api/runner/replay/events", request);

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string secret, string route, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return await client.SendAsync(request);
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return payload.TryGetProperty("error", out var error) ? error.GetString() : null;
    }

    private async Task<string> MintReplayCredentialAsync(WebApplicationFactory<Program> factory, HttpClient anonymous)
    {
        using var browser = NewClient(factory, cookies: true);
        var csrf = await BootstrapOwnerAsync(browser);
        var enrollmentResponse = await EnrollAsync(browser, csrf, "demo-runner-replay", [RunnerScopes.DemoReplay]);
        enrollmentResponse.EnsureSuccessStatusCode();
        var enrollment = await enrollmentResponse.Content.ReadFromJsonAsync<OneTimeEnrollmentResponse>();
        Assert.Equal([RunnerScopes.DemoReplay], enrollment!.Scopes);

        var enrolled = await anonymous.PostAsJsonAsync("/api/auth/runner-enroll", new { code = enrollment.EnrollmentCode });
        enrolled.EnsureSuccessStatusCode();
        var credential = await enrolled.Content.ReadFromJsonAsync<OneTimeSecretResponse>();
        return credential!.Secret;
    }

    private static async Task<string> BootstrapOwnerAsync(HttpClient browser)
    {
        var bootstrap = await browser.PostAsJsonAsync("/api/auth/bootstrap", new
        {
            username = "demo.owner",
            password = "correct horse battery staple!",
            displayName = "Demo Owner",
        });
        bootstrap.EnsureSuccessStatusCode();
        var auth = await bootstrap.Content.ReadFromJsonAsync<AuthStatusResponse>();
        return auth!.CsrfToken!;
    }

    private static async Task<HttpResponseMessage> EnrollAsync(
        HttpClient browser, string csrf, string name, string[]? scopes)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/runner-enrollments")
        {
            Content = JsonContent.Create(scopes is null ? new { name } : (object)new { name, scopes }),
        };
        request.Headers.Add("X-CSRF-Token", csrf);
        return await browser.SendAsync(request);
    }

    private static HttpClient NewClient(WebApplicationFactory<Program> factory, bool cookies = false)
        => factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://studio.test"),
            HandleCookies = cookies,
        });

    private WebApplicationFactory<Program> BuildFactory()
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["WatchPaths:0:Name"] = ProjectName,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
                ["Security:Profile"] = "networked",
                ["AllowedHosts"] = "studio.test",
                ["DemoReplay:Enabled"] = "true",
                ["DemoReplay:TraceId"] = TraceId,
                ["DemoReplay:TraceDigest"] = _trace.Digest,
                ["DemoReplay:SigningKeyId"] = _trace.KeyId,
                ["DemoReplay:PublicKeyBase64"] = Convert.ToBase64String(_signingKey.ExportSubjectPublicKeyInfo()),
            }));
        });
}
