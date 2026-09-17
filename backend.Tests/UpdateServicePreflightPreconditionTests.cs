extern alias UpdSvc;
using System.Net.Http.Json;
using System.Text.Json;

using UpdSvc::AgentTaskboard.UpdateService;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2865 pure-decision layer: which post-restart verification step, in
/// which observed state, is allowed to refuse an update.
///
/// The rule the incident demands is narrow on purpose. A 401/403/404 proves
/// the step's endpoint is absent or closed by configuration, so a restart
/// reproduces it exactly and the preflight refuses. Everything else - no
/// response, 5xx, an unexpected payload - stays advisory, because an operator
/// updating a backend that is currently unwell must not be locked out by the
/// breakage the update is meant to fix, and phase 6 retries those with its own
/// budgets.
/// </summary>
public class VerificationPreconditionPolicyTests
{
    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public void GatedOffEndpoint_Blocks(int status)
    {
        var result = VerificationPreconditionPolicy.Evaluate(
            new VerificationPreconditionObservation("db-touch", status, false, $"http={status}"));

        Assert.False(result.Ok);
        Assert.Equal(PreconditionSeverity.Blocking, result.Severity);
        Assert.NotNull(result.Error);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(VerificationPreconditionPolicy.NoResponse)]
    [InlineData(VerificationPreconditionPolicy.NotEvaluated)]
    public void TransientOrUnreadableState_IsAdvisoryOnly(int status)
    {
        var result = VerificationPreconditionPolicy.Evaluate(
            new VerificationPreconditionObservation("db-touch", status, false,
                VerificationPreconditionPolicy.DescribeStatus(status)));

        Assert.False(result.Ok);
        Assert.Equal(PreconditionSeverity.Advisory, result.Severity);
        Assert.Empty(VerificationPreconditionPolicy.BlockingErrors(new[] { result }));
    }

    [Fact]
    public void SatisfiedStep_ProducesNoError()
    {
        var result = VerificationPreconditionPolicy.Evaluate(
            new VerificationPreconditionObservation("db-touch", 200, true, "http=200, sentinel round-tripped"));

        Assert.True(result.Ok);
        Assert.Null(result.Error);
        Assert.Empty(VerificationPreconditionPolicy.BlockingErrors(new[] { result }));
    }

    [Fact]
    public void GatedOffSentinel_NamesTheFlagTheOperatorHasToSet()
    {
        // The whole point of moving the check earlier: the refusal has to be
        // actionable without opening a run folder.
        var result = VerificationPreconditionPolicy.Evaluate(
            new VerificationPreconditionObservation("db-touch", 404, false, "http=404"));

        var error = Assert.Single(VerificationPreconditionPolicy.BlockingErrors(new[] { result }));
        Assert.Contains("db-touch", error);
        Assert.Contains("POST /api/_internal/probe", error);
        Assert.Contains("http=404", error);
        Assert.Contains("UpdateService:ProbeEnabled=true", error);
        Assert.Contains("appsettings.Local.json", error);
        Assert.Contains("stable-approved-tag", error);
    }

    [Fact]
    public void BlockingErrors_KeepsAdvisoryFailuresOutOfTheRefusal()
    {
        var results = VerificationPreconditionPolicy.Evaluate(new[]
        {
            new VerificationPreconditionObservation("healthz-stable", 200, true, "http=200"),
            new VerificationPreconditionObservation("jobs-grouped", 503, false, "http=503"),
            new VerificationPreconditionObservation("db-touch", 404, false, "http=404"),
            new VerificationPreconditionObservation(
                VerificationPreconditionPolicy.FrontendListeningStep,
                VerificationPreconditionPolicy.NoResponse, false, "no loopback origin answered on port 4011"),
        });

        var blocking = VerificationPreconditionPolicy.BlockingErrors(results);
        Assert.Single(blocking);
        Assert.Contains("db-touch", blocking[0]);

        // The advisory rows still travel, so the operator sees them.
        Assert.Contains(results, r => r.Step == "jobs-grouped" && !r.Ok && r.Severity == PreconditionSeverity.Advisory);
        Assert.Contains(results, r => r.Step == VerificationPreconditionPolicy.FrontendListeningStep && !r.Ok);
    }

