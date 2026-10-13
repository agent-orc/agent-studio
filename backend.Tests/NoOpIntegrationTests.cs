using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3103 - a delivery the published integration branch already carries is
/// a no-op integration: it completes without a build, a test run, or a push,
/// names the SHA that proved containment, and releases the integration
/// reservation at once. On 13.10.2026 such re-integrations each ran a full
/// 60 to 75 minute merge gate on the shared gate host while two formed batches
/// waited behind the reservation.
/// </summary>
public sealed class NoOpIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public NoOpIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "no-op-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A locked fixture file only leaks a temp directory.
            Trace.WriteLine($"NoOpIntegrationTests cleanup: {ex.Message}");
        }
    }

    [Fact]
    public async Task ContainedDelivery_IntegratesWithoutAGateRunOrAPush_AndReleasesTheReservation()
    {
        var (repo, remote) = SeedRepoWithOrigin("contained-on-origin");
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "checkout -q -b task/61");
        Directory.CreateDirectory(Path.Combine(repo, "backend"));
        File.WriteAllText(Path.Combine(repo, "backend", "Rule.cs"), "public class Rule {}\n");
        Commit(repo, "feat: delivered code");
        RunGit(repo, "checkout -q develop");
        RunGit(repo, "merge -q --no-ff -m \"merge: operator fast path\" task/61");
        RunGit(repo, "push -q -u origin develop");
        var published = RemoteSha(remote, "develop");

        var harness = Harness(repo, "61");
        var outcome = await harness.Runner.RunAsync(
            "Fixture", "61", harness.JobFolder, repo, "develop", CancellationToken.None);

        Assert.Equal(MergeIntoIntegrationOutcome.AlreadyMerged, outcome.Outcome);
        Assert.Equal(0, harness.Gate.Invocations);
        Assert.False(harness.Queue.Reader.TryRead(out _));
        Assert.Equal(published, outcome.ContainmentSha);
        Assert.Equal(published, outcome.MergedSha);
        Assert.True(outcome.VerificationPending);
        Assert.Equal(published, RemoteSha(remote, "develop"));

        var merge = ReadStep(harness.Log, harness.JobFolder, PipelineCatalogue.MergeIntoDevelopStepId);
        Assert.Equal(PipelineStepStatus.Passed, merge!.Status);
        Assert.Null(merge.ApprovedIntegrationSha);
        Assert.Contains($"Containment proven by {published[..7]}", merge.Reason);
        Assert.Contains("no gate ran and nothing was pushed", merge.Reason);
        var push = ReadStep(harness.Log, harness.JobFolder, PipelineCatalogue.MergeIntoDevelopPushStepId);
        Assert.Equal("already-remote", push!.Verdict);

        var verification = IntegrationVerificationStore.Read(harness.JobFolder);
        Assert.Equal(IntegrationVerificationStates.Unverified, verification!.State);
        Assert.Equal(published, verification.ContainmentSha);
        Assert.False(verification.GateFailed);

        // The reservation is free the moment the call returns, and no push
        // marker keeps a formed batch waiting.
        Assert.Null(harness.Reservations.GetWait("Fixture"));
        using var grant = harness.Reservations.TryAcquire("Fixture");
        Assert.NotNull(grant);
    }

    [Fact]
    public async Task NoOpMergeResult_SkipsTheGate_WhenTheMergeWouldEqualTheIntegrationTip()
    {
        // A repository without origin: the local branch is the published state.
        var repo = SeedRepo("no-op-local-only");
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "checkout -q -b task/62");
        Directory.CreateDirectory(Path.Combine(repo, "frontend"));
        File.WriteAllText(Path.Combine(repo, "frontend", "view.ts"), "export const view = 1;\n");
        Commit(repo, "feat: delivered view");
        RunGit(repo, "checkout -q develop");
        RunGit(repo, "merge -q --no-ff -m \"merge: already on develop\" task/62");
        var tip = RunGit(repo, "rev-parse develop").Out.Trim();

        var harness = Harness(repo, "62");
        var outcome = await harness.Runner.RunAsync(
            "Fixture", "62", harness.JobFolder, repo, "develop", CancellationToken.None);

        Assert.Equal(MergeIntoIntegrationOutcome.AlreadyMerged, outcome.Outcome);
        Assert.Equal(0, harness.Gate.Invocations);
        Assert.Equal(tip, outcome.ContainmentSha);
        Assert.Equal(tip, RunGit(repo, $"rev-parse {GitService.IntegrationLaneRef("develop")}").Out.Trim());
        Assert.False(harness.Queue.Reader.TryRead(out _));
        Assert.Equal("no-remote",
            ReadStep(harness.Log, harness.JobFolder, PipelineCatalogue.MergeIntoDevelopPushStepId)!.Verdict);
        using var grant = harness.Reservations.TryAcquire("Fixture");
        Assert.NotNull(grant);
    }

    [Fact]
    public async Task RealMerge_StillRunsTheGate_AndHoldsThePushReservation()
    {
        var (repo, _) = SeedRepoWithOrigin("real-merge");
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "push -q -u origin develop");
        RunGit(repo, "checkout -q -b task/63");
        Directory.CreateDirectory(Path.Combine(repo, "backend"));
        File.WriteAllText(Path.Combine(repo, "backend", "Rule.cs"), "public class Rule {}\n");
        Commit(repo, "feat: new code");
        RunGit(repo, "checkout -q develop");

        var harness = Harness(repo, "63");
        var outcome = await harness.Runner.RunAsync(
            "Fixture", "63", harness.JobFolder, repo, "develop", CancellationToken.None);

        Assert.Equal(MergeIntoIntegrationOutcome.Merged, outcome.Outcome);
        Assert.Equal(1, harness.Gate.Invocations);
        Assert.Equal(outcome.MergedSha, harness.Gate.Request!.ExpectedSha);
        Assert.Null(outcome.ContainmentSha);
        Assert.False(outcome.VerificationPending);
        Assert.True(harness.Queue.Reader.TryRead(out var push));
        Assert.Equal(outcome.MergedSha, push!.ApprovedSha);
        Assert.Equal(outcome.MergedSha, harness.Reservations.GetWait("Fixture")?.ApprovedSha);
    }

    [Fact]
    public async Task NoOpCarryingAnEarlierVerifiedTree_IsVerifiedWithoutAGate()
    {
        var (repo, remote) = SeedRepoWithOrigin("carried-tree");
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "checkout -q -b task/64");
        Directory.CreateDirectory(Path.Combine(repo, "backend"));
        File.WriteAllText(Path.Combine(repo, "backend", "Rule.cs"), "public class Rule {}\n");
        Commit(repo, "feat: delivered code");
        RunGit(repo, "checkout -q develop");
        RunGit(repo, "merge -q --no-ff -m \"merge: gated earlier\" task/64");
        var gatedTree = RunGit(repo, "rev-parse develop").Out.Trim();
        File.WriteAllText(Path.Combine(repo, "later.txt"), "another card\n");
        Commit(repo, "feat: a later card moved the tip");
        RunGit(repo, "push -q -u origin develop");
        var published = RemoteSha(remote, "develop");

        var harness = Harness(repo, "64");
        IntegrationVerificationStore.Write(harness.JobFolder, new IntegrationVerificationRecord
        {
            State = IntegrationVerificationStates.Verified,
            Sha = gatedTree,
            IntegrationBranch = "develop",
            Evidence = IntegrationVerificationEvidence.MergeGate,
            GateVerdict = nameof(BuildTestGateVerdict.Ok),
            Reason = "The integration lane created this merge and its gate returned Ok.",
            RecordedAtUtc = DateTimeOffset.Parse("2026-10-13T02:00:00Z"),
        });

        var outcome = await harness.Runner.RunAsync(
            "Fixture", "64", harness.JobFolder, repo, "develop", CancellationToken.None);

        Assert.Equal(MergeIntoIntegrationOutcome.AlreadyMerged, outcome.Outcome);
        Assert.Equal(0, harness.Gate.Invocations);
        Assert.False(outcome.VerificationPending);
        Assert.Equal(published, outcome.ContainmentSha);
        var verification = IntegrationVerificationStore.Read(harness.JobFolder);
        Assert.Equal(IntegrationVerificationStates.Verified, verification!.State);
        Assert.Equal(IntegrationVerificationEvidence.CarriedTree, verification.Evidence);
        Assert.Equal(gatedTree, verification.Sha);
        Assert.Equal(published, verification.ContainmentSha);
    }

    private sealed record RunnerHarness(
        MergeIntoDevelopRunner Runner,
        PipelineExecutionLog Log,
        CountingGateRunner Gate,
        IntegrationPushQueue Queue,
        BatchIntegrationReservationService Reservations,
        string JobFolder);

    private RunnerHarness Harness(string repo, string jobId)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "Fixture",
                ["WatchPaths:0:RootPath"] = repo,
                ["WatchPaths:0:RepositoryPath"] = repo,
                ["WatchPaths:0:Path"] = Path.Combine(repo, ".orchestrator", "jobs"),
                ["TaskRepository"] = Path.Combine(_tempDir, "settings-store"),
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var log = new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        // Declared build commands: a gate applies to every merge, so a gate
        // that does not run was skipped by the no-op rule, not by scope.
        settings.SetBuildProfile("Fixture", new BuildProfile { BuildCmds = ["cd ."] });
        var gate = new CountingGateRunner();
        var queue = new IntegrationPushQueue();
        var reservations = new BatchIntegrationReservationService();
        var runner = new MergeIntoDevelopRunner(
            git,
            log,
            NullLogger<MergeIntoDevelopRunner>.Instance,
            pushQueue: queue,
            projectSettings: settings,
            preDevelopBuildGate: new PreDevelopBuildGate(gate),
            batchReservations: reservations);

        // Outside the repository, as in production, so the tree stays clean.
        var jobFolder = Path.Combine(_tempDir, "jobs", jobId);
        Directory.CreateDirectory(jobFolder);
        log.Begin(jobFolder, PipelineCatalogue.Standard, "Fixture", jobId);
        return new RunnerHarness(runner, log, gate, queue, reservations, jobFolder);
    }

    private sealed class CountingGateRunner : IBuildTestGateRunner
    {
        public int Invocations { get; private set; }
        public BuildTestGateRequest? Request { get; private set; }

        public Task<BuildTestGateResult> RunAsync(
            BuildTestGateRequest request,
            IReadOnlyList<string>? changedFiles,
            BuildProfile? profile,
            PostStepMode mode,
            TimeSpan timeout,
            CancellationToken ct)
        {
            Invocations++;
            Request = request;
            return Task.FromResult(new BuildTestGateResult(
                BuildTestGateVerdict.Ok, 0, 20, string.Empty, "gate passed", true, false)
            {
                ExpectedSha = request.ExpectedSha,
                TestedSha = request.ExpectedSha,
            });
        }
    }

    private static PipelineStepExecution? ReadStep(PipelineExecutionLog log, string jobFolder, string stepId)
        => log.Read(jobFolder)?.Steps.LastOrDefault(step => step.StepId == stepId);

    private (string Repo, string Remote) SeedRepoWithOrigin(string name)
    {
        var remote = Path.Combine(_tempDir, name + "-origin.git");
        RunGit(_tempDir, $"init --bare -q --initial-branch=main \"{remote}\"");
        var repo = SeedRepo(name);
        RunGit(repo, $"remote add origin \"{remote}\"");
        RunGit(repo, "push -q -u origin main");
        return (repo, remote);
    }

    private string SeedRepo(string name)
    {
        var repo = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(repo);
        RunGit(repo, "init -q -b main");
        RunGit(repo, "config user.email test@example.com");
        RunGit(repo, "config user.name test");
        File.WriteAllText(Path.Combine(repo, "README.md"), "seed");
        Commit(repo, "seed");
        return repo;
    }

    private static string RemoteSha(string remote, string branch)
        => RunGit(remote, $"-c safe.bareRepository=all rev-parse refs/heads/{branch}").Out.Trim();

    private static void Commit(string cwd, string message)
    {
        RunGit(cwd, "add -A");
        RunGit(cwd, $"commit -q -m \"{message}\"");
    }

    private static (string Out, string Err, int Code) RunGit(string cwd, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit(15_000);
        return (output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult(), process.ExitCode);
    }
}
