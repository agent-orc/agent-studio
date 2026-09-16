extern alias UpdSvc;
using System.Net.Http.Json;
using System.Text.Json;

using UpdSvc::AgentTaskboard.UpdateService;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2847 restart drill for the immutable-release pipeline.
///
/// The candidate <c>build-manifest.json</c> is deliberately kept out of the
/// Stable checkout root until restart has cleared health, runtime identity,
/// and the frontend port (the mutation boundary). The backend resolves its
/// identity from <c>ATP_BUILD_MANIFEST</c> or from the manifest the build
/// copied beside its assembly out of that same checkout root, so without an
/// explicit handoff the restarted process reports the previous release and no
/// upgrade can ever pass the runtime-identity check.
///
/// The fake start script records the <c>ATP_BUILD_MANIFEST</c> it was handed,
/// and the fake backend resolves <c>/api/system/version</c> the way
/// <c>BuildIdentity.Load</c> does: the handed manifest when there is one,
/// otherwise the checkout root's. That makes the three cases below a real
/// drill rather than an assertion about an internal call.
///
///   - Upgrade: previous manifest in the checkout root, candidate in the run
///     folder, restarted backend reports the candidate, verification passes,
///     and the manifest is committed into the root.
///   - Failure path: the restarted backend keeps reporting the previous
///     identity (a start script that drops the environment, or a build that
///     did not take); the run fails, reverts the checkout, and leaves the root
///     manifest untouched.
///   - Preflight: a backend at the candidate identity while the root still
///     holds the previous manifest reads as "upgrade in verification", not as
///     a running/installed divergence.
///
/// Skipped when bash or git are unavailable; the fake stable checkout needs
/// both. Unlike <see cref="UpdateServiceIntegrationTests"/> this drill does
/// not touch npm or the Windows-native deploy machinery, so it runs on any
/// host that has a POSIX shell.
/// </summary>
public class UpdateServiceRestartDrillTests
{
    private const int TriggerTimeoutMs = 120_000;
    private const string CandidateTag = "v0.4.0";
    private const string CandidateVersion = "0.4.0";
    private const string PreviousVersion = "0.3.0";

    [SkippableFact]
    public async Task Upgrade_HandsIntendedManifestToTheRestartedBackend_AndCommitsItAfterVerification()
    {
        using var checkout = FakeStableCheckout.TryCreate();
        Skip.If(checkout == null, "git and/or bash are not available on PATH; this drill needs both.");

        await using var backend = new FakeBackendHarness();
        await backend.StartAsync();

        var release = SeedRelease(checkout!);
        // The backend resolves its identity exactly like BuildIdentity.Load:
        // the manifest handed through ATP_BUILD_MANIFEST wins, the checkout
        // root's manifest (what the build copies beside the assembly) is the
        // fallback.
        backend.RuntimeManifestJson = () => ResolveRuntimeManifest(checkout!);

        var headBefore = checkout!.ReadStableHead();
        using var factory = new UpdateServiceTestFactory(checkout, backend, autoRollback: false,
            requireReleaseManifest: true, restartHealthWaitSeconds: 30);
        var client = factory.CreateClient();

        var runId = await TriggerAsync(client);
        var status = await WaitForPhaseAsync(client, new[] { "done", "failed" }, TriggerTimeoutMs);
        var message = status.TryGetProperty("message", out var m) ? m.GetString() : null;
        Assert.True(status.GetProperty("phase").GetString() == "done",
            $"restart drill expected phase=done, got {status.GetProperty("phase").GetString()}; message={message}");

        // The identity handoff: the backend was started with the run folder's
        // intended manifest, not with whatever sits in the checkout root.
        var runFolder = Path.Combine(checkout.RunsDir, runId);
        var intendedManifest = Path.Combine(runFolder, "intended-build-manifest.json");
        Assert.True(File.Exists(intendedManifest), "intended-build-manifest.json missing from the run folder");
        Assert.Equal(Path.GetFullPath(intendedManifest), checkout.ReadStartManifestEnv());

        // The run folder's manifest is the candidate, byte content aside.
        Assert.Equal(CandidateTag, StableReleaseContract.Read(File.ReadAllText(intendedManifest)).Tag);

        // Mutation boundary: the candidate manifest is committed into the
        // checkout root only now, after verification passed.
        var installed = StableReleaseContract.Read(File.ReadAllText(checkout.RootManifestFile));
        Assert.Equal(CandidateTag, installed.Tag);
        Assert.Equal(release.CandidateCommit, installed.Commit);
        Assert.Equal(release.CandidateCommit, checkout.ReadStableHead());
        Assert.NotEqual(headBefore, checkout.ReadStableHead());

        // History records the identity the run intended and the one it
        // actually verified the running backend at.
        var entry = Assert.Single(ReadHistory(checkout.HistoryFile));
        Assert.Equal("ok", entry.Status);
        Assert.Equal(CandidateTag, entry.IntendedTag);
        Assert.Equal(CandidateTag, entry.ObservedTag);
        Assert.Equal("Upgrade", entry.ReleaseDirection);
    }

