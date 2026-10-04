using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TaskServer.Recovery;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace TaskServer.Tests;

/// <summary>
/// Empty-target recovery rehearsal (Dossier AGT-W63 D7 option A, I07). The source authority stands in for
/// production and is never restored over; every target starts from an empty directory and only the copy.
/// </summary>
public sealed class RecoveryDrillTests(ITestOutputHelper output)
{
    private const string RunnerPrincipal = "runner:runner-full";

    [Fact]
    public async Task Lost_runner_credential_blocks_resume_until_fenced_and_reenrolled()
    {
        using var temp = new TempDirectory("recovery-lost-client");
        var drill = await CaptureAsync(temp.Path, RecoveryCredentialCustody.Undeclared);
        var targetDirectory = Path.Combine(temp.Path, "target");
        var target = Store(targetDirectory, drill.Clock);
        var workflow = Workflow(target, targetDirectory, drill.Clock);
        Assert.True((await workflow.RestoreToEmptyAsync(drill.CopyRoot, null, null, "drill", default)).Restored);

        target = Store(targetDirectory, drill.Clock);
        await target.InitializeAsync();
        workflow = Workflow(target, targetDirectory, drill.Clock);
        drill.Clock.Advance(TimeSpan.FromSeconds(1));
        await workflow.FenceHostsAsync("drill", default);
        var (blocked, _) = await workflow.ResumeAsync(true, false, true, null, "drill", default);
        Assert.Contains(blocked.Blockers, item => item.Code == "client-credentials-lost" && item.Subject == RunnerPrincipal);

        drill.Clock.Advance(TimeSpan.FromSeconds(1));
        await workflow.ReenrolClientAsync(RunnerPrincipal, "drill", default);
        var (ready, _) = await workflow.ResumeAsync(true, false, true, null, "drill", default);
        Assert.True(ready.Allowed, string.Join("; ", ready.Blockers.Select(item => item.Code)));
    }

    [Fact]
    public async Task Lost_studio_credential_requires_rotation_in_maintenance()
    {
        using var temp = new TempDirectory("recovery-lost-studio");
        var drill = await CaptureAsync(temp.Path, includeLostStudio: true);
        var targetDirectory = Path.Combine(temp.Path, "target");
        var target = Store(targetDirectory, drill.Clock);
        var workflow = Workflow(target, targetDirectory, drill.Clock);
        Assert.True((await workflow.RestoreToEmptyAsync(drill.CopyRoot, null, null, "drill", default)).Restored);

        target = Store(targetDirectory, drill.Clock);
        await target.InitializeAsync();
        workflow = Workflow(target, targetDirectory, drill.Clock);
        drill.Clock.Advance(TimeSpan.FromSeconds(1));
        await workflow.FenceHostsAsync("drill", default);
        Assert.NotNull(await target.AuthenticatePrincipalAsync(drill.OldStudioCredential!, default));
        var (blocked, _) = await workflow.ResumeAsync(true, false, true, null, "drill", default);
        Assert.Contains(blocked.Blockers, item => item.Code == "client-credentials-lost" && item.Subject == "studio:recovery");

        drill.Clock.Advance(TimeSpan.FromSeconds(1));
        var reenrolled = await workflow.ReenrolClientAsync("studio:recovery", "drill", default);
        Assert.Null(await target.AuthenticatePrincipalAsync(drill.OldStudioCredential!, default));
        Assert.NotNull(await target.AuthenticatePrincipalAsync(reenrolled.Credential, default));
        var (ready, _) = await workflow.ResumeAsync(true, false, true, null, "drill", default);
        Assert.True(ready.Allowed, string.Join("; ", ready.Blockers.Select(item => item.Code)));
    }