    [Fact]
    public void FrontendDown_NeverRefusesARun()
    {
        // AGT-2862 keeps its shape: a frontend-only failure is `degraded`,
        // not `failed`, so it must not become a preflight refusal either.
        var result = VerificationPreconditionPolicy.Evaluate(
            new VerificationPreconditionObservation(
                VerificationPreconditionPolicy.FrontendListeningStep,
                VerificationPreconditionPolicy.NoResponse, false, "no loopback origin answered on port 4011"));

        Assert.Equal(PreconditionSeverity.Advisory, result.Severity);
        Assert.Empty(VerificationPreconditionPolicy.BlockingErrors(new[] { result }));
    }

    [Fact]
    public void EverySixStepMatrixStepDeclaresAPrecondition()
    {
        // The guard against the two lists drifting apart: a new verification
        // step without a declared precondition silently reopens the incident.
        var declared = VerificationPreconditionPolicy.Steps.Select(s => s.Step).ToArray();

        Assert.Equal(
            new[] { "healthz-stable", "runner-status", "jobs-grouped", "clients", "cli-quota", "db-touch" },
            declared.Take(6).ToArray());
        Assert.Equal(VerificationPreconditionPolicy.FrontendListeningStep, declared[^1]);
        Assert.All(VerificationPreconditionPolicy.Steps, spec =>
        {
            Assert.False(string.IsNullOrWhiteSpace(spec.Probe));
            Assert.False(string.IsNullOrWhiteSpace(spec.Requirement));
            Assert.False(string.IsNullOrWhiteSpace(spec.Remedy));
        });
    }

    [Fact]
    public void UnreachableInstance_LeavesEveryOtherStepNotEvaluated()
    {
        var observation = VerificationPreconditionPolicy.NotEvaluatedObservation(
            "db-touch", "the running backend did not answer /healthz");
        var result = VerificationPreconditionPolicy.Evaluate(observation);

        Assert.Contains("not evaluated", result.Observed);
        Assert.Equal(PreconditionSeverity.Advisory, result.Severity);
    }
}

/// <summary>
/// AGT-2865 end-to-end: the preflight has to answer the sentinel question
/// against the *running* instance, before the stack is touched.
///
/// The incident (run 0649a4e0, 17.09.2026, v0.6.0 -&gt; v0.7.0) reported
/// <c>allowed=true, direction=Upgrade, errors=[]</c>, then stopped the stack,
/// restored, rebuilt, restarted, passed five of six checks, and failed
/// db-touch with <c>http=404</c> because the sentinel was gated off on a
/// default Stable. These cases pin the three halves of the fix: the preflight
/// refuses with a named flag, an answering sentinel is still allowed, and the
/// post-restart matrix is unchanged - all six checks still run.
///
/// Serial collection for the same reason as the restart identity drill: the
/// cases fork git and bash against their own temp checkout and read
/// wall-clock budgets.
/// </summary>
[Collection(UpdateServiceSerialCollection.Name)]
public class UpdateServicePreflightPreconditionTests
{
    private const int TriggerTimeoutMs = 180_000;
    private const string PreviousVersion = "0.6.0";
    private const string CandidateVersion = "0.7.0";
    private const string CandidateTag = "v" + CandidateVersion;

    [SkippableFact]
    public async Task Preflight_RefusesWhenTheSentinelIsGatedOff_AndNamesTheFlag()
    {
        using var checkout = FakeStableCheckout.TryCreate();
        Skip.If(checkout == null, "git and/or bash are not available on PATH; this drill needs both.");

        await using var backend = await BootStableAsync(checkout!);
        backend.ProbeReturns404 = true;

        using var factory = NewFactory(checkout!, backend);
        var client = factory.CreateClient();

        var preflight = await GetJsonAsync(client, "/update/preflight");

        Assert.False(preflight.GetProperty("allowed").GetBoolean(),
            $"preflight allowed an update whose db-touch step cannot pass: {preflight}");

        var errors = preflight.GetProperty("errors").EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
        var sentinelError = Assert.Single(errors, e => e.Contains("db-touch", StringComparison.Ordinal));
        Assert.Contains("http=404", sentinelError);
        Assert.Contains("UpdateService:ProbeEnabled=true", sentinelError);
        Assert.Contains("appsettings.Local.json", sentinelError);

        // The per-step verdicts travel too, so the Update Center can render
        // which step refused rather than only the flattened message.
        var steps = preflight.GetProperty("verificationPreconditions").EnumerateArray()
            .ToDictionary(p => p.GetProperty("step").GetString()!, p => p);
        Assert.Equal(7, steps.Count);
        Assert.False(steps["db-touch"].GetProperty("ok").GetBoolean());
        Assert.Equal("Blocking", steps["db-touch"].GetProperty("severity").GetString());
        Assert.True(steps["healthz-stable"].GetProperty("ok").GetBoolean());
        Assert.True(steps["clients"].GetProperty("ok").GetBoolean());
    }