    [SkippableFact]
    public async Task RestartedBackendStillReportingThePreviousIdentity_RevertsWithoutTouchingTheRootManifest()
    {
        using var checkout = FakeStableCheckout.TryCreate();
        Skip.If(checkout == null, "git and/or bash are not available on PATH; this drill needs both.");

        await using var backend = new FakeBackendHarness();
        await backend.StartAsync();

        SeedRelease(checkout!);
        // A backend that ignores the handoff: the start wrapper dropped the
        // environment, or the restart did not pick up the new build. The run
        // must fail on runtime identity rather than declare the release done.
        backend.RuntimeManifestJson = () => File.ReadAllText(checkout!.RootManifestFile);

        var headBefore = checkout!.ReadStableHead();
        var rootManifestBefore = File.ReadAllText(checkout.RootManifestFile);
        using var factory = new UpdateServiceTestFactory(checkout, backend, autoRollback: false,
            requireReleaseManifest: true, restartHealthWaitSeconds: 30);
        var client = factory.CreateClient();

        await TriggerAsync(client);
        var status = await WaitForPhaseAsync(client, new[] { "done", "failed" }, TriggerTimeoutMs);
        Assert.Equal("failed", status.GetProperty("phase").GetString());
        Assert.Contains("runtime identity does not equal intended build manifest",
            status.GetProperty("message").GetString());

        var failures = status.GetProperty("verificationFailures");
        Assert.Equal(JsonValueKind.Array, failures.ValueKind);
        var failure = Assert.Single(failures.EnumerateArray());
        Assert.Equal("runtime-identity", failure.GetProperty("step").GetString());
        Assert.Equal($"v{PreviousVersion}", failure.GetProperty("observed").GetString());
        Assert.Equal(CandidateTag, failure.GetProperty("expected").GetString());

        // The checkout is back at the pre-run commit and the installed
        // manifest was never written: the next preflight sees exactly the
        // state it saw before this run.
        Assert.Equal(headBefore, checkout.ReadStableHead());
        Assert.Equal(rootManifestBefore, File.ReadAllText(checkout.RootManifestFile));
    }

    [SkippableFact]
    public async Task Preflight_BackendAtCandidateWhileRootHoldsPrevious_IsUpgradeInVerification()
    {
        using var checkout = FakeStableCheckout.TryCreate();
        Skip.If(checkout == null, "git and/or bash are not available on PATH; this drill needs both.");

        await using var backend = new FakeBackendHarness();
        await backend.StartAsync();

        var release = SeedRelease(checkout!);
        // The state an interrupted run leaves behind, and the state every
        // upgrade passes through between restart and the mutation boundary:
        // the process is already the candidate, the root manifest is not.
        backend.RuntimeManifestJson = () => release.CandidateJson;

        using var factory = new UpdateServiceTestFactory(checkout!, backend, autoRollback: false,
            requireReleaseManifest: true, mode: "manual");
        var client = factory.CreateClient();

        using var response = await client.GetAsync("/update/preflight");
        response.EnsureSuccessStatusCode();
        var preflight = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.True(preflight.GetProperty("allowed").GetBoolean(),
            $"errors: {preflight.GetProperty("errors")}");
        Assert.True(preflight.GetProperty("upgradeInVerification").GetBoolean());
        Assert.Equal("Upgrade", preflight.GetProperty("direction").GetString());
        Assert.Contains("upgrade in verification", preflight.GetProperty("summary").GetString());
        Assert.Empty(preflight.GetProperty("errors").EnumerateArray());
    }

    // ─── fixture ────────────────────────────────────────────────────────────

    private sealed record ReleaseFixture(string CandidateCommit, string CandidateJson, string PreviousJson);

