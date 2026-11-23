using AgentStudio.OrchestrationDecisions;
using AgentStudio.Pipeline;
using AgentStudio.Registry;
using AgentStudio.Runner;
using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class PostMergeIncidentServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "post-merge-incidents-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private const string Tip = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IdenticalRedReceiptsCreateOneCardAndGreenRecordsRedTime(bool noRelevantMerges)
    {
        var (project, clock, scanner, mutations, states, store, service) = Setup();
        var projectPath = project.StorageLocation;
        var failure = new PostMergeFailureEvidence("fingerprint", ["Demo.Tests.Fails"], "Failed Demo.Tests.Fails",
            noRelevantMerges ? [] : ["backend/Foo.cs"], noRelevantMerges ? [] : [Tip], null, false, false,
            NoRelevantMerges: noRelevantMerges);
        var red = new PostMergeRunRecord("red", "demo", "window", Tip, null, Now, Now, 1,
            ["dotnet test"], [new("dotnet test", 1, false)], "red", Now, Now, Failure: failure);
        store.Record(project.Id, red with { Status = "running", Verdicts = [], FinishedAtUtc = null });
        Assert.NotNull(store.Record(project.Id, red));
        // The terminal receipt may persist before card creation; the scheduled
        // check must resume that step without creating a second card.
        service.AskOwnerWhenOverdue(project);
        service.Apply(project, red);
        service.Apply(project, red);

        var opened = Assert.Single(store.Read(project.Id).Incidents!);
        Assert.Single(scanner.ScanAllJobs(), card => card.Id == opened.RepairCardId);
        Assert.Equal(TaskStates.Preparation, scanner.FindJob(opened.RepairCardId, projectPath)!.State);
        Assert.Null(opened.RedMinutes);
        if (noRelevantMerges)
        {
            var repair = scanner.FindJob(opened.RepairCardId, projectPath)!;
            var prompt = File.ReadAllText(Path.Combine(repair.FolderPath, "prompt.md"));
            Assert.Contains("environmental or flaky suspect", prompt);
            Assert.Contains("No merge is blamed", prompt);
            Assert.Contains("Attributed merge: unresolved", prompt);
            Assert.DoesNotContain("Suspects: " + Tip, prompt);
        }

        clock.Advance(TimeSpan.FromHours(2));
        service.AskOwnerWhenOverdue(project);
        service.AskOwnerWhenOverdue(project);
        // RT-1 (AGT-3988): two hours ask the owner once; the repair keeps its lane.
        var asked = Assert.Single(store.Read(project.Id).Incidents!);
        Assert.False(asked.Escalated);
        Assert.Equal(Now.AddHours(2), asked.OwnerAskedAtUtc);
        Assert.Equal(nameof(RedDevelopOwnerOption.KeepFixingForward), asked.OwnerQuestion!.Recommended);
        Assert.Equal("red", asked.OwnerQuestion.RedRunId);
        Assert.Equal(TaskStates.Preparation, scanner.FindJob(opened.RepairCardId, projectPath)!.State);
        Assert.Null(store.Read(project.Id).AttributionEscalatedTaskId);
        Assert.DoesNotContain(ReviewDecisionLog.ReadAll(_root, project.DisplayName),
            record => record.JobId == opened.RepairCardId);
        Assert.Single(OwnerItems(project, store));
        clock.Advance(TimeSpan.FromMinutes(37));
        var green = red with { Id = "green", Status = "green", Verdicts = [new("dotnet test", 0, false)],
            StartedAtUtc = Now.AddMinutes(156), FinishedAtUtc = Now.AddMinutes(157), Failure = null };
        store.Record(project.Id, green with { Status = "running", Verdicts = [], FinishedAtUtc = null });
        Assert.NotNull(store.Record(project.Id, green));
        service.Apply(project, green);
        var closed = Assert.Single(store.Read(project.Id).Incidents!);
        Assert.Equal(157, closed.RedMinutes);
        Assert.Equal(green.FinishedAtUtc, closed.ResolvedAtUtc);
    }

    [Theory]
    [InlineData(TaskStates.AutoReview)]
    [InlineData(TaskStates.HumanReview)]
    [InlineData(TaskStates.Completed)]
    [InlineData(TaskStates.Archive)]
    public void Overdue_receipt_and_poll_leave_delivery_owned_repairs_alone_and_ask_the_owner_once(string lane)
    {
        var (project, clock, scanner, mutations, states, store, service) = Setup();
        var repair = mutations.CreateJob(new AgentStudio.Shared.CreateTaskRequest
        {
            Project = project.Id, Title = "Repair in delivery", TargetState = lane,
        })!;
        var red = Red();
        store.Record(project.Id, red with { Status = "running", FinishedAtUtc = null });
        store.Record(project.Id, red);
        store.UpdateIncidents(project.Id, _ => [new("fingerprint", repair, "FixForward", Now,
            null, null, [], [])]);
        clock.Advance(TimeSpan.FromHours(3));

        service.Apply(project, red);
        service.AskOwnerWhenOverdue(project);

        service.Apply(project, red);
        service.AskOwnerWhenOverdue(project);

        Assert.Equal(lane, scanner.FindJob(repair, project.StorageLocation)!.State);
        var incident = Assert.Single(store.Read(project.Id).Incidents!);
        Assert.False(incident.Escalated);
        Assert.NotNull(incident.OwnerAskedAtUtc);
        Assert.DoesNotContain(ReviewDecisionLog.ReadAll(_root, project.DisplayName), row => row.JobId == repair);
        var item = Assert.Single(OwnerItems(project, store));
        Assert.Equal(repair, item.CardKey);
        Assert.Contains("keep fixing forward", item.Reason);
        Assert.Contains("switch the project to strict", item.Reason);
        Assert.Contains(item.Evidence, link => link.Label == "latest red receipt");
    }

    [Fact]
    public void A_clean_independent_suspect_recommends_reverting_the_attributed_merge()
    {
        var (project, clock, scanner, mutations, states, store, service) = Setup();
        var red = Red() with
        {
            Failure = new("fingerprint", ["Demo.Tests.Fails"], "failure", ["backend/Foo.cs"], [Tip], Tip, true, false),
        };
        store.Record(project.Id, red with { Status = "running", FinishedAtUtc = null });
        store.Record(project.Id, red);
        service.Apply(project, red);
        clock.Advance(TimeSpan.FromHours(2));
        service.AskOwnerWhenOverdue(project);

        var incident = Assert.Single(store.Read(project.Id).Incidents!);
        Assert.Equal(nameof(RedDevelopRepairAction.Revert), incident.Action);
        Assert.Equal(nameof(RedDevelopOwnerOption.RevertAttributedMerge), incident.OwnerQuestion!.Recommended);
        Assert.Contains("Recommended: revert the attributed merge " + Tip[..12],
            Assert.Single(OwnerItems(project, store)).Reason);
    }

    [Fact]
    public void Each_incident_keeps_its_own_clock()
    {
        var (project, clock, scanner, mutations, states, store, service) = Setup();
        var first = Red();
        store.Record(project.Id, first with { Status = "running", FinishedAtUtc = null });
        store.Record(project.Id, first);
        service.Apply(project, first);
        clock.Advance(TimeSpan.FromMinutes(90));
        var second = first with
        {
            Id = "red-2", StartedAtUtc = clock.GetUtcNow(), FinishedAtUtc = clock.GetUtcNow(),
            Failure = first.Failure! with { Fingerprint = "other" },
        };
        store.Record(project.Id, second with { Status = "running", FinishedAtUtc = null });
        store.Record(project.Id, second);
        service.Apply(project, second);
        clock.Advance(TimeSpan.FromMinutes(30));
        service.AskOwnerWhenOverdue(project);

        var incidents = store.Read(project.Id).Incidents!;
        Assert.NotNull(incidents.Single(item => item.Fingerprint == "fingerprint").OwnerAskedAtUtc);
        Assert.Null(incidents.Single(item => item.Fingerprint == "other").OwnerAskedAtUtc);
        Assert.Single(OwnerItems(project, store));
    }

    [Theory]
    [InlineData(TaskStates.Completed, false)]
    [InlineData(TaskStates.Archive, false)]
    [InlineData(TaskStates.AutoReview, true)]
    [InlineData(TaskStates.HumanReview, true)]
    public void A_recurring_fingerprint_never_pulls_a_delivery_or_settled_repair_back(string lane, bool attaches)
    {
        var (project, clock, scanner, mutations, states, store, service) = Setup();
        var prior = mutations.CreateJob(new AgentStudio.Shared.CreateTaskRequest
        {
            Project = project.Id, Title = "Repair red develop: fingerprint", TargetState = lane,
            CreationSource = HumanReviewEscalationCategories.PostMergeRepairCreationSource,
        })!;
        var red = Red();
        store.Record(project.Id, red with { Status = "running", FinishedAtUtc = null });
        store.Record(project.Id, red);

        service.Apply(project, red);

        Assert.Equal(lane, scanner.FindJob(prior, project.StorageLocation)!.State);
        var incident = Assert.Single(store.Read(project.Id).Incidents!);
        if (attaches) Assert.Equal(prior, incident.RepairCardId);
        else
        {
            Assert.NotEqual(prior, incident.RepairCardId);
            Assert.Equal(TaskStates.Preparation, scanner.FindJob(incident.RepairCardId, project.StorageLocation)!.State);
        }
    }

    private static IReadOnlyList<AgentStudio.Operator.OperatorDecisionFact> OwnerItems(ProjectRecord project,
        PostMergeRunStore store)
        => new AgentStudio.Operator.OperatorPostMergeRedIncidentFactSource(
            () => [(project.DisplayName, store.Read(project.Id))]).Collect(Now.UtcDateTime);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_revert_recommendation_and_green_receipt_do_not_prove_the_original_was_reverted(bool alreadyResolved)
    {
        var (project, clock, scanner, mutations, states, store, service) = Setup();
        var original = mutations.CreateJob(new AgentStudio.Shared.CreateTaskRequest
        {
            Project = project.Id, Title = "Completed suspect", TargetState = TaskStates.Completed,
        })!;
        var repair = mutations.CreateJob(new AgentStudio.Shared.CreateTaskRequest
        {
            Project = project.Id, Title = "Fix forward despite old revert recommendation", TargetState = TaskStates.HumanReview,
        })!;
        store.UpdateIncidents(project.Id, _ => [new("fingerprint", repair, "Revert", Now,
            alreadyResolved ? Now : null, Tip, ["Demo.Tests.Fails"], [], OriginalTaskId: original)]);
        if (!alreadyResolved)
            service.Apply(project, Red() with { Status = "green", Failure = null, FinishedAtUtc = Now });
        service.AskOwnerWhenOverdue(project);

        Assert.Equal(TaskStates.Completed, scanner.FindJob(original, project.StorageLocation)!.State);
        Assert.False(Assert.Single(store.Read(project.Id).Incidents!).OriginalReopened);
    }

    private static PostMergeRunRecord Red() => new("red", "demo", "window", Tip, null, Now, Now, 1,
        ["dotnet test"], [new("dotnet test", 1, false)], "red", Now, Now,
        Failure: new("fingerprint", ["Demo.Tests.Fails"], "failure", [], [], null, false, false));

    private (ProjectRecord Project, FakeTimeProvider Clock, TaskScannerService Scanner,
        TaskMutationService Mutations, TaskStateMachine States, PostMergeRunStore Store,
        PostMergeIncidentService Service) Setup()
    {
        var projectPath = Path.Combine(_root, "projects", "demo");
        Directory.CreateDirectory(projectPath);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["WatchPaths:0:Name"] = "demo",
            ["WatchPaths:0:Path"] = projectPath,
            ["WatchPaths:0:RootPath"] = projectPath,
        }).Build();
        var clock = new FakeTimeProvider(Now);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var project = registry.EnsureProjectForStorage(projectPath, "demo", DefaultWorkspace.Id, clock);
        var mutations = new TaskMutationService(scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance), registry,
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance,
            new TimelineLog(NullLogger<TimelineLog>.Instance));
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var store = new PostMergeRunStore(Path.Combine(_root, "post-merge"));
        var service = new PostMergeIncidentService(store, mutations, scanner, states, clock,
            NullLogger<PostMergeIncidentService>.Instance);
        return (project, clock, scanner, mutations, states, store, service);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
