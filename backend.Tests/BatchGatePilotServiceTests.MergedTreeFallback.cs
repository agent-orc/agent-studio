using AgentStudio.Pipeline;
using AgentStudio.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3434: the per-task fallback gate tests the delivery merged onto the
/// current integration tip, a conflict goes back to the agent without a gate,
/// and the publish step reuses the verdict while the tip has not moved.
/// </summary>
public sealed partial class BatchGatePilotServiceTests
{
    [Fact]
    public async Task Per_task_fallback_tests_the_delivery_merged_onto_the_current_tip()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        var member = SeedFallbackMember(factory, "DOC-MERGED-TREE", "docs/merged-tree.md");
        PushToOriginDevelop("docs/drift.md", "moved on after the delivery\n");
        var tip = RemoteTip();
        string? tested = null;
        _gate.Fallback = request =>
        {
            tested = request.ExpectedSha;
            return Green(request.ExpectedSha);
        };

        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();

        await pilot.TickAsync(CancellationToken.None);

        Assert.Equal(1, _gate.FallbackRuns);
        Assert.NotNull(tested);
        Assert.NotEqual(member.ResultSha, tested);
        Assert.Equal($"{tested} {tip} {member.ResultSha}", Git(_repo, "rev-list", "--parents", "-n", "1", tested!));
        Assert.Equal("moved on after the delivery", Git(_repo, "show", $"{tested}:docs/drift.md"));
        var ownership = Ownership(member.Key)!;
        Assert.Equal(member.ResultSha, ownership.FallbackTestedSha);
        Assert.Equal(tip, ownership.FallbackBaseSha);
        Assert.Equal(tested, ownership.FallbackCandidateSha);
        Assert.Equal(Git(_repo, "rev-parse", tested + "^{tree}"), ownership.FallbackCandidateTreeSha);
        Assert.True(ownership.FallbackIntegrated);
        // The pin lives only while the gate runs.
        Assert.Empty(Git(_repo, "for-each-ref", "refs/agent-studio/fallback-candidates/"));

        await PublishAndTickAsync(factory, pilot, member.Key);