    /// <summary>
    /// Publishes the tagged candidate commit on the fake remote and lays down
    /// the three files the release preflight reads: the installed manifest in
    /// the checkout root, the cached candidate manifest, and the cached
    /// latest-approved tag.
    /// </summary>
    private static ReleaseFixture SeedRelease(FakeStableCheckout checkout)
    {
        var previousJson = ManifestJson(PreviousVersion, checkout.ReadStableHead());
        var candidateCommit = checkout.PublishReleaseCommit(CandidateTag, new Dictionary<string, string>
        {
            [".agent-studio/project.yml"] = ProjectDefinitionYaml,
        });
        var candidateJson = ManifestJson(CandidateVersion, candidateCommit);

        File.WriteAllText(checkout.RootManifestFile, previousJson);
        File.WriteAllText(checkout.CandidateManifestFile, candidateJson);
        File.WriteAllText(checkout.ApprovedTagFile, CandidateTag + "\n");
        return new ReleaseFixture(candidateCommit, candidateJson, previousJson);
    }

    private static string? ResolveRuntimeManifest(FakeStableCheckout checkout)
    {
        var handed = checkout.ReadStartManifestEnv();
        if (handed is not null && File.Exists(handed)) return File.ReadAllText(handed);
        return File.Exists(checkout.RootManifestFile) ? File.ReadAllText(checkout.RootManifestFile) : null;
    }

    private static string ManifestJson(string version, string commit) => $$"""
        {
          "schemaVersion": 1,
          "application": "Agent Studio",
          "tag": "v{{version}}",
          "version": "{{version}}",
          "commit": "{{commit}}",
          "dirty": false,
          "builtAt": "2026-09-16T10:00:00+00:00",
          "integrity": "sha256-app",
          "codingAgentRunner": {
            "name": "CodingAgentRunner",
            "version": "0.5.0",
            "tag": "v0.5.0",
            "commit": "car0000",
            "integrity": "sha512-car"
          },
          "codingAgentChat": {
            "name": "coding-agent-chat",
            "version": "0.1.0",
            "tag": "v0.1.0",
            "commit": "cac0000",
            "integrity": "sha512-cac"
          },
          "legacy": false
        }
        """;

    /// <summary>
    /// Release contract of the candidate commit. Exact version/integrity pins
    /// keep the drill focused on the identity handoff: no lock files have to
    /// be read from the tagged commit, and the restore step is a no-op instead
    /// of a real dotnet/npm restore.
    /// </summary>
    private const string ProjectDefinitionYaml = """
        schemaVersion: 1
        stack: [dotnet, node]
        toolVersions:
        commands:
          prepare: .agent-studio/prepare
          build:
          test:
          lint:
        testSuites:
        cachePaths: [frontend/node_modules]
        capabilities: [linux]
        environment:
          CI: "true"
        release:
          identity:
            - package: CodingAgentRunner
              ecosystem: nuget
              version: 0.5.0
              integrity: sha512-car
            - package: coding-agent-chat
              ecosystem: npm
              version: 0.1.0
              integrity: sha512-cac
          restore:
            - echo "restore is a no-op in the restart drill"
        """;

    // ─── helpers ────────────────────────────────────────────────────────────

    private static async Task<string> TriggerAsync(HttpClient client)
    {
        using var resp = await client.PostAsJsonAsync("/update/trigger", new { Force = false });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<TriggerResponse>();
        Assert.NotNull(body);
        return body!.RunId;
    }

    private static async Task<JsonElement> WaitForPhaseAsync(HttpClient client, string[] terminalPhases, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        JsonElement last = default;
        while (DateTime.UtcNow < deadline)
        {
            using var resp = await client.GetAsync("/update/status", cts.Token);
            resp.EnsureSuccessStatusCode();
            last = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token)).RootElement.Clone();
            var phase = last.GetProperty("phase").GetString();
            if (phase != null && terminalPhases.Contains(phase)) return last;
            await Task.Delay(200, cts.Token);
        }
        var lastPhase = last.ValueKind == JsonValueKind.Object ? last.GetProperty("phase").GetString() : "(none)";
        throw new TimeoutException(
            $"Did not reach any of [{string.Join(", ", terminalPhases)}] within {timeoutMs} ms. Last phase: {lastPhase}.");
    }

    private static UpdateHistoryEntry[] ReadHistory(string path)
    {
        if (!File.Exists(path)) return Array.Empty<UpdateHistoryEntry>();
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return File.ReadAllLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<UpdateHistoryEntry>(line, opts)!)
            .ToArray();
    }
}
