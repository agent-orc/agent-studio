using System.Net.Http.Json;
using AgentStudio.Operator;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Fleet incidents replayed through the existing batch-pilot service harness.
/// Scheduler passes and agent replies are explicit; simulated hours never sleep.
/// Real Git children require MachineBound even though domain time is controlled.
/// </summary>
[Trait("Category", "MachineBound")]
public sealed class FleetPipelineScenarioTests : BatchGatePilotServiceTests
{
    [Fact(DisplayName = "1. Nobody is claimed for two days")]
    public async Task Nobody_is_claimed_for_two_days()
    {
        using var factory = BuildScenarioFactory();
        var host = CreateScenarioHost(factory, slots: 32);
        SeedReady(factory, "RUNNING", "Edit docs/running.md. See docs/system/contracts/.", state: TaskStates.Progress);
        host.AdoptRunning(Card(factory, "RUNNING"));
        for (var i = 1; i <= 19; i++)
            SeedReady(factory, $"READY-{i}", $"Edit docs/card-{i}.md. See docs/system/contracts/.");

        await host.PollAsync();
        Assert.True(host.Started.Count > 1, "Incidental docs/ citations must not serialize nineteen Ready cards.");

        // A second wave deliberately touches the running card's exact file.
        // All candidates are rejected with capacity still free. This exercises
        // the independent liveness obligation even after the path bug is fixed.
        for (var i = 1; i <= 19; i++)
            SeedReady(factory, $"HELD-{i}", "Edit docs/running.md.");
        await host.PollAsync();
        Assert.Empty(host.Attention());
        for (var pass = 1; pass <= 48 * 12; pass++)
        {
            _clock = _clock.AddMinutes(5);
            host.Heartbeat();
            await host.PollAsync();
            var attention = host.Attention();
            if (pass >= 6)
                Assert.Contains(attention, item => item.Type == OperatorDecisionTypes.OverlapStarved);
        }
        Assert.Equal(20, host.Started.Count);
        Assert.Equal(19, factory.Services.GetRequiredService<TaskScannerService>().ScanAllJobs()
            .Count(card => card.State == TaskStates.Ready));
    }

    [Fact(DisplayName = "2. Strategy switch with a full batch gate")]
    public async Task Strategy_switch_with_a_full_batch_gate()
    {
        using var factory = BuildScenarioFactory();
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        WithBuildStep();
        EnableBatchGate(factory, 8, "code");
        var members = Enumerable.Range(1, 8)
            .Select(i => SeedMember(factory, $"SWITCH-{i}", $"src/switch-{i}.cs", settleReview: false)).ToArray();
        await PassReviewsAsync(http, members);
        Assert.Equal(8, factory.Services.GetRequiredService<BatchGateStore>().ListPending().Count);
        _gate.Merge = request => Green(request.ExpectedSha);
        _gate.Fallback = request =>
        {
            Assert.Equal(TestExecutionLevels.CompileOnly, request.RequiredTestLevel);
            return Green(request.ExpectedSha);
        };

        using var switched = await http.PutAsJsonAsync($"/api/projects/{ProjectName}/testing", new { strategy = "fast" });
        switched.EnsureSuccessStatusCode();
        for (var hour = 0; hour < 4; hour++)
        {
            await factory.Services.GetRequiredService<BatchGatePilotService>().TickAsync(default);
            _clock = _clock.AddHours(1);
        }

        Assert.Empty(factory.Services.GetRequiredService<BatchGateStore>().ListPending());
        Assert.Equal(0, _gate.BatchRuns);
        Assert.NotEmpty(_gate.Requests);
        Assert.All(_gate.Requests, request => Assert.Equal(TestExecutionLevels.CompileOnly, request.RequiredTestLevel));
        foreach (var member in members)
        {
            AssertLane(member.Key, TaskStates.HumanReview);
            AssertIntegrated(factory, member.Key);
        }
        AssertNoEscalations(factory);
    }

