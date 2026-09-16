extern alias UpdSvc;
using System.Net.Http.Json;
using System.Text.Json;

using UpdSvc::AgentTaskboard.UpdateService;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2847 restart drill for the immutable-release pipeline. It reproduces
/// the v0.3.0 -> v0.4.0 run that could never pass: the checkout root carried
/// the previous manifest, the candidate lived only in the run folder, and the
/// restarted backend therefore reported the previous release forever.
///
/// The fake start script resolves the identity the launched backend would
/// report exactly the way <c>BuildIdentity.Load</c> does (ATP_BUILD_MANIFEST
/// first, the manifest copied out of the checkout root second), so these
/// cases prove the handoff rather than asserting that some variable was set.
///
/// Cases:
///
///   - RestartDrill_PreviousManifestInRoot_CandidateInRunFolder_VerifiesAndCommits:
///     the restarted backend reports the candidate, runtime-identity
///     verification passes, and only then is the manifest committed to the
///     checkout root.
///   - RestartDrill_LegacyManifestInRoot_...: same, departing from a
///     pre-contract (untagged, legacy) installation.
///   - RestartDrill_FailureAfterRestart_RevertsCheckoutAndLeavesRootManifest:
///     the failure path still reverts the checkout and never touches the root
///     manifest.
///   - Preflight_AfterFailedVerification_ReportsUpgradeInVerification: the
///     state that failure leaves behind (process on the candidate, root on the
///     previous manifest) is classified as "upgrade in verification", not as a
///     running/installed divergence.
///
/// Unlike <see cref="UpdateServiceIntegrationTests"/> this suite does not
/// drive npm/dotnet restore: the candidate's release contract declares a
/// no-op restore command, so it runs anywhere bash and git are available.
/// </summary>
// MachineBound: drives real bash + git processes and healthz polling.
[Trait("Category", "MachineBound")]
public class UpdateServiceRestartDrillTests
{
    private const int TriggerTimeoutMs = 120_000;
    private const string InstalledTag = "v0.3.0";
    private const string CandidateTag = "v0.4.0";

    [SkippableFact]
    public async Task RestartDrill_PreviousManifestInRoot_CandidateInRunFolder_VerifiesAndCommits()
    {
        using var checkout = FakeStableCheckout.TryCreate();
        Skip.If(checkout == null, "git and/or bash are not available on PATH; this restart drill needs both.");

        await using var backend = new FakeBackendHarness();
        await backend.StartAsync();

        var candidateCommit = PrepareUpgrade(checkout!, backend, Installed(checkout!));
        var headBefore = checkout!.ReadStableHead();

        using var factory = new UpdateServiceTestFactory(
            checkout, backend, autoRollback: false, requireReleaseManifest: true);
        var client = factory.CreateClient();

        await TriggerAsync(client);
        var status = await WaitForPhaseAsync(client, new[] { "done", "failed" }, TriggerTimeoutMs);
        var phase = status.GetProperty("phase").GetString();
        var message = status.TryGetProperty("message", out var m) ? m.GetString() : null;
        Assert.True(phase == "done", $"restart drill expected phase=done, got {phase}; message={message}");

        // The start wrapper was handed the run folder's candidate manifest,
        // not the checkout root's previous one.
        var runFolder = LatestRunFolder(checkout.RunsDir);
        var intendedManifest = Path.Combine(runFolder, "intended-build-manifest.json");
        Assert.Equal(intendedManifest, checkout.StartBuildManifestEnv());
        Assert.Contains($"ATP_BUILD_MANIFEST={intendedManifest}",
            File.ReadAllText(Path.Combine(runFolder, "start-stable-output.txt")));

        // Therefore the restarted backend reported the candidate identity and
        // runtime-identity verification had something to agree with.
        var observed = ReadManifest(checkout.RuntimeIdentityPath);
        Assert.Equal(CandidateTag, observed.Tag);
        Assert.Equal(candidateCommit, observed.Commit);

        // Only after verification is the manifest committed to the root, and
        // the checkout stays on the candidate commit.
        var installedAfter = ReadManifest(checkout.InstalledManifestPath);
        Assert.Equal(CandidateTag, installedAfter.Tag);
        Assert.Equal(candidateCommit, installedAfter.Commit);
        Assert.Equal(candidateCommit, checkout.ReadStableHead());
        Assert.NotEqual(headBefore, checkout.ReadStableHead());

        var entry = Assert.Single(ReadHistory(checkout.HistoryFile), h => h.Status == "ok");
        Assert.Equal(CandidateTag, entry.IntendedTag);
        Assert.Equal(CandidateTag, entry.ObservedTag);
        Assert.Equal("Upgrade", entry.ReleaseDirection);
    }

