extern alias UpdSvc;
using System.Net.Http.Json;
using System.Text.Json;

using UpdSvc::AgentTaskboard.UpdateService;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2847 restart drill. The Update Service keeps the candidate
/// <c>build-manifest.json</c> out of the Stable checkout until restart has
/// cleared health, runtime identity, and the frontend port (the mutation
/// boundary). The backend, however, reads its identity from
/// <c>ATP_BUILD_MANIFEST</c> or from the manifest the build copied next to
/// its assembly out of that same checkout root, so before this drill existed
/// a restarted backend could only ever report the *previous* release and every
/// upgrade failed its own runtime-identity check (run 197866f8, 16.09.2026,
/// v0.3.0 -> v0.4.0).
///
/// The fixture models the real chain honestly: the fake start-stable.sh
/// resolves the booted backend's manifest exactly the way
/// <c>BuildIdentity.Load</c> does (environment first, checkout root second),
/// and <see cref="FakeBackendHarness"/> reports whatever that resolved to.
/// The drill therefore fails if the orchestrator stops handing the manifest
/// over, not just if an assertion is removed.
///
/// Cases:
///   - Upgrade: previous manifest in the checkout root, candidate only in the
///     run folder, restarted backend reports the candidate, verification
///     passes, and the manifest is committed into the checkout afterwards.
///   - Failure after the handoff: the frontend never comes up, so the run
///     fails before the mutation boundary. The checkout reverts, the root
///     manifest is untouched, and the following preflight reads the resulting
///     running/installed difference as "upgrade in verification" rather than
///     refusing it as a divergence.
/// </summary>
public class UpdateServiceRestartIdentityDrillTests
{
    private const int TriggerTimeoutMs = 180_000;
    private const string PreviousVersion = "0.3.0";
    private const string CandidateVersion = "0.4.0";
    private const string CandidateTag = "v" + CandidateVersion;

    [SkippableFact]
    public async Task Upgrade_RestartedBackendReportsTheCandidate_ThenTheManifestIsCommitted()
    {
        using var checkout = FakeStableCheckout.TryCreate();
        Skip.If(checkout == null, "git and/or bash are not available on PATH; this drill needs both.");

        var installedManifest = ManifestJson(PreviousVersion, checkout!.ReadStableHead());
        var candidateCommit = checkout.PublishReleaseCandidate(CandidateTag, ProjectDefinitionYaml);
        var candidateManifest = ManifestJson(CandidateVersion, candidateCommit);

        checkout.InstallManifest(installedManifest);
        checkout.PublishCandidateManifest(candidateManifest, CandidateTag);
        checkout.BootBackendWith(installedManifest);

        await using var backend = new FakeBackendHarness { RuntimeIdentityFile = checkout.RuntimeIdentityFile };
        await backend.StartAsync();

        using var factory = NewFactory(checkout, backend, frontendUrl: backend.BaseUrl);
        var client = factory.CreateClient();

        await TriggerAsync(client);
        var status = await WaitForPhaseAsync(client, new[] { "done", "failed" }, TriggerTimeoutMs);
        Assert.True(status.GetProperty("phase").GetString() == "done",
            $"upgrade drill expected phase=done, got {status.GetProperty("phase").GetString()}; " +
            $"message={status.GetProperty("message").GetString()}{RunFolderDiagnostics(checkout.RunsDir)}");

        // The candidate manifest lived in the run folder, and that is the file
        // the restarted backend was pointed at.
        var runFolder = LatestRunFolder(checkout.RunsDir);
        var intendedManifestPath = Path.Combine(runFolder, UpdateOrchestrator.IntendedManifestFileName);
        Assert.True(File.Exists(intendedManifestPath),
            $"{UpdateOrchestrator.IntendedManifestFileName} missing from the run folder");
        Assert.Equal(intendedManifestPath, checkout.ReadStartManifestEnv());
        Assert.Contains(UpdateOrchestrator.BuildManifestEnvironmentVariable,
            File.ReadAllText(Path.Combine(runFolder, "start-stable-output.txt")));

        // The restarted backend reported the candidate, so the run cleared its
        // own runtime-identity check instead of failing on the old identity.
        Assert.Equal(CandidateTag, ReadManifest(checkout.RuntimeIdentityFile).Tag);

        var history = ReadHistory(checkout.HistoryFile);
        var entry = Assert.Single(history);
        Assert.Equal("ok", entry.Status);
        Assert.Equal(CandidateTag, entry.IntendedTag);
        Assert.Equal(CandidateTag, entry.ObservedTag);
        Assert.Equal(ReleaseDirection.Upgrade.ToString(), entry.ReleaseDirection);

        // Only now, after verification, does the checkout carry the candidate.
        Assert.Equal(CandidateTag, ReadManifest(checkout.InstalledManifestFile).Tag);
        Assert.Equal(candidateCommit, checkout.ReadStableHead());
    }

