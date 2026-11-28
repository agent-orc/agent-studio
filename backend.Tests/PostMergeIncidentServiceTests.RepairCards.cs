using AgentStudio.Pipeline;
using AgentStudio.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed partial class PostMergeIncidentServiceTests
{
    [Fact]
    public void Repair_creation_uses_the_project_provider_and_default_model()
    {
        var (project, clock, scanner, mutations, states, store, service) = Setup();
        project = project with { CliDefault = CliTypes.Claude,
            ModelDefault = new ModelRoutingPolicyRegistry().Policy.Tiers.Single(tier => tier.Rank == 2).VendorOverrides["anthropic"].Model };
        Record(store, service, project, TypedRed());
        var repair = scanner.FindJob(Assert.Single(service.ActiveQuarantines(project)).Card, project.StorageLocation)!;
        Assert.Equal(CliTypes.Claude, repair.CliType);
        var expected = new ModelRoutingPolicyRegistry().Policy.Tiers.Single(tier => tier.Rank == 2).VendorOverrides["anthropic"];
        Assert.Equal(expected.Model, repair.Model);
        Assert.Equal("medium", repair.ThinkingLevel);
    }

    [Fact]
    public void Brief_keeps_structured_first_errors_when_the_console_tail_has_lost_them()
    {
        var (project, _, scanner, _, _, store, service) = Setup();
        var red = Red() with { Verdicts = [new("dotnet test", 1, false, new string('x', 2000), ["Demo.Tests.Fails"])
            { FailureMessages = new Dictionary<string, string> { ["Demo.Tests.Fails"] = "Expected healthy, actual unavailable" } }] };
        Record(store, service, project, red);
        var card = Assert.Single(scanner.ScanAllJobs());
        Assert.Contains("Demo.Tests.Fails: Expected healthy, actual unavailable",
            File.ReadAllText(Path.Combine(card.FolderPath, "prompt.md")));
    }

    [Fact]
    public void Typed_overlapping_red_sets_share_a_repair_but_keep_per_test_incidents()
    {
        var (project, _, scanner, _, _, store, service) = Setup();
        var red = TypedRed();
        var firstTest = red.TestVerdicts!.Single();
        red = red with { Failure = red.Failure! with { FailingTests = [firstTest.Test, "Demo.Tests.Second"] },
            TestVerdicts = [firstTest, firstTest with { Test = "Demo.Tests.Second" }] };
        Record(store, service, project, red);
        Assert.Equal(2, store.Read(project.Id).Incidents!.Count);
        var card = Assert.Single(scanner.ScanAllJobs());
        var next = red with { Id = "next", Failure = red.Failure! with { Fingerprint = "changed",
                FailingTests = [firstTest.Test, "Demo.Tests.Second", "Demo.Tests.Third"] },
            TestVerdicts = [.. red.TestVerdicts!, firstTest with { Test = "Demo.Tests.Third" }] };
        Record(store, service, project, next);
        Assert.Equal(card.Id, Assert.Single(scanner.ScanAllJobs()).Id);
        Assert.Equal(3, store.Read(project.Id).Incidents!.Count);
        Assert.All(store.Read(project.Id).Incidents!, item => Assert.Equal(card.Id, item.RepairCardId));
    }

    [Fact]
    public void Refreshing_an_old_queued_repair_recovers_it_from_preparation()
    {
        var (project, _, scanner, mutations, _, store, service) = Setup();
        var id = mutations.CreateJob(new AgentStudio.Shared.CreateTaskRequest
        { Project = project.Id, Title = "Old repair", TargetState = TaskStates.Preparation })!;
        store.UpdateIncidents(project.Id, _ => [new("fingerprint", id, "FixForward", Now, null, null,
            ["Demo.Tests.Fails"], [])]);
        Record(store, service, project, Red());
        Assert.Equal(TaskStates.Ready, scanner.FindJob(id, project.StorageLocation)!.State);
        Assert.Equal(id, Assert.Single(scanner.ScanAllJobs()).Id);
    }

    [Theory]
    [InlineData("gpt-6.1-sol")]
    [InlineData("gpt-5.4-mini")]
    public void Repair_creation_uses_ready_first_and_the_current_project_coding_default(string model)
    {
        var (project, _, scanner, mutations, _, store, service) = Setup();
        project = project with { CliDefault = "codex", ModelDefault = model };
        mutations.CreateJob(new AgentStudio.Shared.CreateTaskRequest
        { Project = project.Id, Title = "Existing ready card", TargetState = TaskStates.Ready, Order = 0 });
        Record(store, service, project, Red());
        var incident = Assert.Single(store.Read(project.Id).Incidents!);
        var card = scanner.FindJob(incident.RepairCardId, project.StorageLocation)!;
        Assert.Equal(TaskStates.Ready, card.State);
        Assert.Equal(card.Id, scanner.ScanAllJobs().Where(c => c.State == TaskStates.Ready).MinBy(c => c.Order)!.Id);
        Assert.Equal(project.ModelDefault, card.Model);
        Assert.Equal("medium", card.ThinkingLevel);
        Assert.True(card.ModelExplicit);
        Assert.True(card.ThinkingLevelExplicit);
    }

    [Fact]
    public void Overlapping_red_fingerprints_refresh_one_card_and_keep_the_first_red_clock()
    {
        var (project, clock, scanner, _, _, store, service) = Setup();
        Record(store, service, project, Red());
        var first = Assert.Single(store.Read(project.Id).Incidents!);
        clock.Advance(TimeSpan.FromMinutes(5));
        var next = Red() with { Id = "next", TipSha = new string('b', 40), FinishedAtUtc = clock.GetUtcNow(),
            Failure = Red().Failure! with { Fingerprint = "changed", FailingTests = ["Demo.Tests.Fails", "Demo.Tests.NewFailure"],
                LogExcerpt = "Failed Demo.Tests.NewFailure\nError Message:\nExpected 2, actual 3", SuspectMerges = [new string('c', 40)] } };
        Record(store, service, project, next);
        service.Apply(project, next);
        var updated = Assert.Single(store.Read(project.Id).Incidents!);
        Assert.Equal(first.RepairCardId, updated.RepairCardId);
        Assert.Equal(first.FirstRedAtUtc, updated.FirstRedAtUtc);
        Assert.Equal(next.Failure!.FailingTests, updated.FailingTests);
        var card = Assert.Single(scanner.ScanAllJobs());
        var prompt = File.ReadAllText(Path.Combine(card.FolderPath, "prompt.md"));
        Assert.Contains(next.TipSha, prompt);
        Assert.Contains("Demo.Tests.NewFailure: Expected 2, actual 3", prompt);
        Assert.Contains(new string('c', 40), prompt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Green_closes_only_repairs_that_never_started(bool started)
    {
        var (project, _, scanner, _, states, store, service) = Setup();
        Record(store, service, project, Red());
        var id = store.Read(project.Id).Incidents!.Single().RepairCardId;
        var card = scanner.FindJob(id, project.StorageLocation)!;
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        if (started)
            timeline.Append(card.FolderPath, TimelineEventKinds.AgentRunStarted, TimelineActors.System, "Started coding");
        Record(store, service, project, Green("green", Now.AddMinutes(1)) with { SuiteSelection = null });
        card = scanner.FindJob(id, project.StorageLocation)!;
        Assert.Equal(started ? TaskStates.Ready : TaskStates.Archive, card.State);
        if (!started) Assert.Contains(timeline.ReadAll(card.FolderPath), item => item.Kind == TimelineEventKinds.LaneChanged
            && item.Details!.Values.Any(value => value.Contains("green", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Pickup_attention_appears_at_ten_minutes_and_clears_when_running_or_green()
    {
        var (project, _, scanner, _, states, store, service) = Setup();
        Record(store, service, project, Red());
        var id = store.Read(project.Id).Incidents!.Single().RepairCardId;
        var source = new AgentStudio.Operator.OperatorPostMergeRedIncidentFactSource(
            () => [(project.DisplayName, store.Read(project.Id))],
            cardState: (_, card) => scanner.FindJob(card, project.StorageLocation)?.State);
        Assert.Empty(source.Collect(Now.AddMinutes(9).UtcDateTime));
        var item = Assert.Single(source.Collect(Now.AddMinutes(10).UtcDateTime));
        Assert.Contains("develop is red since", item.Title);
        Assert.Contains("repair card", item.Title);
        Assert.Contains(TaskStates.Ready, item.Title);
        states.MoveJob(id, TaskStates.Progress, project.StorageLocation);
        Assert.Empty(source.Collect(Now.AddMinutes(11).UtcDateTime));
        Record(store, service, project, Green("green", Now.AddMinutes(12)) with { SuiteSelection = null });
        Assert.Empty(source.Collect(Now.AddMinutes(13).UtcDateTime));
    }

    [Theory]
    [InlineData(TaskStates.Preparation)]
    [InlineData(TaskStates.Ready)]
    [InlineData(TaskStates.Progress)]
    [InlineData(TaskStates.AutoReview)]
    [InlineData(TaskStates.HumanReview)]
    [InlineData(TaskStates.Completed)]
    [InlineData(TaskStates.Archive)]
    public void Typed_overdue_poll_recovers_only_queued_repairs_without_another_receipt(string lane)
    {
        var (project, _, scanner, mutations, _, store, service) = Setup();
        var id = mutations.CreateJob(new AgentStudio.Shared.CreateTaskRequest
            { Project = project.Id, Title = "Old typed repair", TargetState = lane })!;
        store.UpdateIncidents(project.Id, _ => [new("fingerprint", id, "FixForward", Now, null, null,
            ["Demo.Tests.Fails"], []) { TestId = "Demo.Tests.Fails", SuiteId = "backend-routine" }]);
        var red = TypedRed();
        Assert.NotNull(store.Record(project.Id, red with { Status = "running", FinishedAtUtc = null }));
        Assert.NotNull(store.Record(project.Id, red));
        service.AskOwnerWhenOverdue(project);
        service.AskOwnerWhenOverdue(project);
        Assert.Equal(lane == TaskStates.Preparation ? TaskStates.Ready : lane,
            scanner.FindJob(id, project.StorageLocation)!.State);
        Assert.Single(scanner.ScanAllJobs());
    }
}