        AssertLane(member.Key, TaskStates.HumanReview);
        Assert.Equal(1, _gate.FallbackRuns);
    }

    [Fact]
    public async Task Failure_already_fixed_on_the_tip_does_not_fail_the_fallback()
    {
        // AGT-3408: the delivery's own base still carried two tests that
        // develop had fixed, and the per-task gate escalated the card for them.
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        PushToOriginDevelop("tests/sandbox-policy.txt", "broken\n");
        var member = SeedFallbackMember(factory, "DOC-FIXED-ON-TIP", "docs/fixed-on-tip.md");
        PushToOriginDevelop("tests/sandbox-policy.txt", "fixed\n");
        _gate.Fallback = request => Git(_repo, "show", $"{request.ExpectedSha}:tests/sandbox-policy.txt") == "broken"
            ? Red(request.ExpectedSha)
            : Green(request.ExpectedSha);

        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();

        await pilot.TickAsync(CancellationToken.None);
        Assert.True(Ownership(member.Key)?.FallbackIntegrated);
        await PublishAndTickAsync(factory, pilot, member.Key);

        Assert.Equal(1, _gate.FallbackRuns);
        Assert.Equal(0, _gate.BaseRuns);
        AssertLane(member.Key, TaskStates.HumanReview);
        Assert.Equal("fixed", Git(_origin, "show", "refs/heads/develop:tests/sandbox-policy.txt"));
        Assert.Equal(member.Key, Git(_origin, "show", "refs/heads/develop:docs/fixed-on-tip.md"));
    }

    [Fact]
    public async Task Conflict_with_the_tip_returns_the_card_to_its_agent_without_a_gate()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        var member = SeedFallbackMember(factory, "DOC-TIP-CONFLICT", "README.md");
        var folder = Path.Combine(_watchPath, TaskStates.AutoReview, member.Key);
        // A Remote completion records the fenced result ref the merge round needs.
        ReviewSubjectStore.Write(folder, ReviewSubjectStore.Read(folder)! with
        {
            ResultRef = "refs/heads/agent-studio/results/" + member.Key,
        });
        PushToOriginDevelop("README.md", "Integration changed this line.\n");
        _gate.Fallback = _ => throw new InvalidOperationException("A conflicting delivery reached a gate.");
        var store = factory.Services.GetRequiredService<BatchGateStore>();

        await factory.Services.GetRequiredService<BatchGatePilotService>().TickAsync(CancellationToken.None);

        Assert.Equal(0, _gate.FallbackRuns);
        AssertLane(member.Key, TaskStates.Ready);
        var card = factory.Services.GetRequiredService<TaskScannerService>().FindJob(member.Key, _watchPath)!;
        Assert.Contains("README.md", card.PendingIntent?.Prompt);
        Assert.Empty(store.ListPending());
        Assert.Null(Ownership(member.Key));
        var fact = Assert.Single(store.ListFallbackSuites(ProjectName));
        Assert.Equal("merge-round", fact.Outcome);
        Assert.Equal(0, fact.Suites);
    }

    [Fact]
    public async Task Conflict_without_an_automatic_merge_round_escalates_with_the_files_and_no_gate()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        var member = SeedFallbackMember(factory, "DOC-TIP-CONFLICT-NO-ROUND", "README.md");
        PushToOriginDevelop("README.md", "Integration changed this line.\n");
        _gate.Fallback = _ => throw new InvalidOperationException("A conflicting delivery reached a gate.");

        await factory.Services.GetRequiredService<BatchGatePilotService>().TickAsync(CancellationToken.None);

        Assert.Equal(0, _gate.FallbackRuns);
        AssertLane(member.Key, TaskStates.Escalated);
        // The escalation verdict names the conflict and its files.
        Assert.Contains(Directory.GetFiles(_workspace, "*", SearchOption.AllDirectories)
                .Where(file => !file.StartsWith(_repo, StringComparison.Ordinal)
                    && !file.StartsWith(_origin, StringComparison.Ordinal)
                    && !file.EndsWith(".lock", StringComparison.Ordinal))
                .Select(File.ReadAllText),
            text => text.Contains("Merge conflict with develop", StringComparison.Ordinal)
                && text.Contains("README.md", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Publish_step_reuses_the_per_task_verdict_when_the_tip_has_not_moved()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        RequireMergeGate(factory);
        var member = SeedFallbackMember(factory, "DOC-REUSED-VERDICT", "docs/reused-verdict.md");
        PushToOriginDevelop("docs/drift.md", "moved on before the fallback\n");
        _gate.Fallback = request => Green(request.ExpectedSha);
        _gate.Merge = _ => throw new InvalidOperationException("The publish step ran its suite on an unmoved tip.");
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();

        await pilot.TickAsync(CancellationToken.None);

        Assert.Equal(1, _gate.FallbackRuns);
        Assert.Equal(0, _gate.MergeRuns);
        var receipt = Directory.GetFiles(Path.Combine(_watchPath, TaskStates.AutoReview, member.Key, "post-steps"),
            IntegrationGateJournal.PreDevelopBuildGateStep + "-*.log").Single();
        Assert.Contains("verdictSource=CacheHit", File.ReadAllText(receipt));
        var report = pilot.Report(ProjectName);
        Assert.Equal(1, report.PerTaskFallbackCards);
        Assert.Equal(1, report.PerTaskFallbackSuiteRuns);
        Assert.Equal(1d, report.PerTaskFallbackSuitesPerCard);
        Assert.Equal(1, report.PerTaskPublishVerdictReuses);

        await PublishAndTickAsync(factory, pilot, member.Key);

        AssertLane(member.Key, TaskStates.HumanReview);
        Assert.Equal(0, _gate.MergeRuns);
        Assert.Equal(member.Key, Git(_origin, "show", "refs/heads/develop:docs/reused-verdict.md"));
    }

    [Fact]
    public async Task Publish_step_runs_its_gate_when_the_tip_moved_after_the_fallback()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        RequireMergeGate(factory);
        var member = SeedFallbackMember(factory, "DOC-MOVED-TIP", "docs/moved-tip.md");
        _gate.Fallback = request =>
        {
            // Another delivery lands while the per-task suite runs.
            PushToOriginDevelop("docs/landed-meanwhile.md", "landed\n");
            return Green(request.ExpectedSha);
        };
        string? merged = null;
        _gate.Merge = request =>
        {
            merged = request.ExpectedSha;
            return Green(request.ExpectedSha);
        };
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();

        await pilot.TickAsync(CancellationToken.None);

        Assert.Equal(1, _gate.FallbackRuns);
        Assert.Equal(1, _gate.MergeRuns);
        Assert.Equal("landed", Git(_repo, "show", $"{merged}:docs/landed-meanwhile.md"));
        await PublishAndTickAsync(factory, pilot, member.Key);
        AssertLane(member.Key, TaskStates.HumanReview);
        var report = pilot.Report(ProjectName);
        Assert.Equal(1, report.PerTaskFallbackCards);
        Assert.Equal(2, report.PerTaskFallbackSuiteRuns);
        Assert.Equal(0, report.PerTaskPublishVerdictReuses);
    }

    [Fact]
    public async Task Red_candidate_on_a_red_tip_is_retested_on_the_delivery_own_base()
    {
        // What the old base protected: a red integration branch does not
        // escalate a delivery that is green on its own.
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        var member = SeedFallbackMember(factory, "DOC-RED-TIP", "docs/red-tip.md");
        PushToOriginDevelop("docs/drift.md", "red on its own\n");
        var tip = RemoteTip();
        _gate.Fallback = request => request.ExpectedSha == member.ResultSha
            ? Green(request.ExpectedSha)
            : Red(request.ExpectedSha);
        string? baseTested = null;
        _gate.Base = request =>
        {
            baseTested = request.ExpectedSha;
            return Red(request.ExpectedSha);
        };
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();

        await pilot.TickAsync(CancellationToken.None);

        Assert.Equal(2, _gate.FallbackRuns);
        Assert.Equal(1, _gate.BaseRuns);
        Assert.Equal(tip, baseTested);
        var ownership = Ownership(member.Key)!;
        Assert.True(ownership.FallbackOwnBase);
        Assert.True(ownership.FallbackIntegrated);
        Assert.Null(ownership.FallbackBaseSha);
        Assert.Equal($"batch-fallback-{member.ReviewAttemptId}-own-base.json", ownership.FallbackEvidencePath);
        Assert.Equal(3, Assert.Single(factory.Services.GetRequiredService<BatchGateStore>()
            .ListFallbackSuites(ProjectName)).PerTaskGateRuns);

        await PublishAndTickAsync(factory, pilot, member.Key);

        AssertLane(member.Key, TaskStates.HumanReview);
        Assert.Equal(2, _gate.FallbackRuns);
    }

    [Fact]
    public async Task Red_candidate_on_a_green_tip_escalates_without_an_own_base_retest()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        var member = SeedFallbackMember(factory, "DOC-GREEN-TIP", "docs/green-tip.md");
        PushToOriginDevelop("docs/drift.md", "green on its own\n");
        _gate.Fallback = request => Red(request.ExpectedSha);

        await factory.Services.GetRequiredService<BatchGatePilotService>().TickAsync(CancellationToken.None);

        Assert.Equal(1, _gate.FallbackRuns);
        Assert.Equal(1, _gate.BaseRuns);
        AssertLane(member.Key, TaskStates.Escalated);
        var fact = Assert.Single(factory.Services.GetRequiredService<BatchGateStore>().ListFallbackSuites(ProjectName));
        Assert.Equal("escalated", fact.Outcome);
        Assert.Equal(2, fact.Suites);
    }

    // A member queued while the batch was on, then routed to the per-task
    // gate because the project turned the batch off.
    private SeededMember SeedFallbackMember(WebApplicationFactory<Program> factory, string key, string file)
    {
        EnableBatchGate(factory, closeSize: 4);
        var member = SeedMember(factory, key, file);
        factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>()
            .SetBatchGate(ProjectName, new BatchGateFormationOptions(Enabled: false));
        return member;
    }

    // Build commands make the merge into develop run its own gate.
    private static void RequireMergeGate(WebApplicationFactory<Program> factory)
        => factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>()
            .SetBuildProfile(ProjectName, new BuildProfile { BuildCmds = ["cd ."] });

    // The fixture runs no push worker: publish the gated merge, then let the
    // next tick release the card through its containment record.
    private async Task PublishAndTickAsync(WebApplicationFactory<Program> factory,
        BatchGatePilotService pilot, string taskKey)
    {
        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        scanner.InvalidateCache();
        var task = scanner.FindJob(taskKey, _watchPath)!;
        var pushed = await factory.Services.GetRequiredService<MergeIntoDevelopRunner>()
            .PushIntegrationBranchAsync(task.ProjectName, task.Id, task.FolderPath, task.WatchPath,
                "develop", CancellationToken.None);
        Assert.True(pushed.Success, $"Integration publication failed: {pushed.Status} {pushed.Error}");
        await pilot.TickAsync(CancellationToken.None);
    }

    private void PushToOriginDevelop(string file, string content)
    {
        var restore = Git(_repo, "rev-parse", "HEAD");
        Git(_repo, "fetch", "-q", "origin");
        Git(_repo, "checkout", "-q", "--detach", "refs/remotes/origin/develop");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_repo, file))!);
        File.WriteAllText(Path.Combine(_repo, file), content);
        Git(_repo, "add", ".");
        Git(_repo, "commit", "-qm", "develop changes " + file);
        Git(_repo, "push", "-q", "origin", "HEAD:develop");
        Git(_repo, "fetch", "-q", "origin");
        Git(_repo, "checkout", "-q", "--detach", restore);
    }
}