    [SkippableFact]
    public async Task FailureAfterTheHandoff_RevertsWithoutTouchingTheRootManifest()
    {
        using var checkout = FakeStableCheckout.TryCreate();
        Skip.If(checkout == null, "git and/or bash are not available on PATH; this drill needs both.");

        var headBefore = checkout!.ReadStableHead();
        var installedManifest = ManifestJson(PreviousVersion, headBefore);
        var candidateCommit = checkout.PublishReleaseCandidate(CandidateTag, ProjectDefinitionYaml);
        var candidateManifest = ManifestJson(CandidateVersion, candidateCommit);

        checkout.InstallManifest(installedManifest);
        checkout.PublishCandidateManifest(candidateManifest, CandidateTag);
        checkout.BootBackendWith(installedManifest);

        await using var backend = new FakeBackendHarness { RuntimeIdentityFile = checkout.RuntimeIdentityFile };
        await backend.StartAsync();

        // Backend health and runtime identity pass; the frontend port never
        // opens, which is the last check before the manifest is committed.
        using var factory = NewFactory(checkout, backend, frontendUrl: ClosedLoopbackUrl(), frontendWaitSeconds: 4);
        var client = factory.CreateClient();

        await TriggerAsync(client);
        var status = await WaitForPhaseAsync(client, new[] { "done", "failed" }, TriggerTimeoutMs);
        Assert.Equal("failed", status.GetProperty("phase").GetString());
        Assert.True(
            status.GetProperty("message").GetString()!.Contains("frontend dev server did not come up"),
            "expected the frontend wait to be the failing step, got: " +
            $"{status.GetProperty("message").GetString()}{RunFolderDiagnostics(checkout.RunsDir)}");

        // The handoff happened, and it stayed out of the checkout: the root
        // manifest is byte-for-byte the pre-run one and HEAD is back.
        var runFolder = LatestRunFolder(checkout.RunsDir);
        Assert.Equal(Path.Combine(runFolder, UpdateOrchestrator.IntendedManifestFileName),
            checkout.ReadStartManifestEnv());
        Assert.Equal(installedManifest, File.ReadAllText(checkout.InstalledManifestFile));
        Assert.Equal(headBefore, checkout.ReadStableHead());

        // The backend that stayed up is the candidate while the checkout root
        // still carries the previous release. That is the verification window,
        // not a divergence, and the preflight has to say so.
        var preflight = await GetJsonAsync(client, "/update/preflight");
        Assert.True(preflight.GetProperty("upgradeInVerification").GetBoolean(),
            "preflight did not recognise the post-restart state as an upgrade in verification");
        Assert.True(preflight.GetProperty("allowed").GetBoolean(),
            $"preflight refused the recovery run: {preflight.GetProperty("errors")}");
        Assert.DoesNotContain("running identity diverges",
            preflight.GetProperty("errors").ToString());
    }

    // ─── harness helpers ────────────────────────────────────────────────────

    private static UpdateServiceTestFactory NewFactory(
        FakeStableCheckout checkout,
        FakeBackendHarness backend,
        string frontendUrl,
        int frontendWaitSeconds = 30)
        => new(checkout, backend,
            autoRollback: false,
            healthWaitSeconds: 30,
            requireReleaseManifest: true,
            frontendUrl: frontendUrl,
            frontendWaitSeconds: frontendWaitSeconds,
            restartHealthWaitSeconds: 30);