    [Fact(DisplayName = "3. The conflicting chain head")]
    public async Task The_conflicting_chain_head()
    {
        using var factory = BuildScenarioFactory();
        var host = CreateScenarioHost(factory);
        EnableBatchGate(factory, 2);
        var head = SeedMember(factory, "CHAIN-HEAD", "README.md", content: "Delivery changed this line.\n");
        var sibling = SeedMember(factory, "INDEPENDENT", "docs/independent.md");
        SeedReady(factory, "CHILD-A", "Edit src/child-a.cs.", [head.Key]);
        SeedReady(factory, "CHILD-B", "Edit src/child-b.cs.", [head.Key]);
        _gate.Batch = request => Green(request.ExpectedSha);
        _gate.Fallback = request => Green(request.ExpectedSha);
        _gate.Merge = request => Green(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();

        // Three integration opportunities against moving develop. Healthy
        // scheduling sends one agent round, then stops replaying that unchanged
        // delivery while unrelated work continues through the lane.
        for (var round = 1; round <= 3; round++)
        {
            AdvanceOriginDevelop(round == 1 ? "README.md" : $"docs/develop-moved-{round}.md");
            AdvanceScenarioHour(host);
            await pilot.TickAsync(default);
            await host.PollAsync();
            Assert.DoesNotContain("CHILD-A", host.Started);
            Assert.DoesNotContain("CHILD-B", host.Started);
        }

        var card = Card(factory, head.Key);
        var rounds = factory.Services.GetRequiredService<TimelineLog>().ReadAll(card.FolderPath)
            .Where(entry => entry.Kind == TimelineEventKinds.IntegrationRecoveryQueued).ToArray();
        Assert.Single(rounds);
        Assert.Equal("true", rounds[0].Details!["automatic"]);
        Assert.Contains("README.md", host.Steers[head.Key]);
        Assert.Contains(head.Key, host.Started);
        Assert.DoesNotContain(factory.Services.GetRequiredService<BatchGateStore>().ListPending(),
            item => item.Subject.TaskKey == head.Key);
        AssertIntegrated(factory, sibling.Key);
        AssertNoEscalations(factory);
    }

    [Fact(DisplayName = "4. Red develop overnight")]
    public async Task Red_develop_overnight()
    {
        using var factory = BuildScenarioFactory();
        var host = CreateScenarioHost(factory);
        var project = factory.Services.GetRequiredService<ProjectRegistry>()
            .EnsureProjectForStorage(_watchPath, ProjectName, DefaultWorkspace.Id,
                factory.Services.GetRequiredService<TimeProvider>());
        var store = factory.Services.GetRequiredService<PostMergeRunStore>();
        var service = factory.Services.GetRequiredService<PostMergeIncidentService>();
        var tip = RemoteTip();
        var tests = Enumerable.Range(1, 8).Select(i => $"Fleet.Tests.Red{i}").ToArray();
        var red = new PostMergeRunRecord("overnight-red", project.Id, "overnight", tip, null, _clock, _clock, 1,
            ["dotnet test"], [new("dotnet test", 1, false)], "red", _clock, _clock,
            Failure: new("eight-tests", tests, "Eight confirmed failing tests", [], [], null, false, false))
        {
            TestVerdicts = tests.Select(test => new PostMergeTestVerdict(test, "backend-routine", "routine",
                [new("initial", "fail", null, tip), new("repeat", "fail", 10, tip)], "ambiguous")
                { CoveringFolders = ["backend/Features/Fleet"] }).ToArray(),
        };
        store.Record(project.Id, red with { Status = "running", FinishedAtUtc = null });
        Assert.NotNull(store.Record(project.Id, red));
        service.Apply(project, red);
        for (var hour = 1; hour <= 12; hour++)
        {
            _clock = _clock.AddHours(1);
            service.AskOwnerWhenOverdue(project);
            service.Apply(project, red); // Replay the persisted receipt after each poll/restart opportunity.
        }

        var repair = Assert.Single(factory.Services.GetRequiredService<TaskScannerService>().ScanAllJobs(),
            card => card.CreationSource == HumanReviewEscalationCategories.PostMergeRepairCreationSource);
        Assert.Equal(8, store.Read(project.Id).Incidents!.Count);
        Assert.All(store.Read(project.Id).Incidents!, incident => Assert.Equal(repair.Id, incident.RepairCardId));
        Assert.Equal(TaskStates.Ready, repair.State);
        var attention = new OperatorPostMergeRedIncidentFactSource(
            () => [(ProjectName, store.Read(project.Id))],
            cardState: (_, id) => factory.Services.GetRequiredService<TaskScannerService>().FindJob(id, _watchPath)?.State);
        Assert.Contains(OperatorAttention.Project(attention.Collect(_clock.UtcDateTime),
            OperatorDecisionReader.Everything).Items, item => item.Type == OperatorDecisionTypes.PostMergeRedIncident);
        await host.PollAsync();
        Assert.Contains(repair.Key ?? repair.Id, host.Started);
        AssertNoEscalations(factory);
    }

    [Fact(DisplayName = "5. Follow-up before its parent")]
    public async Task Follow_up_before_its_parent()
    {
        using var factory = BuildScenarioFactory();
        using var http = factory.CreateClient();
        var host = CreateScenarioHost(factory);
        WithBuildStep();
        EnableBatchGate(factory, 8, "code");
        var parent = SeedMember(factory, "PARENT", "src/parent.cs", settleReview: false);
        UseCanonicalScenarioStorage(factory, parent.Key);
        var source = Card(factory, parent.Key);
        var followUpKey = V1ReviewPlaneEndpoints.CreateReviewBudgetFollowUpCard(source, parent.ReviewAttemptId,
            [new("code-quality", "block", "quality:concerns", "A helper still needs a focused test.",
                EvidenceChecked: "src/parent.cs", Missing: "Add the helper test.")],
            new ReviewRoundBudgetDecision(3, 4, ["code-quality"], "code-quality"),
            factory.Services.GetRequiredService<TaskMutationService>(), factory.Services.GetRequiredService<TaskScannerService>());
        Assert.NotNull(followUpKey);
        for (var hour = 0; hour < 3; hour++)
        {
            await host.PollAsync();
            Assert.Empty(host.Started);
            _clock = _clock.AddHours(1);
        }

        await PassReviewsAsync(http, [parent]);
        factory.Services.GetRequiredService<ProjectSettingsService>().SetTestingStrategy(ProjectName, TestingStrategies.Fast);
        _gate.Merge = request => Green(request.ExpectedSha);
        await factory.Services.GetRequiredService<BatchGatePilotService>().TickAsync(default);
        AssertIntegrated(factory, parent.Key);
        await host.PollAsync();

        Assert.Equal([followUpKey!], host.Started);
        Assert.Equal(TaskStates.Progress, Card(factory, followUpKey!).State);
        AssertNoEscalations(factory);
    }

    [Fact(DisplayName = "6. Ready runs dry")]
    public async Task Ready_runs_dry()
    {
        using var factory = BuildScenarioFactory();
        var host = CreateScenarioHost(factory);
        await host.PollAsync();
        Assert.Empty(host.Started);
        Assert.Empty(host.Attention());
        for (var hour = 0; hour < 8; hour++)
        {
            _clock = _clock.AddHours(1);
            await host.PollAsync();
            var item = Assert.Single(host.Attention());
            Assert.Equal(OperatorDecisionTypes.CapacityUnderused, item.Type);
            Assert.Contains("Ready is empty", item.Reason);
        }
        Assert.Empty(host.Active);
    }

    private async Task PassReviewsAsync(HttpClient http, IReadOnlyList<SeededMember> members)
    {
        http.DefaultRequestHeaders.Remove("X-Client-Id");
        http.DefaultRequestHeaders.Add("X-Client-Id", ReviewRunnerId);
        await RegisterReviewExecutorAsync(http);
        var remaining = members.ToDictionary(member => member.ReviewAttemptId);
        for (var i = 0; i < members.Count; i++)
        {
            using var claimed = await http.PostAsJsonAsync($"/api/v1/runners/{ReviewRunnerId}/review-claims",
                new ReviewClaimRequest(ReviewRunnerId, ReviewInstance, 300, AvailableSlots: 1));
            claimed.EnsureSuccessStatusCode();
            var claim = (await claimed.Content.ReadFromJsonAsync<ReviewClaimResponse>())!;
            Assert.Equal("claimed", claim.Status);
            Assert.True(remaining.Remove(claim.Attempt!.AttemptId, out var member));
            Assert.NotNull(member);
            using var report = await http.PostAsJsonAsync($"/api/v1/reviews/attempts/{member.ReviewAttemptId}/report",
                PassingReport(claim, "scenario-pass-" + member.Key));
            Assert.True(report.IsSuccessStatusCode, await report.Content.ReadAsStringAsync());
        }
        Assert.Empty(remaining);
        http.DefaultRequestHeaders.Remove("X-Client-Id");
        http.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
    }

    private void AssertIntegrated(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, string key)
        => Assert.Equal(IntegrationStatuses.Integrated, factory.Services.GetRequiredService<TaskIntegrationStatusService>()
            .BuildForAcceptance(Card(factory, key)).Status);

    private static void AssertNoEscalations(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
        => Assert.DoesNotContain(factory.Services.GetRequiredService<TaskScannerService>().ScanAllJobs(),
            card => card.State == TaskStates.Escalated);
}