    [SkippableFact]
    public async Task Preflight_AllowsWhenTheSentinelAnswers()
    {
        using var checkout = FakeStableCheckout.TryCreate();
        Skip.If(checkout == null, "git and/or bash are not available on PATH; this drill needs both.");

        await using var backend = await BootStableAsync(checkout!);

        using var factory = NewFactory(checkout!, backend);
        var client = factory.CreateClient();

        var preflight = await GetJsonAsync(client, "/update/preflight");

        Assert.True(preflight.GetProperty("allowed").GetBoolean(),
            $"preflight refused a runnable upgrade: {preflight.GetProperty("errors")}");
        Assert.Equal("Upgrade", preflight.GetProperty("direction").GetString());

        var steps = preflight.GetProperty("verificationPreconditions").EnumerateArray().ToArray();
        Assert.All(steps.Where(s => s.GetProperty("step").GetString() != VerificationPreconditionPolicy.FrontendListeningStep),
            step => Assert.True(step.GetProperty("ok").GetBoolean(),
                $"{step.GetProperty("step").GetString()} precondition failed: {step.GetProperty("observed").GetString()}"));
    }

    [SkippableFact]
    public async Task Trigger_WithAGatedOffSentinel_FailsInPreflight_WithoutStoppingTheStack()
    {
        using var checkout = FakeStableCheckout.TryCreate();
        Skip.If(checkout == null, "git and/or bash are not available on PATH; this drill needs both.");

        var headBefore = checkout!.ReadStableHead();
        await using var backend = await BootStableAsync(checkout);
        backend.ProbeReturns404 = true;

        using var factory = NewFactory(checkout, backend);
        var client = factory.CreateClient();

        using var trigger = await client.PostAsJsonAsync("/update/trigger", new { Force = false });
        trigger.EnsureSuccessStatusCode();
        var status = await WaitForPhaseAsync(client, new[] { "done", "failed", "degraded", "idle" }, TriggerTimeoutMs);

        Assert.Equal("failed", status.GetProperty("phase").GetString());
        Assert.Contains("db-touch", status.GetProperty("message").GetString()!);
        Assert.Contains("UpdateService:ProbeEnabled=true", status.GetProperty("message").GetString()!);

        // The whole point: the stack was never touched. In the incident the
        // same failure arrived after stop + restore + build + restart, and
        // then drove the rollback path under an already-running new backend.
        Assert.False(checkout.StopRan(), "the run stopped the stack despite refusing in the preflight");
        Assert.False(checkout.StartRan(), "the run restarted the stack despite refusing in the preflight");
        Assert.Equal(headBefore, checkout.ReadStableHead());

        // The refusal is readable from the run folder on its own.
        var runFolder = LatestRunFolder(checkout.RunsDir);
        var preconditions = File.ReadAllText(
            Path.Combine(runFolder, UpdateOrchestrator.VerificationPreconditionsFileName));
        Assert.Contains("db-touch", preconditions);
        Assert.Contains("Blocking", preconditions);
    }

    [SkippableFact]
    public async Task SatisfiedPreconditions_StillRunAllSixPostRestartChecks()
    {
        using var checkout = FakeStableCheckout.TryCreate();
        Skip.If(checkout == null, "git and/or bash are not available on PATH; this drill needs both.");

        await using var backend = await BootStableAsync(checkout!);

        using var factory = NewFactory(checkout!, backend);
        var client = factory.CreateClient();

        using var trigger = await client.PostAsJsonAsync("/update/trigger", new { Force = false });
        trigger.EnsureSuccessStatusCode();
        var status = await WaitForPhaseAsync(client, new[] { "done", "failed", "degraded" }, TriggerTimeoutMs);
        Assert.True(status.GetProperty("phase").GetString() == "done",
            $"expected the upgrade to complete, got {status.GetProperty("phase").GetString()}; " +
            $"message={status.GetProperty("message").GetString()}");

        // The preflight gate does not replace the matrix: all six checks still
        // run after the restart, in order, against the restarted process.
        var runFolder = LatestRunFolder(checkout!.RunsDir);
        var rows = File.ReadAllLines(Path.Combine(runFolder, "verification.jsonl"))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonDocument.Parse(l).RootElement)
            .ToArray();
        Assert.Equal(
            new[] { "healthz-stable", "runner-status", "jobs-grouped", "clients", "cli-quota", "db-touch" },
            rows.Select(r => r.GetProperty("step").GetString()).ToArray());
        Assert.All(rows, r => Assert.True(r.GetProperty("ok").GetBoolean()));

