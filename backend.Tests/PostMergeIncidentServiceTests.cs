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

    [Fact]
    public void IdenticalRedReceiptsCreateOneCardAndGreenRecordsRedTime()
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
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config, prompts);
        var transitions = new TaskTransitionService(scanner, states, mutations, git, settings,
            NullLogger<TaskTransitionService>.Instance);
        var escalation = new HumanReviewEscalation(states, transitions, _root,
            NullLogger<HumanReviewEscalation>.Instance);
        var store = new PostMergeRunStore(Path.Combine(_root, "post-merge"));
        var service = new PostMergeIncidentService(store, mutations, scanner, states, escalation, clock,
            NullLogger<PostMergeIncidentService>.Instance);
        var failure = new PostMergeFailureEvidence("fingerprint", ["Demo.Tests.Fails"], "Failed Demo.Tests.Fails",
            ["backend/Foo.cs"], [Tip], null, false, false);
        var red = new PostMergeRunRecord("red", "demo", "window", Tip, null, Now, Now, 1,
            ["dotnet test"], [new("dotnet test", 1, false)], "red", Now, Now, Failure: failure);
        store.Record(project.Id, red with { Status = "running", Verdicts = [], FinishedAtUtc = null });
        Assert.NotNull(store.Record(project.Id, red));
        // The terminal receipt may persist before card creation; the scheduled
        // check must resume that step without creating a second card.
        service.EscalateOverdue(project);
        service.Apply(project, red);
        service.Apply(project, red);

        var opened = Assert.Single(store.Read(project.Id).Incidents!);
        Assert.Single(scanner.ScanAllJobs(), card => card.Id == opened.RepairCardId);
        Assert.Equal(TaskStates.Preparation, scanner.FindJob(opened.RepairCardId, projectPath)!.State);
        Assert.Null(opened.RedMinutes);

        clock.Advance(TimeSpan.FromHours(2));
        service.EscalateOverdue(project);
        Assert.True(Assert.Single(store.Read(project.Id).Incidents!).Escalated);
        Assert.Equal(TaskStates.Escalated, scanner.FindJob(opened.RepairCardId, projectPath)!.State);
        Assert.Contains(ReviewDecisionLog.ReadAll(_root, project.DisplayName),
            record => record.JobId == opened.RepairCardId && record.Kind == ReviewDecisionKind.Escalate);
        var escalatedCard = scanner.FindJob(opened.RepairCardId, projectPath)!;
        Assert.False(string.IsNullOrWhiteSpace(File.ReadAllText(Path.Combine(escalatedCard.FolderPath,
            "status.md"))));
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

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
