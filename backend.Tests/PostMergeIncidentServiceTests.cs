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
        var choices = Assert.Single(OwnerItems(project, store)).OwnerOptions;
        Assert.NotNull(choices);
        Assert.Equal(3, choices.Count);
        Assert.Equal(nameof(RedDevelopOwnerOption.KeepFixingForward), Assert.Single(choices, choice => choice.Recommended).Id);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Guardian_receipts_are_shadow_by_default_and_owner_live_switch_attributes_mutations(bool live)
    {
        var (project, clock, scanner, mutations, states, store, service) = Setup();
        var guardian = new AgentStudio.Personas.LineGuardianStore(Path.Combine(store.Root, "..", "line-guardian"));
        if (live)
        {
            guardian.SetLive(project.Id, false, "owner", true, clock.GetUtcNow().AddDays(-7));
            guardian.SetLive(project.Id, true, "owner", true, clock.GetUtcNow());
        }
        Record(store, service, project, TypedRed());
        var entry = Assert.Single(service.ActiveQuarantines(project));
        var expected = live ? AgentStudio.Personas.LineGuardianStore.Principal(project.Id) : "system:post-merge";
        Assert.Equal(expected, entry.StartedBy);
        Assert.Equal(expected, scanner.FindJob(entry.Card, project.StorageLocation)!.CreatedBy);
        Assert.Equal(expected, Assert.Single(Quarantines(store).Read(project.Id).Audit).Actor);
        var comparison = Assert.Single(guardian.Read(project.Id).Decisions);
        Assert.Equal(!live, comparison.Shadow);
        Assert.Equal(expected, Assert.Single(Quarantines(store).Read(project.Id).IncidentLog).Principal);
        var request = service.RequestGuardianOperation(project, "red", "Demo.Tests.Fails", "quarantine.lift");
        Assert.NotNull(request);
        Assert.Null(entry.LiftedAtUtc);
    }

    [Fact]
    public void Guardian_base_quarantine_request_creates_owner_item_without_applying_it()
    {
        var (project, clock, scanner, mutations, states, store, service) = Setup();
        Record(store, service, project, TypedRed(category: "base"));
        var item = service.RequestGuardianOperation(project, "red", "Demo.Tests.Fails", "quarantine.hold.start");
        Assert.NotNull(item);
        Assert.Equal("Refuse", item.Recommended);
        Assert.Equal(1, item.UseCap);
        Assert.Equal(clock.GetUtcNow().AddHours(24), item.ExpiresAt);
        Assert.Empty(service.ActiveQuarantines(project));
    }

    [Theory]
    [InlineData("green")]
    [InlineData("inconclusive")]
    public void Running_and_terminal_receipts_each_have_a_guardian_observation(string terminalStatus)
    {
        var (project, clock, scanner, mutations, states, store, service) = Setup();
        var green = Green("green", clock.GetUtcNow());
        service.Apply(project, green with { Status = "running", FinishedAtUtc = null });
        service.Apply(project, green with { Status = terminalStatus });
        var guardian = new AgentStudio.Personas.LineGuardianStore(Path.Combine(store.Root, "..", "line-guardian"));
        Assert.Equal(2, guardian.Read(project.Id).Decisions.Count);
        Assert.Equal(2, guardian.Read(project.Id).Decisions.Select(d => d.Log.DecisionId).Distinct().Count());
    }

    [Fact]
    public void Routine_twice_red_is_green_with_one_held_entry_one_first_repair_and_one_log()
    {
        var (project, clock, scanner, mutations, states, store, service) = Setup();
        mutations.CreateJob(new AgentStudio.Shared.CreateTaskRequest
            { Project = project.Id, Title = "Waiting", TargetState = TaskStates.Preparation, Order = -10 });
        var red = TypedRed();
        Record(store, service, project, red);
        service.Apply(project, red);
        var entry = Assert.Single(service.ActiveQuarantines(project));
        Assert.Equal("held", entry.Kind);
        Assert.Equal("green", store.Read(project.Id).DevelopState);
        Assert.Null(store.Read(project.Id).LineStop);
        var repair = scanner.FindJob(entry.Card, project.StorageLocation)!;
        Assert.Equal("Repair red test: Demo.Tests.Fails", repair.Title);
        Assert.Equal(TaskStates.Preparation, repair.State);
        Assert.Equal(repair.Id, scanner.ScanAllJobs().Where(card => card.State == TaskStates.Preparation).MinBy(card => card.Order)!.Id);
        var quarantine = Quarantines(store).Read(project.Id);
        Assert.Single(quarantine.IncidentLog);
        Assert.True(quarantine.IncidentLog[0].Reason.Length <= 280);
        Assert.Single(quarantine.Audit);
    }

    [Fact]
    public void Repair_rung_uses_the_project_provider_override_from_the_configured_tiers()
    {
        var (project, clock, scanner, mutations, states, store, service) = Setup();
        project = project with { CliDefault = CliTypes.Claude };
        Record(store, service, project, TypedRed());
        var repair = scanner.FindJob(Assert.Single(service.ActiveQuarantines(project)).Card, project.StorageLocation)!;
        Assert.Equal(CliTypes.Claude, repair.CliType);
        var expected = new ModelRoutingPolicyRegistry().Policy.Tiers.Single(tier => tier.Rank == 2).VendorOverrides["anthropic"];
        Assert.Equal(expected.Model, repair.Model);
        Assert.Equal(expected.ThinkingLevel, repair.ThinkingLevel);
    }

    [Fact]
    public void Base_twice_red_stops_the_line_and_asks_after_sixty_minutes()
    {
        var (project, clock, scanner, mutations, states, store, service) = Setup();
        var red = TypedRed(category: "base");
        Record(store, service, project, red);
        Assert.Empty(service.ActiveQuarantines(project));
        Assert.Equal("red", store.Read(project.Id).DevelopState);
        Assert.NotNull(store.Read(project.Id).LineStop);
        clock.Advance(TimeSpan.FromHours(1));
        service.AskOwnerWhenOverdue(project);
        service.AskOwnerWhenOverdue(project);
        var incident = Assert.Single(store.Read(project.Id).Incidents!);
        var question = Assert.Single(incident.OwnerQuestions);
        Assert.Equal("LineStopAge", question.Reason);
        Assert.Equal("KeepFixingForward", question.Recommended);
        Assert.Single(OwnerItems(project, store));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Lift_requires_two_covering_greens_containing_the_repair_and_relapse_asks_the_owner(bool containsRepair)
    {
        var (project, clock, scanner, mutations, states, store, service) = Setup(containsRepair);
        Record(store, service, project, TypedRed());
        var entry = Assert.Single(service.ActiveQuarantines(project));
        clock.Advance(TimeSpan.FromMinutes(1));
        Record(store, service, project, Green("unrelated", clock.GetUtcNow(), "other-suite"));
        Record(store, service, project, Green("green-1", clock.GetUtcNow()));
        Assert.Single(service.ActiveQuarantines(project));
        clock.Advance(TimeSpan.FromMinutes(1));
        Record(store, service, project, Green("green-2", clock.GetUtcNow()));
        if (!containsRepair)
        {
            Assert.Single(service.ActiveQuarantines(project));
            return;
        }
        Assert.Empty(service.ActiveQuarantines(project));
        var lifted = Assert.Single(Quarantines(store).Read(project.Id).Entries);
        Assert.Equal(["green-1", "green-2"], lifted.LiftEvidence);
        clock.Advance(TimeSpan.FromDays(1));
        Record(store, service, project, TypedRed() with
            { Id = "relapse", StartedAtUtc = clock.GetUtcNow(), FinishedAtUtc = clock.GetUtcNow() });
        var relapse = Assert.Single(service.ActiveQuarantines(project));
        Assert.NotEqual(entry.Card, relapse.Card);
        var incident = store.Read(project.Id).Incidents!.Single(item => item.ResolvedAtUtc is null);
        Assert.Equal("ReholdNewCardHigherRung", Assert.Single(incident.OwnerQuestions).Recommended);
    }

    [Fact]
    public void Sharded_covering_receipts_lift_even_when_filters_are_rewritten()
    {
        var (project, clock, scanner, mutations, states, store, service) = Setup(containsRepair: true);
        Record(store, service, project, TypedRed());
        foreach (var id in new[] { "shard-1", "shard-2" })
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            Record(store, service, project, Green(id, clock.GetUtcNow()) with
            {
                Verdicts = [new("dotnet test --settings /tmp/shard.runsettings", 0, false) { SuiteId = "backend-routine" }],
                SuiteSelection = new(["backend-routine"], [], [])
                    { SuiteCommands = new Dictionary<string, string> { ["backend-routine"] = "dotnet test --filter Category!=MachineBound" } },
            });
        }
        Assert.Empty(service.ActiveQuarantines(project));
    }

    [Fact]
    public void Fourth_held_test_stops_line_and_raises_the_cap_owner_question()
    {
        var (project, clock, scanner, mutations, states, store, service) = Setup();
        for (var i = 1; i <= 4; i++)
        {
            var red = TypedRed(test: "Tests.Fail" + i) with { Id = "red-" + i };
            Record(store, service, project, red);
        }
        Assert.Equal(3, service.ActiveQuarantines(project).Count);
        Assert.NotNull(store.Read(project.Id).LineStop);
        var question = Assert.Single(store.Read(project.Id).Incidents!.SelectMany(item => item.OwnerQuestions));
        Assert.Equal("CapExceeded", question.Reason);
        Assert.Equal("StopLine", question.Recommended);
    }

    [Fact]
    public void Flaky_third_distinct_run_starts_report_only_and_environment_remains_inconclusive()
    {
        var (project, clock, scanner, mutations, states, store, service) = Setup();
        for (var i = 1; i <= 3; i++)
        {
            var run = TypedRed() with { Id = "flaky-" + i, Status = "green", Failure = null,
                Verdicts = [new("dotnet test", 0, false, FlakyTests: ["Demo.Tests.Fails"])],
                TestVerdicts = [new("Demo.Tests.Fails", "backend-routine", "routine",
                    [new("initial", "fail", null, Tip), new("repeat", "pass", 1, Tip)], "flaky")] };
            Record(store, service, project, run);
            if (i < 3) Assert.Empty(service.ActiveQuarantines(project));
        }
        Assert.Equal("report-only", Assert.Single(service.ActiveQuarantines(project)).Kind);
        Assert.Equal("green", store.Read(project.Id).DevelopState);
        var environment = TypedRed(test: "Environment.Fail") with { Id = "environment", Status = "inconclusive", EnvelopeCanaryPassed = false };
        Record(store, service, project, environment);
        Assert.Equal("environment", store.Read(project.Id).InconclusiveRunId);
        Assert.Null(store.Read(project.Id).LineStop);
        var guardian = new AgentStudio.Personas.LineGuardianStore(Path.Combine(store.Root, "..", "line-guardian"));
        var comparison = Assert.Single(guardian.Read(project.Id).Decisions, item => item.Log.RunId == environment.Id
            && item.Log.Test == "Environment.Fail");
        Assert.Equal(RedTestClassification.Environment, comparison.Applied.Classification);
        Assert.Equal(RedTestClassification.Environment, comparison.Guardian.Classification);
    }

    [Fact]
    public void Ladder_renews_once_while_repair_is_in_progress_then_asks_at_forty_eight_hours_even_after_green()
    {
        var (project, clock, scanner, mutations, states, store, service) = Setup();
        Record(store, service, project, TypedRed());
        var entry = Assert.Single(service.ActiveQuarantines(project));
        states.MoveJob(entry.Card, TaskStates.Ready, project.StorageLocation);
        states.MoveJob(entry.Card, TaskStates.Progress, project.StorageLocation);
        clock.Advance(TimeSpan.FromMinutes(1));
        Record(store, service, project, Green("unrelated", clock.GetUtcNow(), "other-suite"));
        clock.Advance(TimeSpan.FromHours(24));
        service.AskOwnerWhenOverdue(project);
        Assert.Equal(1, Quarantines(store).Read(project.Id).Entries.Single().Renewals);
        clock.Advance(TimeSpan.FromHours(24));
        service.AskOwnerWhenOverdue(project);
        service.AskOwnerWhenOverdue(project);
        var question = Assert.Single(store.Read(project.Id).Incidents!.Single().OwnerQuestions);
        Assert.Equal("RenewalLimit", question.Reason);
        Assert.Equal("ReportOnlyWithCard", question.Recommended);
    }

    private static TestQuarantineStore Quarantines(PostMergeRunStore store)
        => new(Path.Combine(store.Root, "..", "test-quarantines"));
    private static void Record(PostMergeRunStore store, PostMergeIncidentService service, ProjectRecord project, PostMergeRunRecord run)
    {
        Assert.NotNull(store.Record(project.Id, run with { Status = "running", Verdicts = [], FinishedAtUtc = null }));
        Assert.NotNull(store.Record(project.Id, run));
        service.Apply(project, run);
    }
    private static PostMergeRunRecord Green(string id, DateTimeOffset now, string suite = "backend-routine")
        => Red() with { Id = id, Status = "green", StartedAtUtc = now, FinishedAtUtc = now,
            Failure = null, Verdicts = [new("dotnet test", 0, false)],
            SuiteSelection = new([suite], [], []) { SuiteCommands = new Dictionary<string, string> { [suite] = "dotnet test" } } };
    private static PostMergeRunRecord TypedRed(string category = "routine", string test = "Demo.Tests.Fails")
        => Red() with { TestVerdicts = [new(test, "backend-routine", category,
            [new("initial", "fail", null, Tip), new("repeat", "fail", 10, Tip)], "ambiguous")
            { CoveringFolders = ["backend/Features/Pipeline"] }] };

    private static PostMergeRunRecord Red() => new("red", "demo", "window", Tip, null, Now, Now, 1,
        ["dotnet test"], [new("dotnet test", 1, false)], "red", Now, Now,
        Failure: new("fingerprint", ["Demo.Tests.Fails"], "failure", [], [], null, false, false));

    private (ProjectRecord Project, FakeTimeProvider Clock, TaskScannerService Scanner,
        TaskMutationService Mutations, TaskStateMachine States, PostMergeRunStore Store,
        PostMergeIncidentService Service) Setup(bool containsRepair = false)
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
            NullLogger<PostMergeIncidentService>.Instance, containsRepair: (_, _, _) => containsRepair);
        return (project, clock, scanner, mutations, states, store, service);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