    [Fact]
    public async Task Moved_origin_ref_blocks_resume_until_immutable_ref_proves_recorded_commit()
    {
        using var temp = new TempDirectory("recovery-moved-ref");
        var drill = await CaptureAsync(temp.Path);
        var clone = Path.Combine(temp.Path, "clone");
        Git(temp.Path, "clone", drill.Origin, clone);
        var recordedSha = Git(clone, "rev-parse", "HEAD").Trim();
        await File.WriteAllTextAsync(Path.Combine(clone, "NEXT.md"), "later publication\n");
        Git(clone, "add", "NEXT.md");
        Git(clone, "-c", "user.name=drill", "-c", "user.email=drill@example.invalid", "commit", "-m", "later");
        Git(clone, "push", "origin", "main");

        var targetDirectory = Path.Combine(temp.Path, "target");
        var target = Store(targetDirectory, drill.Clock);
        var workflow = Workflow(target, targetDirectory, drill.Clock);
        var restore = await workflow.RestoreToEmptyAsync(drill.CopyRoot, null, null, "drill", default);
        Assert.True(restore.Restored, restore.Message);
        Assert.Contains(restore.Report.Findings, item => item.Code == "git-ref-moved" && item.Severity == RecoveryFindingSeverity.BlocksResume);

        target = Store(targetDirectory, drill.Clock);
        await target.InitializeAsync();
        workflow = Workflow(target, targetDirectory, drill.Clock);
        drill.Clock.Advance(TimeSpan.FromSeconds(1));
        await workflow.FenceHostsAsync("drill", default);
        var (blocked, _) = await workflow.ResumeAsync(true, false, true, null, "drill", default);
        Assert.Contains(blocked.Blockers, item => item.Code == "git-ref-moved");
        Git(clone, "push", "origin", $"{recordedSha}:refs/heads/agent-studio/results/run_recovery/fence-1/{recordedSha}");
        var (ready, _) = await workflow.ResumeAsync(true, false, true, null, "drill", default);
        Assert.True(ready.Allowed, string.Join("; ", ready.Blockers.Select(item => item.Code)));
    }

    [Fact]
    public async Task Resume_requires_the_copied_set_for_fresh_git_verification()
    {
        using var temp = new TempDirectory("recovery-copy-resume");
        var drill = await CaptureAsync(temp.Path);
        var targetDirectory = Path.Combine(temp.Path, "target");
        var target = Store(targetDirectory, drill.Clock);
        var workflow = Workflow(target, targetDirectory, drill.Clock);
        Assert.True((await workflow.RestoreToEmptyAsync(drill.CopyRoot, null, null, "drill", default)).Restored);

        target = Store(targetDirectory, drill.Clock);
        await target.InitializeAsync();
        workflow = Workflow(target, targetDirectory, drill.Clock);
        drill.Clock.Advance(TimeSpan.FromSeconds(1));
        await workflow.FenceHostsAsync("drill", default);

        var unavailableCopy = drill.CopyRoot + ".offline";
        Directory.Move(drill.CopyRoot, unavailableCopy);
        Directory.Move(drill.Origin, drill.Origin + ".offline");
        var (missing, _) = await workflow.ResumeAsync(true, false, false, null, "drill", default);
        Assert.Contains(missing.Blockers, item => item.Code == "recovery-copy-unavailable");
        Assert.Equal(TaskServerMode.Maintenance, target.Mode);

        Directory.Move(unavailableCopy, drill.CopyRoot);
        var (staleOrigin, _) = await workflow.ResumeAsync(true, false, false, null, "drill", default);
        Assert.Contains(staleOrigin.Blockers, item => item.Code == "git-origin-unavailable");
        Assert.Equal(TaskServerMode.Maintenance, target.Mode);

        Directory.Move(drill.Origin + ".offline", drill.Origin);
        var (ready, _) = await workflow.ResumeAsync(true, false, true, null, "drill", default);
        Assert.True(ready.Allowed, string.Join("; ", ready.Blockers.Select(item => item.Code)));
    }