        // db-touch ran twice in total: once as a precondition before the run,
        // once as the post-restart check.
        Assert.True(backend.ProbeCallCount >= 2,
            $"expected the sentinel to be hit by both the preflight and the matrix, saw {backend.ProbeCallCount} call(s)");
    }

    /// <summary>
    /// The branch pipeline has no release gate to hang the preconditions off,
    /// so the orchestrator evaluates them directly in phase 1. Same rule, same
    /// refusal, same untouched stack.
    /// </summary>
    [SkippableFact]
    public async Task BranchPipeline_WithAGatedOffSentinel_AlsoRefusesBeforeStopping()
    {
        using var checkout = FakeStableCheckout.TryCreate();
        Skip.If(checkout == null, "git and/or bash are not available on PATH; this drill needs both.");

        await using var backend = new FakeBackendHarness { ProbeReturns404 = true };
        await backend.StartAsync();
        checkout!.AdvanceOriginMain();

        using var factory = new UpdateServiceTestFactory(checkout, backend,
            autoRollback: false,
            healthWaitSeconds: 30,
            requireReleaseManifest: false,
            frontendUrl: backend.BaseUrl,
            frontendWaitSeconds: 30,
            restartHealthWaitSeconds: 30);
        var client = factory.CreateClient();

        using var trigger = await client.PostAsJsonAsync("/update/trigger", new { Force = false });
        trigger.EnsureSuccessStatusCode();
        var status = await WaitForPhaseAsync(client, new[] { "done", "failed", "degraded", "idle" }, TriggerTimeoutMs);

        Assert.Equal("failed", status.GetProperty("phase").GetString());
        Assert.Contains("verification preconditions refused", status.GetProperty("message").GetString()!);
        Assert.Contains("UpdateService:ProbeEnabled=true", status.GetProperty("message").GetString()!);
        Assert.False(checkout.StopRan(), "the run stopped the stack despite refusing in the preflight");
        Assert.False(checkout.StartRan(), "the run restarted the stack despite refusing in the preflight");
    }

    // ─── harness helpers ────────────────────────────────────────────────────

    /// <summary>
    /// The incident's starting position: v0.6.0 installed and running,
    /// v0.7.0 published and approved as the candidate.
    /// </summary>
    private static async Task<FakeBackendHarness> BootStableAsync(FakeStableCheckout checkout)
    {
        var installedManifest = ManifestJson(PreviousVersion, checkout.ReadStableHead());
        var candidateCommit = checkout.PublishReleaseCandidate(CandidateTag, ProjectDefinitionYaml);
        checkout.InstallManifest(installedManifest);
        checkout.PublishCandidateManifest(ManifestJson(CandidateVersion, candidateCommit), CandidateTag);
        checkout.BootBackendWith(installedManifest);

        var backend = new FakeBackendHarness { RuntimeIdentityFile = checkout.RuntimeIdentityFile };
        await backend.StartAsync();
        return backend;
    }

    /// <summary>
    /// The frontend origin points at the fake backend's port, so the frontend
    /// check is satisfied and every assertion here is about the backend
    /// sentinel rather than about a dev server this fixture does not run.
    /// </summary>
    private static UpdateServiceTestFactory NewFactory(FakeStableCheckout checkout, FakeBackendHarness backend)
        => new(checkout, backend,
            autoRollback: false,
            healthWaitSeconds: 30,
            requireReleaseManifest: true,
            frontendUrl: backend.BaseUrl,
            frontendWaitSeconds: 30,
            restartHealthWaitSeconds: 30);

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

    private static string LatestRunFolder(string runsRoot)
    {
        Assert.True(Directory.Exists(runsRoot), $"runs root does not exist: {runsRoot}");
        return new DirectoryInfo(runsRoot).GetDirectories()
            .OrderByDescending(d => d.CreationTimeUtc).First().FullName;
    }

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
          "builtAt": "2026-09-17T10:00:00+00:00",
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
