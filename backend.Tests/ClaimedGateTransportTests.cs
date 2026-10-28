using System.Diagnostics;
using System.Security.Cryptography;
using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// DP-S4: merge and promotion gates as claimed verification subjects with an
/// immutable bundle, the bridge/claimed switch, and evidence equivalence.
/// </summary>
public sealed class ClaimedGateTransportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "claimed-gate-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Fixed = new DateTime(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-10-28T09:00:00Z"));

    [Theory]
    [InlineData("bridge", "merge-into-develop-build-gate", "PROJ-001", false, GateTransportRoute.Bridge, "bridge-default")]
    [InlineData("canary", "merge-into-develop-build-gate", "PROJ-001", false, GateTransportRoute.Claimed, "claimed-canary")]
    [InlineData("canary", "merge-into-main", "PROJ-002", true, GateTransportRoute.Bridge, "outside-canary")]
    [InlineData("claimed", "merge-into-main", "PROJ-002", false, GateTransportRoute.Bridge, "awaiting-canary-pass")]
    [InlineData("claimed", "merge-into-main", "PROJ-001", false, GateTransportRoute.Claimed, "claimed-canary")]
    [InlineData("claimed", "merge-into-main", "PROJ-002", true, GateTransportRoute.Claimed, "claimed")]
    [InlineData("claimed", "batch-base", null, true, GateTransportRoute.Claimed, "claimed")]
    [InlineData("claimed", "post-build-test-gate", "PROJ-001", true, GateTransportRoute.Bridge, "gate-not-claimable")]
    public void Transport_switch_keeps_the_bridge_until_a_canary_passes(string mode, string gateId,
        string? project, bool canaryPassed, GateTransportRoute route, string reason)
    {
        var decision = GateTransportSelectionPolicy.Decide(mode, gateId, project, ["proj-001"], canaryPassed);

        Assert.Equal(route, decision.Route);
        Assert.Equal(reason, decision.Reason);
    }

    [Theory]
    [InlineData("claimed", true, 0, true)]
    [InlineData("claimed", true, 1, false)]
    [InlineData("claimed", false, 0, false)]
    [InlineData("canary", true, 0, false)]
    [InlineData("bridge", true, 0, false)]
    public void Bridge_default_retires_only_after_the_ssh_drain_and_a_canary_pass(string mode,
        bool canaryPassed, int sshInFlight, bool ready)
        => Assert.Equal(ready, GateTransportSelectionPolicy.BridgeRetirementReady(mode, canaryPassed, sshInFlight));

    [Fact]
    public void Transport_mode_fails_closed_for_unknown_values_and_without_a_Task_Server()
    {
        static IConfiguration Config(string? value) => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [GateTransportSelectionPolicy.ModeKey] = value })
            .Build();

        Assert.Equal("bridge", GateTransportSelectionPolicy.Mode(Config(null), taskServerConfigured: false));
        Assert.Equal("claimed", GateTransportSelectionPolicy.Mode(Config("Claimed"), taskServerConfigured: true));
        Assert.Throws<InvalidOperationException>(() => GateTransportSelectionPolicy.Mode(Config("ssh"), true));
        Assert.Throws<InvalidOperationException>(() => GateTransportSelectionPolicy.Mode(Config("canary"), false));
    }

    [Fact]
    public async Task Merge_gate_freezes_the_exact_sha_into_a_bundle_and_reports_bridge_equivalent_evidence()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (repository, sha) = CreateRepository();
        var authority = new FakeAuthority(_clock);
        var ledger = new ClaimedGateCanaryLedger(Path.Combine(_root, "ledger"));
        var transport = new ClaimedGateTransport(authority, ledger, _clock,
            NullLogger<ClaimedGateTransport>.Instance) { PollInterval = TimeSpan.Zero };

        var result = await transport.RunAsync(Request(repository, sha, "merge-into-develop-build-gate"), null,
            new BuildProfile { BuildCmds = ["true"] }, PostStepMode.Fail, TimeSpan.FromMinutes(10),
            CancellationToken.None);

        var created = Assert.Single(authority.Created);
        Assert.Equal(GateSubjectKinds.Merge, created.Kind);
        Assert.Equal(sha, created.ExpectedSha);
        Assert.Null(created.RepositoryUrl);
        Assert.Equal(authority.UploadedSha256, created.Bundle!.Sha256);
        Assert.Equal(authority.UploadedBytes, created.Bundle.SizeBytes);
        Assert.Equal(GateRules.PlanHash(created.Plan), created.PlanHash);
        Assert.Equal("bash", Assert.Single(created.Plan.Commands).FileName);
        Assert.Equal(BuildTestGateVerdict.Ok, result.Verdict);
        Assert.Equal(sha, result.TestedSha);
        Assert.Equal("merge-into-develop-build-gate", result.GateId);
        Assert.Empty(ClaimedGateEvidence.MissingFields(result));
        Assert.Contains(created.Bundle.Sha256, result.Reason, StringComparison.Ordinal);
        Assert.True(ledger.Passed);
        Assert.True(new ClaimedGateCanaryLedger(Path.Combine(_root, "ledger")).Passed);
    }

    [Fact]
    public async Task Cancelled_claimed_gate_cancels_its_subject_so_the_grant_is_released()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (repository, sha) = CreateRepository();
        var authority = new FakeAuthority(_clock) { NeverFinishes = true };
        var transport = new ClaimedGateTransport(authority,
            new ClaimedGateCanaryLedger(Path.Combine(_root, "ledger")), _clock,
            NullLogger<ClaimedGateTransport>.Instance) { PollInterval = TimeSpan.Zero };
        using var cancel = new CancellationTokenSource();
        authority.Polled = () => cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.RunAsync(
            Request(repository, sha, "merge-into-main"), null, new BuildProfile { BuildCmds = ["true"] },
            PostStepMode.Fail, TimeSpan.FromMinutes(10), cancel.Token));

        Assert.Equal(GateSubjectKinds.Promotion, Assert.Single(authority.Created).Kind);
        Assert.Single(authority.Cancelled);
    }

    [Fact]
    public void Bridge_and_claimed_results_fill_the_same_evidence_fields()
    {
        var bridge = RemoteGateTransport.AcceptResponse(new RemoteGateResponse(1, "run-1", new string('a', 64),
            new BuildTestGateResult(BuildTestGateVerdict.Ok, 0, 10, "", "ok", true, false)
            {
                GateId = "merge-into-main", GateRunId = "gate-run", ExpectedSha = new string('1', 40),
                TestedSha = new string('1', 40), GateStartedAtUtc = _clock.GetUtcNow(),
                GateCompletedAtUtc = _clock.GetUtcNow(), GateProfileDigest = "profile",
                PipelineDefinitionVersion = 3, ToolchainIdentity = "dotnet-10",
            }), "run-1", new string('a', 64), "kori-gate", "merge", 10, TimeSpan.FromSeconds(1));
        var subject = new GateSubject("gsub_1", "job", "promotion:1", "product", null, new string('1', 40),
            null, null, null, new string('2', 64), "policy", "3", new string('3', 64),
            new GatePlan("post-build-test-gate", 1, [new GateCommand("build-1", "bash", ["-lc", "true"], 30)],
                "", 30, [], 1024, "always"), Fixed, Fixed, 2, Kind: GateSubjectKinds.Promotion);
        var attempt = new GateAttempt("gat_1", "gsub_1", 1, GateStates.Passed, "gate-a", "host-a", null,
            GateStates.Passed, Fixed, Fixed, Fixed, Fixed, 4, null);
        var report = new GateReport(GateStates.Passed, null, new string('1', 40), new string('4', 40), false, false,
            [new GateCommandEvidence("build-1", 0, false, new string('5', 64), "ok", Fixed, Fixed)],
            "host-a", "dotnet-10", [], [], "clean");
        var claimed = ClaimedGateEvidence.Result(new GateStatus(subject, [attempt], report, 2, null,
            GateStates.Passed, 1, null, report.TestedSha, GateStates.Passed, true), "merge-into-main",
            "claimed promotion gate", new string('1', 40), _clock.GetUtcNow(), _clock.GetUtcNow());

        Assert.Empty(ClaimedGateEvidence.MissingFields(bridge));
        Assert.Empty(ClaimedGateEvidence.MissingFields(claimed));
        Assert.Equal(bridge.Verdict, claimed.Verdict);
        Assert.Equal(bridge.TestedSha, claimed.TestedSha);
        Assert.Equal("gate:host-a/gat_1#4", claimed.Executor);
    }

    private BuildTestGateRequest Request(string repository, string sha, string gateId)
        => new(repository, sha, "merge") { GateId = gateId, Project = "PROJ-001", JobId = "job-1" };

    private (string Repository, string Sha) CreateRepository()
    {
        var repository = Path.Combine(_root, "source");
        Directory.CreateDirectory(repository);
        Git(repository, "init", "-b", "develop");
        Git(repository, "config", "user.email", "gate@example.invalid");
        Git(repository, "config", "user.name", "Gate Test");
        File.WriteAllText(Path.Combine(repository, "README.md"), "exact candidate\n");
        Git(repository, "add", "README.md");
        Git(repository, "commit", "-m", "candidate");
        var sha = Git(repository, "rev-parse", "HEAD").Trim();
        // A later commit must not leak into the frozen bundle.
        File.WriteAllText(Path.Combine(repository, "README.md"), "moved on\n");
        Git(repository, "commit", "-am", "later");
        return (repository, sha);
    }

    private static string Git(string cwd, params string[] args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true,
            },
        };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException(error);
        return output;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>A gate authority whose host claims, proves and passes each subject.</summary>
    private sealed class FakeAuthority(TimeProvider clock) : IBatchGateSubjectAuthority
    {
        private GateSubject? _subject;
        public List<CreateGateSubjectRequest> Created { get; } = [];
        public List<string> Cancelled { get; } = [];
        public string? UploadedSha256 { get; private set; }
        public long UploadedBytes { get; private set; }
        public bool NeverFinishes { get; init; }
        public Action? Polled { get; set; }
        public string Name => "fake";

        public async Task<GateBundleLocator> UploadBundleAsync(string repositoryId, string path, string sha256,
            long sizeBytes, TimeSpan budget, CancellationToken ct)
        {
            var bytes = await File.ReadAllBytesAsync(path, ct);
            Assert.Equal(sizeBytes, bytes.LongLength);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), sha256);
            UploadedSha256 = sha256;
            UploadedBytes = sizeBytes;
            return new GateBundleLocator(sha256, sizeBytes, GateBundleLimits.Locator(sha256));
        }

        public Task<GateStatus> CreateAsync(CreateGateSubjectRequest request, CancellationToken ct)
        {
            GateRules.ValidateSubject(request);
            Created.Add(request);
            _subject = new GateSubject("gsub_1", request.TaskId, request.SourceRunId, request.RepositoryId,
                request.RepositoryUrl, request.ExpectedSha, request.ResultRef, null, null, request.PlanHash,
                request.PolicyHash, request.PipelineDefinitionVersion, request.TestSelectionAuditDigest,
                request.Plan, clock.GetUtcNow().UtcDateTime, request.DispatchDeadline, request.MaxAttempts,
                Kind: request.Kind, Bundle: request.Bundle);
            return Task.FromResult(Status(GateStates.Queued, null));
        }

        public Task<GateStatus?> GetAsync(string subjectId, CancellationToken ct)
        {
            Polled?.Invoke();
            ct.ThrowIfCancellationRequested();
            if (NeverFinishes) return Task.FromResult<GateStatus?>(Status(GateStates.Running, null));
            var proof = new GateSubjectProof(_subject!.PlanHash, _subject.PolicyHash, _subject.ExpectedSha,
                _subject.Bundle!.Sha256, _subject.Bundle.SizeBytes);
            var report = new GateReport(GateStates.Passed, null, _subject.ExpectedSha, new string('4', 40),
                false, false, _subject.Plan.Commands.Select(command => new GateCommandEvidence(command.StepId,
                    0, false, new string('5', 64), "ok", Fixed, Fixed)).ToArray(),
                "host-a", "dotnet-10", [], [], "clean", proof);
            Assert.Null(GateRules.ProofViolation(_subject, report));
            return Task.FromResult<GateStatus?>(Status(GateStates.Passed, report));
        }

        public Task CancelAsync(string subjectId, CancellationToken ct)
        {
            Cancelled.Add(subjectId);
            return Task.CompletedTask;
        }

        private GateStatus Status(string phase, GateReport? report)
            => new(_subject!, [new GateAttempt("gat_1", "gsub_1", 1, phase, "gate-a", "host-a", null,
                    report is null ? null : phase, Fixed, Fixed, null, null, 1, null)],
                report, 1, "host-a", phase, 1, null, report?.TestedSha,
                GateStates.IsTerminal(phase) ? phase : null, report is not null);
    }
}