    [Theory]
    [InlineData("repository-ref", "manifest-digest-mismatch")]
    [InlineData("client-custody", "manifest-digest-mismatch")]
    [InlineData("receipt-removed", "copy-receipt-missing")]
    public async Task Resume_rejects_a_copy_whose_manifest_is_no_longer_bound_to_its_receipt(
        string change, string expectedFinding)
    {
        using var temp = new TempDirectory("recovery-manifest-binding");
        var drill = await CaptureAsync(temp.Path);
        var targetDirectory = Path.Combine(temp.Path, "target");
        var target = Store(targetDirectory, drill.Clock);
        var workflow = Workflow(target, targetDirectory, drill.Clock);
        Assert.True((await workflow.RestoreToEmptyAsync(drill.CopyRoot, null, null, "drill", default)).Restored);

        target = Store(targetDirectory, drill.Clock);
        await target.InitializeAsync();
        workflow = Workflow(target, targetDirectory, drill.Clock);
        await workflow.FenceHostsAsync("drill", default);

        if (change == "receipt-removed")
            File.Delete(Path.Combine(drill.CopyRoot, RecoveryWorkflow.CopyReceiptFile));
        else
        {
            var path = Path.Combine(drill.CopyRoot, RecoveryWorkflow.ManifestFile);
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            if (change == "repository-ref")
                manifest["repositories"]![0]!["sampledRefs"]![0]!["sha"] = new string('0', 40);
            else
                manifest["secretCustody"]!["clients"]![0]!["custody"] = RecoveryCredentialCustody.ReEnrol;
            await File.WriteAllTextAsync(path, manifest.ToJsonString());
        }

        var verification = await workflow.VerifyCopyAsync(drill.CopyRoot, null, true, default);
        Assert.Contains(verification.Report.Findings, item =>
            item.Code == expectedFinding && item.Severity == RecoveryFindingSeverity.BlocksRestore);
        var (decision, _) = await workflow.ResumeAsync(true, false, false, null, "drill", default);
        Assert.Contains(decision.Blockers, item => item.Code == expectedFinding);
        Assert.Equal(TaskServerMode.Maintenance, target.Mode);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("missing")]
    [InlineData("inconsistent")]
    public async Task Resume_rejects_failed_or_missing_identity_comparisons(string comparisonFault)
    {
        using var temp = new TempDirectory("recovery-identity-gate");
        var drill = await CaptureAsync(temp.Path);
        var targetDirectory = Path.Combine(temp.Path, "target");
        var target = Store(targetDirectory, drill.Clock);
        var workflow = Workflow(target, targetDirectory, drill.Clock);
        var restored = await workflow.RestoreToEmptyAsync(drill.CopyRoot, null, null, "drill", default);
        Assert.True(restored.Restored, restored.Message);

        var comparisons = comparisonFault == "missing"
            ? []
            : restored.Receipt!.Comparisons.Select((item, index) => index == 0
                ? comparisonFault == "failed" ? item with { Matches = false } : item with { Actual = "wrong" }
                : item).ToArray();
        var receipt = restored.Receipt! with { Comparisons = comparisons };
        await File.WriteAllTextAsync(Path.Combine(targetDirectory, RecoveryRestoreReceipt.FileName),
            JsonSerializer.Serialize(receipt, RecoveryJson.Options));

        target = Store(targetDirectory, drill.Clock);
        await target.InitializeAsync();
        workflow = Workflow(target, targetDirectory, drill.Clock);
        await workflow.FenceHostsAsync("drill", default);
        var (decision, _) = await workflow.ResumeAsync(true, false, false, null, "drill", default);
        Assert.Contains(decision.Blockers, item => item.Code == "identity-comparison-failed");
        Assert.Equal(TaskServerMode.Maintenance, target.Mode);
    }

    [Fact]
    public async Task Empty_target_rebuilds_from_the_retained_set_and_resumes_behind_the_gate()
    {
        using var temp = new TempDirectory("recovery-drill");
        var drill = await CaptureAsync(temp.Path);

        // A write the old authority accepts after capture: lost on restore, and later replayed by its host.
        await drill.Source.CreateTaskAsync(drill.ProjectId, new CreateTaskRequest("After capture", State: "2-ready"), "test", default);
        var lostClaim = await drill.Source.ClaimAsync(new ClaimRequest("runner-full", "runner-full:1"), "test", default);
        drill.Clock.Advance(TimeSpan.FromSeconds(90));
        var lossAt = drill.Clock.GetUtcNow().UtcDateTime;

        var targetDirectory = Path.Combine(temp.Path, "target");
        var target = Store(targetDirectory, drill.Clock);
        var workflow = Workflow(target, targetDirectory, drill.Clock);
        var restoreTimer = Stopwatch.StartNew();
        var restored = await workflow.RestoreToEmptyAsync(drill.CopyRoot, null, lossAt, "drill", default);
        restoreTimer.Stop();

        Assert.True(restored.Restored, restored.Message);
        Assert.All(restored.Receipt!.Comparisons, item => Assert.True(item.Matches, $"{item.Subject}: {item.Expected} != {item.Actual}"));
        Assert.Contains(restored.Receipt.Comparisons, item => item.Subject.StartsWith("cold ", StringComparison.Ordinal));
        Assert.DoesNotContain(restored.Report.Findings, item => item.Code.StartsWith("git-", StringComparison.Ordinal));
        Assert.Equal(90, restored.Receipt.MeasuredRecoveryPointSeconds);
        Assert.Equal(TaskServerMode.Maintenance, target.Mode);
        Assert.Equal(drill.Source.ServerId, target.ServerId);

        // A fresh process on the restored store, as the CLI would run it.
        target = Store(targetDirectory, drill.Clock);
        await target.InitializeAsync();
        workflow = Workflow(target, targetDirectory, drill.Clock);
        var (blocked, _) = await workflow.ResumeAsync(false, false, true, null, "drill", default);
        Assert.Contains(blocked.Blockers, item => item.Code == "old-writer-open");
        Assert.Contains(blocked.Blockers, item => item.Code == "stale-hosts-unfenced");

        Assert.NotNull(await target.AuthenticatePrincipalAsync(drill.OldRunnerCredential, default));
        drill.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await workflow.FenceHostsAsync("drill", default));
        Assert.Null(await target.AuthenticatePrincipalAsync(drill.OldRunnerCredential, default));