    private static async Task TriggerAsync(HttpClient client)
    {
        using var resp = await client.PostAsJsonAsync("/update/trigger", new { Force = false });
        resp.EnsureSuccessStatusCode();
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path, CancellationToken ct = default)
    {
        using var resp = await client.GetAsync(path, ct);
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct)).RootElement.Clone();
    }

    private static async Task<JsonElement> WaitForPhaseAsync(HttpClient client, string[] terminalPhases, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        JsonElement last = default;
        while (DateTime.UtcNow < deadline)
        {
            last = await GetJsonAsync(client, "/update/status", cts.Token);
            var phase = last.GetProperty("phase").GetString();
            if (phase != null && terminalPhases.Contains(phase)) return last;
            await Task.Delay(200, cts.Token);
        }
        var lastPhase = last.ValueKind == JsonValueKind.Object ? last.GetProperty("phase").GetString() : "(none)";
        throw new TimeoutException(
            $"Did not reach any of [{string.Join(", ", terminalPhases)}] within {timeoutMs} ms. Last phase: {lastPhase}.");
    }

    /// <summary>
    /// Run-folder evidence for an assertion message. A drill that fails in an
    /// earlier phase than the one under test should say which phase and why,
    /// instead of leaving the reader to reconstruct it from a temp directory
    /// that the fixture has already deleted.
    /// </summary>
    private static string RunFolderDiagnostics(string runsRoot)
    {
        if (!Directory.Exists(runsRoot)) return "\n(no run folder was created)";
        var folders = new DirectoryInfo(runsRoot).GetDirectories();
        if (folders.Length == 0) return "\n(no run folder was created)";
        var latest = folders.OrderByDescending(d => d.CreationTimeUtc).First();
        var report = new System.Text.StringBuilder($"\n--- run folder {latest.Name} ---");
        foreach (var file in latest.GetFiles().OrderBy(f => f.Name))
        {
            report.AppendLine();
            report.AppendLine($"# {file.Name}");
            try { report.Append(Truncate(File.ReadAllText(file.FullName), 2000)); }
            catch (IOException ex) { report.Append($"(unreadable: {ex.Message})"); }
        }
        return report.ToString();
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + $"... (+{value.Length - max} chars)";

    private static string LatestRunFolder(string runsRoot)
    {
        Assert.True(Directory.Exists(runsRoot), $"runs root does not exist: {runsRoot}");
        return new DirectoryInfo(runsRoot).GetDirectories()
            .OrderByDescending(d => d.CreationTimeUtc).First().FullName;
    }

    private static UpdateHistoryEntry[] ReadHistory(string path)
    {
        if (!File.Exists(path)) return Array.Empty<UpdateHistoryEntry>();
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return File.ReadAllLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonSerializer.Deserialize<UpdateHistoryEntry>(l, opts)!)
            .ToArray();
    }

    private static ReleaseManifest ReadManifest(string path)
    {
        Assert.True(File.Exists(path), $"expected a build manifest at {path}");
        return StableReleaseContract.Read(File.ReadAllText(path));
    }

    /// <summary>A loopback port nothing listens on, for the frontend-down case.</summary>
    private static string ClosedLoopbackUrl()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return $"http://127.0.0.1:{port}";
    }

    // ─── release fixture ────────────────────────────────────────────────────

    /// <summary>
    /// Exact-pin release rules, so the candidate needs no lock files for the
    /// Update Service to prove its manifest against the tagged commit.
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
              integrity: sha512-package
            - package: coding-agent-chat
              ecosystem: npm
              version: 0.1.0
              integrity: sha512-package
          restore:
            - echo locked-restore
        """;

    private static string ManifestJson(string version, string commit) => $$"""
        {
          "schemaVersion": 1,
          "application": "Agent Studio",
          "tag": "v{{version}}",
          "version": "{{version}}",
          "commit": "{{commit}}",
          "dirty": false,
          "builtAt": "2026-09-16T10:00:00+00:00",
          "integrity": "sha256-agent-studio-{{version}}",
          "codingAgentRunner": {
            "name": "CodingAgentRunner",
            "version": "0.5.0",
            "tag": "v0.5.0",
            "commit": "car",
            "integrity": "sha512-package"
          },
          "codingAgentChat": {
            "name": "coding-agent-chat",
            "version": "0.1.0",
            "tag": "v0.1.0",
            "commit": "cac",
            "integrity": "sha512-package"
          },
          "legacy": false
        }
        """;
}