    [SkippableFact]
    public async Task RestartDrill_LegacyManifestInRoot_VerifiesAndCommitsTheFirstRelease()
    {
        using var checkout = FakeStableCheckout.TryCreate();
        Skip.If(checkout == null, "git and/or bash are not available on PATH; this restart drill needs both.");

        await using var backend = new FakeBackendHarness();
        await backend.StartAsync();

        PrepareUpgrade(checkout!, backend, Legacy());

        using var factory = new UpdateServiceTestFactory(
            checkout!, backend, autoRollback: false, requireReleaseManifest: true);
        var client = factory.CreateClient();

        await TriggerAsync(client);
        var status = await WaitForPhaseAsync(client, new[] { "done", "failed" }, TriggerTimeoutMs);
        var phase = status.GetProperty("phase").GetString();
        var message = status.TryGetProperty("message", out var m) ? m.GetString() : null;
        Assert.True(phase == "done", $"legacy restart drill expected phase=done, got {phase}; message={message}");

        Assert.Equal(CandidateTag, ReadManifest(checkout!.RuntimeIdentityPath).Tag);
        Assert.Equal(CandidateTag, ReadManifest(checkout.InstalledManifestPath).Tag);
    }

    [SkippableFact]
    public async Task RestartDrill_FailureAfterRestart_RevertsCheckoutAndLeavesRootManifest()
    {
        using var checkout = FakeStableCheckout.TryCreate();
        Skip.If(checkout == null, "git and/or bash are not available on PATH; this restart drill needs both.");

        await using var backend = new FakeBackendHarness();
        await backend.StartAsync();

        PrepareUpgrade(checkout!, backend, Installed(checkout!));
        var headBefore = checkout!.ReadStableHead();
        var rootManifestBefore = File.ReadAllText(checkout.InstalledManifestPath);

        // The frontend port wait is the last gate before the mutation
        // boundary. Pointing it at a port nothing listens on fails the run
        // after the backend has already restarted onto the candidate.
        using var factory = new UpdateServiceTestFactory(
            checkout, backend, autoRollback: false, requireReleaseManifest: true,
            frontendUrl: "http://127.0.0.1:1", frontendWaitSeconds: 1);
        var client = factory.CreateClient();

        await TriggerAsync(client);
        var status = await WaitForPhaseAsync(client, new[] { "done", "failed" }, TriggerTimeoutMs);
        Assert.Equal("failed", status.GetProperty("phase").GetString());
        Assert.Contains("frontend dev server did not come up", status.GetProperty("message").GetString());

        // The restart did happen and did report the candidate, so the failure
        // is genuinely past the restart, not a preflight refusal.
        Assert.True(checkout.StartRan(), "start-stable.sh marker missing - the drill did not reach the restart");
        Assert.Equal(CandidateTag, ReadManifest(checkout.RuntimeIdentityPath).Tag);

        // Mutation boundary holds: checkout back on the pre-run commit, root
        // manifest byte-identical to what it was before the run.
        Assert.Equal(headBefore, checkout.ReadStableHead());
        Assert.Equal(rootManifestBefore, File.ReadAllText(checkout.InstalledManifestPath));
        Assert.Equal(InstalledTag, ReadManifest(checkout.InstalledManifestPath).Tag);
    }

    [SkippableFact]
    public async Task Preflight_AfterFailedVerification_ReportsUpgradeInVerification()
    {
        using var checkout = FakeStableCheckout.TryCreate();
        Skip.If(checkout == null, "git and/or bash are not available on PATH; this restart drill needs both.");

        await using var backend = new FakeBackendHarness();
        await backend.StartAsync();

        PrepareUpgrade(checkout!, backend, Installed(checkout!));

        using var factory = new UpdateServiceTestFactory(
            checkout!, backend, autoRollback: false, requireReleaseManifest: true,
            frontendUrl: "http://127.0.0.1:1", frontendWaitSeconds: 1);
        var client = factory.CreateClient();

        await TriggerAsync(client);
        var status = await WaitForPhaseAsync(client, new[] { "done", "failed" }, TriggerTimeoutMs);
        Assert.Equal("failed", status.GetProperty("phase").GetString());

        // The run left the process on the candidate and the root on the
        // previous manifest. That is the verification window, not a
        // divergence, so the next preflight must still allow the retry.
        using var response = await client.GetAsync("/update/preflight");
        response.EnsureSuccessStatusCode();
        var comparison = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.True(comparison.GetProperty("upgradeInVerification").GetBoolean(),
            $"expected upgradeInVerification=true, got {comparison}");
        Assert.Equal("Upgrade", comparison.GetProperty("direction").GetString());
        Assert.Equal("upgrade in verification", comparison.GetProperty("summary").GetString());
        Assert.True(comparison.GetProperty("allowed").GetBoolean(),
            $"upgrade in verification must stay retryable, got {comparison}");
        Assert.DoesNotContain(
            comparison.GetProperty("errors").EnumerateArray().Select(e => e.GetString()),
            e => e is not null && e.Contains("running identity diverges"));
    }