        drill.Clock.Advance(TimeSpan.FromSeconds(1));
        var reenrolled = await workflow.ReenrolClientAsync(RunnerPrincipal, "drill", default);
        Assert.NotNull(await target.AuthenticatePrincipalAsync(reenrolled.Credential, default));

        drill.Clock.Advance(TimeSpan.FromSeconds(30));
        var (decision, receipt) = await workflow.ResumeAsync(true, false, false, null, "drill", default);
        Assert.True(decision.Allowed, string.Join("; ", decision.Blockers.Select(item => item.Code)));
        Assert.Equal(TaskServerMode.Normal, target.Mode);
        Assert.Equal(32, receipt!.MeasuredRecoveryTimeSeconds);

        // Obsolete replay: the old host reports the run the lost authority leased after capture.
        var replay = await Assert.ThrowsAnyAsync<Exception>(() => target.CompleteRunAsync(lostClaim.Run!.RunId, new CompleteRunRequest(
            "runner-full", "runner-full:1", lostClaim.Lease!.LeaseId, lostClaim.Lease.Fence,
            ExecutionOutcomeKind.LaunchFailure.ToString(), IdempotencyKey: "lost-completion", Sequence: 1), "runner-full", default));
        output.WriteLine($"obsolete replay rejected: {replay.GetType().Name}: {replay.Message}");
        Assert.True(replay is KeyNotFoundException or TaskServerConflictException or InvalidOperationException, replay.ToString());

        // Reconnect one host with a new instance and finish a canary.
        await target.RegisterRunnerAsync("runner-full",
            new RegisterRunnerRequest("runner-full", "host-full", "runner-full:2", "1.0.0", TaskServerProtocol.Current,
                [ReviewCapabilities.CodingExecutor]), "runner-full", default);
        var canaryTask = await target.CreateTaskAsync(drill.ProjectId, new CreateTaskRequest("Recovery canary", State: "2-ready"), "drill", default);
        var canary = await target.ClaimAsync(new ClaimRequest("runner-full", "runner-full:2"), "runner-full", default);
        Assert.Equal(canaryTask.TaskId, canary.Task!.TaskId);
        var canaryBytes = Encoding.UTF8.GetBytes("recovery canary\n");
        await target.IngestArtifactAsync(canary.Run!.RunId, new ArtifactIngestRequest(
            "art-recovery-canary", "logs/canary.log", "text/plain", Convert.ToBase64String(canaryBytes),
            Convert.ToHexStringLower(SHA256.HashData(canaryBytes)), "canary-ingest", canary.Lease!.Fence), "runner-full", default);
        var work = Path.Combine(temp.Path, "work");
        var baseSha = Git(work, "rev-parse", "HEAD").Trim();
        await File.WriteAllTextAsync(Path.Combine(work, "CANARY.md"), "recovered authority completed a canary\n");
        Git(work, "add", "CANARY.md");
        Git(work, "-c", "user.name=drill", "-c", "user.email=drill@example.invalid", "commit", "-m", "recovery canary");
        var resultSha = Git(work, "rev-parse", "HEAD").Trim();
        var resultRef = FencedGitRefs.ImmutableResult(canary.Run.RunId, canary.Lease.Fence, resultSha);
        Git(work, "push", drill.Origin, $"HEAD:{resultRef}");
        var envelope = new ImmutableResultEnvelope("recovery-repo", canary.Run.RunId, baseSha, resultSha,
            resultRef, null, Convert.ToHexStringLower(SHA256.HashData(canaryBytes)));
        var digest = ResultEnvelopeDigest.Compute(envelope);
        await target.AcknowledgeResultHandoffAsync(canary.Run.RunId, new ResultHandoffRequest(
            "runner-full", "runner-full:2", canary.Lease.LeaseId, canary.Lease.Fence, 1,
            $"handoff:{canary.Run.RunId}", digest, envelope), "runner-full", default);
        var completedCanary = await target.CompleteRunAsync(canary.Run.RunId, new CompleteRunRequest(
            "runner-full", "runner-full:2", canary.Lease.LeaseId, canary.Lease.Fence, "success",
            "Recovery canary published.", digest, $"completion:{canary.Run.RunId}", 2), "runner-full", default);
        Assert.Equal("success", completedCanary.Status);
        Assert.NotNull(completedCanary.FinishedAt);
        Assert.Equal(resultSha, completedCanary.ResultSha);
        Assert.Equal(resultSha, (await new OriginRefProbe(Http).ListRemoteAsync(drill.Origin, default))![resultRef]);

        // Production state is untouched by the rehearsal.
        Assert.Equal(TaskServerMode.Normal, drill.Source.Mode);
        Assert.Equal(drill.SourceTaskCount + 1, (await drill.Source.ReadLiveRecoveryFactsAsync(default)).TaskCount);

        var report = new
        {
            schema = "agent-studio.recovery-drill-report/v1",
            manifest = receipt.ManifestId,
            setSha256 = receipt.SetSha256,
            comparisons = receipt.Comparisons.Count,
            measuredRecoveryPointSeconds = receipt.MeasuredRecoveryPointSeconds,
            measuredRecoveryPointBasis = "drill clock: loss instant minus manifest capture time",
            measuredRecoveryTimeSeconds = receipt.MeasuredRecoveryTimeSeconds,
            measuredRecoveryTimeBasis = "drill clock: loss instant to resume gate release, including scripted operator steps",
            wallClockRestoreSeconds = Math.Round(restoreTimer.Elapsed.TotalSeconds, 3),
            lostWritesAfterCapture = 1,
            canaryRun = canary.Run.RunId,
            canaryStatus = completedCanary.Status,
            canaryFinishedAt = completedCanary.FinishedAt,
            canaryResultRef = resultRef,
            canaryResultSha = resultSha,
        };
        var text = JsonSerializer.Serialize(report, RecoveryJson.Options);
        output.WriteLine(text);
        if (Environment.GetEnvironmentVariable("RECOVERY_DRILL_REPORT") is { Length: > 0 } reportPath)
            await File.WriteAllTextAsync(reportPath, text);
    }

    public static TheoryData<string, string> Faults => new()
    {
        { "missing-cold-payload", "restore-refused" },
        { "incomplete-set", "restore-refused" },
        { "corrupted-hash", "restore-refused" },
        { "schema-mismatch", "restore-refused" },
        { "git-origin-unavailable", "resume-blocked" },
        { "client-credentials-lost", "resume-blocked" },
    };

    [Theory]
    [MemberData(nameof(Faults))]
    public async Task Injected_fault_yields_specific_guidance_and_never_touches_production(string fault, string effect)
    {
        using var temp = new TempDirectory("recovery-fault");
        var drill = await CaptureAsync(temp.Path);
        var set = Path.Combine(drill.CopyRoot, RecoveryWorkflow.SetDirectory);
        string? bundleOverride = null;
        switch (fault)
        {
            case "missing-cold-payload":
                File.Delete(Directory.EnumerateFiles(Path.Combine(set, "cold"), "*", SearchOption.AllDirectories).Single());
                break;
            case "incomplete-set":
                File.Delete(Path.Combine(set, "complete.json"));
                break;
            case "corrupted-hash":
                await File.AppendAllTextAsync(Path.Combine(set, "export", "tasks.jsonl"), "{}\n");
                break;
            case "schema-mismatch":
                var manifestPath = Path.Combine(drill.CopyRoot, RecoveryWorkflow.ManifestFile);
                var node = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!;
                node["store"]!["schemaVersion"] = TaskServerStore.CurrentSchemaVersion + 1;
                await File.WriteAllTextAsync(manifestPath, node.ToJsonString());
                break;
            case "git-origin-unavailable":
                Directory.Move(drill.Origin, drill.Origin + ".offline");
                break;
            case "client-credentials-lost":
                bundleOverride = Path.Combine(temp.Path, "lost-secrets.age");
                break;
        }

        var targetDirectory = Path.Combine(temp.Path, "target");
        var target = Store(targetDirectory, drill.Clock);
        var result = await Workflow(target, targetDirectory, drill.Clock)
            .RestoreToEmptyAsync(drill.CopyRoot, bundleOverride, null, "drill", default);
        var finding = Assert.Single(result.Report.Findings, item => item.Code == fault);
        output.WriteLine($"{fault}: {finding.Guidance}");
        Assert.False(string.IsNullOrWhiteSpace(finding.Guidance));

        if (effect == "restore-refused")
        {
            Assert.False(result.Restored);
            Assert.Equal(RecoveryFindingSeverity.BlocksRestore, finding.Severity);
            Assert.False(Directory.Exists(targetDirectory) && Directory.EnumerateFileSystemEntries(targetDirectory).Any());
        }
        else
        {
            Assert.True(result.Restored, result.Message);
            Assert.Equal(RecoveryFindingSeverity.BlocksResume, finding.Severity);
            target = Store(targetDirectory, drill.Clock);
            await target.InitializeAsync();
            var workflow = Workflow(target, targetDirectory, drill.Clock);
            drill.Clock.Advance(TimeSpan.FromSeconds(1));
            await workflow.FenceHostsAsync("drill", default);
            var (decision, _) = await workflow.ResumeAsync(true, false, false, bundleOverride, "drill", default);
            Assert.False(decision.Allowed);
            Assert.Equal(TaskServerMode.Maintenance, target.Mode);
            if (fault == "git-origin-unavailable") Assert.Contains(decision.Blockers, item => item.Code == fault);
            else
            {
                Assert.Contains(decision.Blockers, item => item.Code == "secret-bundle-missing");
                Assert.Contains(decision.Blockers, item => item.Code == "client-credentials-lost");
            }
        }

        Assert.Equal(TaskServerMode.Normal, drill.Source.Mode);
        Assert.Equal(drill.SourceTaskCount, (await drill.Source.ReadLiveRecoveryFactsAsync(default)).TaskCount);
    }

    [Fact]
    public async Task Copy_and_restore_refuse_non_empty_targets()
    {
        using var temp = new TempDirectory("recovery-nonempty");
        var drill = await CaptureAsync(temp.Path);
        var source = Workflow(drill.Source, drill.SourceDirectory, drill.Clock);
        await Assert.ThrowsAsync<IOException>(() => source.CopyAsync(drill.BackupId, Path.GetDirectoryName(drill.CopyRoot)!, default));

        var targetDirectory = Path.Combine(temp.Path, "occupied");
        Directory.CreateDirectory(targetDirectory);
        await File.WriteAllTextAsync(Path.Combine(targetDirectory, "keep.txt"), "existing");
        var target = Store(targetDirectory, drill.Clock);
        await Assert.ThrowsAsync<IOException>(() =>
            Workflow(target, targetDirectory, drill.Clock).RestoreToEmptyAsync(drill.CopyRoot, null, null, "drill", default));
        Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(targetDirectory, "keep.txt")));
    }

    private sealed record Drill(
        TaskServerStore Source,
        string SourceDirectory,
        ManualTimeProvider Clock,
        string ProjectId,
        string BackupId,
        string CopyRoot,
        string Origin,
        string OldRunnerCredential,
        int SourceTaskCount,
        string? OldStudioCredential);

    private static async Task<Drill> CaptureAsync(string root, string credentialCustody = RecoveryCredentialCustody.SecretBundle,
        bool includeLostStudio = false)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var sourceDirectory = Path.Combine(root, "source");
        var source = Store(sourceDirectory, clock);
        await source.InitializeAsync();

        var workspace = await source.CreateWorkspaceAsync(new CreateWorkspaceRequest("Recovery"), "test", default);
        var project = await source.CreateProjectAsync(new CreateProjectRequest(workspace.WorkspaceId, "Recovery", "RCV"), "test", default);
        await source.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Archived task", State: "2-ready"), "test", default);
        var runner = await source.CreatePrincipalAsync(
            new CreatePrincipalRequest(RunnerPrincipal, TaskServerPrincipalKinds.Runner, RunnerId: "runner-full"), "test", default);
        var studio = includeLostStudio
            ? await source.CreatePrincipalAsync(new CreatePrincipalRequest("studio:recovery", TaskServerPrincipalKinds.Studio), "test", default)
            : null;
        await source.RegisterRunnerAsync("runner-full",
            new RegisterRunnerRequest("runner-full", "host-full", "runner-full:1", "1.0.0", TaskServerProtocol.Current,
                [ReviewCapabilities.CodingExecutor]), "test", default);
        var claim = await source.ClaimAsync(new ClaimRequest("runner-full", "runner-full:1"), "test", default);
        var bytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("recovery drill log\n", 40)));
        await source.IngestArtifactAsync(claim.Run!.RunId, new ArtifactIngestRequest(
            "art-recovery-cold", "logs/cli-output.log", "text/plain", Convert.ToBase64String(bytes),
            Convert.ToHexStringLower(SHA256.HashData(bytes)), "recovery-ingest", claim.Lease!.Fence), "runner-full", default);
        await source.UpdateTaskAsync(project.ProjectId, claim.Task!.TaskId,
            new UpdateTaskRequest(null, null, "7-archive", claim.Task.Version), "test", default);
        await source.ReleaseLeaseAsync(claim.Run.RunId,
            new LeaseReleaseRequest("runner-full", "runner-full:1", claim.Lease.LeaseId, claim.Lease.Fence, "completed"), "test", default);
        clock.Advance(TimeSpan.FromDays(31));
        Assert.Equal(1, (await source.ApplyRetentionRunAsync(new RunRetentionRequest(), "test", default)).AppliedActions);
        await source.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Open task", State: "1-backlog"), "test", default);

        var origin = Path.Combine(root, "origin.git");
        var work = Path.Combine(root, "work");
        Git(root, "init", "--bare", "-b", "main", origin);
        Git(root, "init", "-b", "main", work);
        await File.WriteAllTextAsync(Path.Combine(work, "README.md"), "recovery drill\n");
        Git(work, "add", ".");
        Git(work, "-c", "user.name=drill", "-c", "user.email=drill@example.invalid", "commit", "-m", "canonical");
        Git(work, "push", origin, "main");

        var bundle = Path.Combine(root, "offhost", "secrets.age");
        Directory.CreateDirectory(Path.GetDirectoryName(bundle)!);
        await File.WriteAllTextAsync(bundle, "age-encrypted-placeholder");
        var clientCustody = new List<RecoveryClientCustodyDeclaration> { new(RunnerPrincipal, credentialCustody) };
        if (includeLostStudio) clientCustody.Add(new("studio:recovery", RecoveryCredentialCustody.Undeclared));
        var custody = new RecoveryCustodyDeclaration(
            "inst_drill",
            [new("compose environment", Path.Combine(root, "compose.env"), RecoveryCredentialCustody.SecretBundle, null)],
            new(bundle, "age", "administrator"),
            clientCustody,
            [new("recovery-repo", origin, ["refs/heads/main"])]);

        var workflow = Workflow(source, sourceDirectory, clock);
        var manifest = await workflow.CaptureAsync(custody, "drill", default);
        Assert.Equal(RecoveryManifest.CurrentSchema, manifest.Schema);
        Assert.Equal(1, manifest.ColdEvidence.PayloadCount);
        Assert.Equal(2, manifest.Identities.TaskCount);
        Assert.Equal(40, Assert.Single(Assert.Single(manifest.Repositories).SampledRefs).Sha.Length);
        Assert.Contains(manifest.SecretCustody.Clients, item => item.PrincipalId == RunnerPrincipal && item.Custody == credentialCustody);
        Assert.NotEmpty(manifest.RebuildableCaches);

        var receipt = await workflow.CopyAsync(manifest.DataSet.BackupId, Path.Combine(root, "offhost"), default);
        Assert.Empty(receipt.Warnings);
        return new Drill(source, sourceDirectory, clock, project.ProjectId, manifest.DataSet.BackupId, receipt.Destination, origin,
            runner.Credential, manifest.Identities.TaskCount, studio?.Credential);
    }

    private static RecoveryWorkflow Workflow(TaskServerStore store, string directory, TimeProvider clock)
        => new(store, Options(directory), new OriginRefProbe(Http), clock);

    private static readonly HttpClient Http = new();

    private static TaskServerOptions Options(string directory) => new() { DataDirectory = directory };

    private static TaskServerStore Store(string directory, TimeProvider clock)
        => new(Microsoft.Extensions.Options.Options.Create(Options(directory)), clock);

    private static string Git(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
        return output;
    }
}