    // ─── fixture wiring ─────────────────────────────────────────────────────

    /// <summary>
    /// Sets up the v0.3.0 -> v0.4.0 upgrade the incident run attempted:
    /// <paramref name="installed"/> in the checkout root (and as the identity
    /// the currently-running backend reports), the candidate manifest plus the
    /// approved tag in the workspace metadata, and the tagged candidate commit
    /// pushed to the remote. Returns the candidate commit SHA.
    /// </summary>
    private static string PrepareUpgrade(
        FakeStableCheckout checkout, FakeBackendHarness backend, ReleaseManifest installed)
    {
        var candidateCommit = checkout.TagRelease(CandidateTag, new[] { "true" });

        File.WriteAllText(checkout.InstalledManifestPath, Serialize(installed));
        File.WriteAllText(checkout.RuntimeIdentityPath, Serialize(installed));
        backend.RuntimeIdentityFile = checkout.RuntimeIdentityPath;

        File.WriteAllText(checkout.CandidateManifestPath, Serialize(Candidate(candidateCommit)));
        File.WriteAllText(checkout.ApprovedTagPath, CandidateTag + "\n");
        return candidateCommit;
    }

    private static ReleaseManifest Installed(FakeStableCheckout checkout) =>
        Manifest(InstalledTag, "0.3.0", checkout.ReadStableHead());

    /// <summary>Pre-contract installation: untagged, dirty, no build time.</summary>
    private static ReleaseManifest Legacy() => Manifest("untagged", "0.0.0-migration", "legacy") with
    {
        Dirty = true,
        BuiltAt = null,
        Integrity = "unverified",
        CodingAgentRunner = new ReleaseArtifact("CodingAgentRunner", "unknown", "untagged", "unknown", "unverified"),
        CodingAgentChat = new ReleaseArtifact("coding-agent-chat", "unknown", "untagged", "unknown", "unverified"),
        Legacy = true,
    };

    private static ReleaseManifest Candidate(string commit) => Manifest(CandidateTag, "0.4.0", commit);

    private static ReleaseManifest Manifest(string tag, string version, string commit) => new(
        1, "Agent Studio", tag, version, commit, false,
        DateTimeOffset.Parse("2026-09-16T10:00:00Z"), "sha256-app",
        new ReleaseArtifact("CodingAgentRunner", "0.5.0", "v0.5.0", "car", "sha512-package"),
        new ReleaseArtifact("coding-agent-chat", "0.1.0", "v0.1.0", "cac", "sha512-package"));

    private static string Serialize(ReleaseManifest manifest) =>
        JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        });

    private static ReleaseManifest ReadManifest(string path)
    {
        Assert.True(File.Exists(path), $"expected a manifest at {path}");
        return StableReleaseContract.Read(File.ReadAllText(path));
    }

    // ─── shared polling helpers ─────────────────────────────────────────────

    private static async Task TriggerAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/update/trigger", new { Reason = "manual", Force = false });
        response.EnsureSuccessStatusCode();
    }

    private static async Task<JsonElement> WaitForPhaseAsync(HttpClient client, string[] terminalPhases, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        JsonElement last = default;
        while (DateTime.UtcNow < deadline)
        {
            using var response = await client.GetAsync("/update/status", cts.Token);
            response.EnsureSuccessStatusCode();
            last = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token)).RootElement.Clone();
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
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return File.ReadAllLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<UpdateHistoryEntry>(line, options)!)
            .ToArray();
    }

    private static string LatestRunFolder(string runsRoot)
    {
        Assert.True(Directory.Exists(runsRoot), $"runs root does not exist: {runsRoot}");
        return new DirectoryInfo(runsRoot).GetDirectories()
            .OrderByDescending(d => d.CreationTimeUtc).First().FullName;
    }
}
