using AgentStudio.Runner;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// DS-0 (decision DS-D10 B of docs/dispatcher-personas/index.html): the shared
/// prompt-path scope, the overlap window rule of <see cref="ParallelSlotPolicy"/>
/// and the in-flight comparison set the local pickup and the remote claim use.
/// </summary>
public sealed class OverlapWindowTests
{
    private const string Project = "demo";
    private static readonly DateTime Start = new(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc);

    private static TaskParallelism Scope(params string[] paths) => new(false, paths);

    // ---- TaskScopePrediction: unchanged behaviour of the former ProjectRunner.PredictParallelism

    [Fact]
    public void Prediction_keeps_every_named_file_path()
    {
        var p = TaskScopePrediction.FromText(
            "Fix backend/Features/Runner/ProjectRunner.cs and docs/system/domains/runner.md, "
            + "see frontend/src/app/app.ts and Backend/Features/Runner/ActiveRuns.cs.");

        Assert.False(p.Exclusive);
        Assert.False(p.ReadOnly);
        Assert.Equal(
            ["backend/Features/Runner/ProjectRunner.cs", "docs/system/domains/runner.md", "frontend/src/app/app.ts", "Backend/Features/Runner/ActiveRuns.cs"],
            p.PredictedScope);
    }

    [Fact]
    public void Prediction_without_a_named_path_is_unknown_scope()
    {
        Assert.Empty(TaskScopePrediction.FromText("Make the board faster.").PredictedScope);
        Assert.Empty(TaskScopePrediction.FromText(null).PredictedScope);
        // A root without a file below it names no path.
        Assert.Empty(TaskScopePrediction.FromText("Touches backend only.").PredictedScope);
    }

    [Fact]
    public void Prediction_keeps_at_most_24_paths_and_ignores_top_level_folders()
    {
        var text = string.Join(" ", Enumerable.Range(0, 30).Select(i => $"tools/d{i}/file.sh"));
        var p = TaskScopePrediction.FromText(text);
        Assert.Equal(24, p.PredictedScope.Count);
        Assert.Equal("tools/d0/file.sh", p.PredictedScope[0]);

        Assert.Empty(TaskScopePrediction.FromText("see src/readme").PredictedScope);
    }

    [Fact]
    public void Prediction_reads_title_and_prompt_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "atp-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "prompt.md"), "Edit frontend/src/app/a.ts");
            var p = TaskScopePrediction.Predict(new TaskInfo { Title = "docs/x/y.md", FolderPath = dir });
            Assert.Equal(["docs/x/y.md", "frontend/src/app/a.ts"], p.PredictedScope);
            Assert.Empty(TaskScopePrediction.Predict(new TaskInfo { FolderPath = Path.Combine(dir, "missing") })
                .PredictedScope);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- OverlapWindows setting

    [Fact]
    public void Overlap_window_defaults_to_advisory_off()
    {
        Assert.Equal(OverlapWindows.Off, OverlapWindows.Default);
        Assert.Equal(OverlapWindows.Off, OverlapWindows.Resolve(new ProjectSettings()));
        Assert.Equal(OverlapWindows.Off, OverlapWindows.Resolve(null));
        Assert.Equal(OverlapWindows.Off,
            OverlapWindows.Resolve(new ProjectSettings { DispatchOverlapWindow = "bogus" }));
        Assert.Equal(OverlapWindows.Running,
            OverlapWindows.Resolve(new ProjectSettings { DispatchOverlapWindow = " Running " }));
        Assert.False(OverlapWindows.IsKnown("all"));
    }

    [Fact]
    public void Overlap_window_round_trips_under_its_dotted_name()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            new ProjectSettings { DispatchOverlapWindow = OverlapWindows.Running });
        Assert.Contains("\"dispatch.overlapWindow\":\"running\"", json);
        var back = System.Text.Json.JsonSerializer.Deserialize<ProjectSettings>(json);
        Assert.Equal(OverlapWindows.Running, back!.DispatchOverlapWindow);
    }

    // ---- ParallelSlotPolicy over the window

    [Fact]
    public void In_flight_window_serializes_on_an_in_flight_overlap_and_names_window_card_and_prefix()
    {
        var inFlight = new[] { new InFlightTask("in-review", Scope("backend/Features/Runner"), TaskStates.AutoReview) };

        var admission = ParallelSlotPolicy.Decide(
            "candidate", Scope("backend/Features/Runner/Sub"), Array.Empty<RunningTask>(), 4,
            inFlight, OverlapWindows.InFlight);

        Assert.Equal(SlotDecision.Serialize, admission.Decision);
        Assert.True(admission.IsScopeConflict);
        Assert.Equal(OverlapWindows.InFlight, admission.Window);
        Assert.Equal("in-review", admission.ConflictTaskId);
        Assert.Equal("backend/features/runner", admission.SharedPrefix);
        Assert.Equal(TaskStates.AutoReview, admission.ConflictLane);
        Assert.Equal(
            "scope conflict with 'in-review' on 'backend/features/runner' (window in-flight, 4-auto-review)",
            admission.Reason);
    }

    [Fact]
    public void Running_window_ignores_in_flight_cards()
    {
        var inFlight = new[] { new InFlightTask("in-review", Scope("backend"), TaskStates.AutoReview) };

        var admission = ParallelSlotPolicy.Decide(
            "candidate", Scope("backend/x"), Array.Empty<RunningTask>(), 4, inFlight, OverlapWindows.Running);

        Assert.Equal(SlotDecision.Admit, admission.Decision);
        Assert.Equal("parallel-ok: first slot", admission.Reason);
        Assert.Equal(OverlapWindows.Running, admission.Window);
    }

    [Fact]
    public void Running_conflict_names_the_window_too()
    {
        var running = new[] { new RunningTask("busy", Scope("docs/a")) };
        var admission = ParallelSlotPolicy.Decide("c", Scope("docs/a/b"), running, 4,
            Array.Empty<InFlightTask>(), OverlapWindows.Running);

        Assert.Equal("scope conflict with 'busy' on 'docs/a' (window running, running)", admission.Reason);
        Assert.Equal("busy", admission.ConflictTaskId);
        Assert.Equal("running", admission.ConflictLane);
    }

    [Fact]
    public void In_flight_cards_take_no_slot_and_exclusive_blocks_only_while_running()
    {
        var inFlight = new[]
        {
            new InFlightTask("a", Scope("frontend/x"), TaskStates.AutoReview),
            new InFlightTask("b", new TaskParallelism(true, ["tools/y"]), TaskStates.HumanReview),
        };

        var disjoint = ParallelSlotPolicy.Decide("c", Scope("backend/z"), Array.Empty<RunningTask>(), 1,
            inFlight, OverlapWindows.InFlight);
        Assert.Equal(SlotDecision.Admit, disjoint.Decision);
        Assert.Equal("parallel-ok: disjoint from 0 running and 2 in-flight task(s)", disjoint.Reason);

        var exclusiveCandidate = ParallelSlotPolicy.Decide("c", new TaskParallelism(true, []),
            Array.Empty<RunningTask>(), 1, inFlight, OverlapWindows.InFlight);
        Assert.Equal(SlotDecision.RunExclusive, exclusiveCandidate.Decision);

        var exclusiveRunning = ParallelSlotPolicy.Decide("c", Scope("backend/z"),
            [new RunningTask("x", new TaskParallelism(true, []))], 4, inFlight, OverlapWindows.InFlight);
        Assert.Equal(SlotDecision.Serialize, exclusiveRunning.Decision);
        Assert.False(exclusiveRunning.IsScopeConflict);
    }

    [Fact]
    public void Unknown_scope_stays_optimistic_and_the_candidate_never_meets_itself()
    {
        var inFlight = new[]
        {
            new InFlightTask("self", Scope("backend/x"), TaskStates.Ready),
            new InFlightTask("other", Scope("backend/x"), TaskStates.AutoReview),
        };

        var unknown = ParallelSlotPolicy.Decide("new", Scope(), Array.Empty<RunningTask>(), 4,
            inFlight, OverlapWindows.InFlight);
        Assert.Equal(SlotDecision.Admit, unknown.Decision);
        Assert.Contains("optimistic", unknown.Reason);

        var self = ParallelSlotPolicy.Decide("self", Scope("backend/x"), Array.Empty<RunningTask>(), 4,
            inFlight, OverlapWindows.InFlight);
        Assert.Equal("other", self.ConflictTaskId);

        var onlySelf = ParallelSlotPolicy.Decide("self", Scope("backend/x"), Array.Empty<RunningTask>(), 4,
            [inFlight[0]], OverlapWindows.InFlight);
        Assert.Equal(SlotDecision.Admit, onlySelf.Decision);
    }

    [Fact]
    public void Four_argument_decide_keeps_the_running_only_behaviour()
    {
        var admission = ParallelSlotPolicy.Decide("c", Scope("backend/x"), Array.Empty<RunningTask>(), 1);
        Assert.Equal(SlotDecision.Admit, admission.Decision);
        Assert.Equal(OverlapWindows.Running, admission.Window);
    }

    [Fact]
    public void Decide_overlap_leaves_the_slot_count_to_the_host_and_keeps_the_scope_rule()
    {
        var running = Enumerable.Range(0, 40)
            .Select(i => new RunningTask($"busy-{i}", Scope($"frontend/area{i}")))
            .ToArray();
        var inFlight = new[] { new InFlightTask("in-review", Scope("backend/Features"), TaskStates.HumanReview) };

        var disjoint = ParallelSlotPolicy.DecideOverlap("c", Scope("tools/x"), running, inFlight, OverlapWindows.InFlight);
        Assert.Equal(SlotDecision.Admit, disjoint.Decision);

        var overlap = ParallelSlotPolicy.DecideOverlap("c", Scope("backend/Features/Runner"), running, inFlight, OverlapWindows.InFlight);
        Assert.Equal(SlotDecision.Serialize, overlap.Decision);
        Assert.Equal("in-review", overlap.ConflictTaskId);
        Assert.Equal(TaskStates.HumanReview, overlap.ConflictLane);
    }

    // ---- OverlapComparisonSet membership

    private static TaskInfo Card(string id, string state, Action<TaskProvenanceBuilder>? provenance = null,
        List<TaskIntegrationRecord>? records = null, PendingIntent? intent = null, string mode = TaskModes.Coding,
        bool noBranch = false, string project = Project)
    {
        var builder = new TaskProvenanceBuilder();
        provenance?.Invoke(builder);
        return new TaskInfo
        {
            Id = id,
            State = state,
            ProjectName = project,
            Mode = mode,
            NoBranchExpected = noBranch,
            PendingIntent = intent,
            IntegrationRecords = records ?? [],
            Commits = state is TaskStates.HumanReview ? [new TaskCommitInfo { Sha = "delivery" }] : [],
            Provenance = builder.Build(),
        };
    }

    private sealed class TaskProvenanceBuilder
    {
        private readonly List<TaskProvenanceTransition> _transitions = [];
        private TaskProvenanceMerge? _merge;

        public TaskProvenanceBuilder Entered(string lane, DateTime at)
        {
            _transitions.Add(new TaskProvenanceTransition { Lane = lane, AtUtc = at });
            return this;
        }

        public TaskProvenanceBuilder Merged(DateTime at)
        {
            _merge = new TaskProvenanceMerge { MergeCommit = "abc123", AtUtc = at };
            return this;
        }

        public TaskProvenance? Build() => _transitions.Count == 0 && _merge is null
            ? null
            : new TaskProvenance { Branch = "task/x", Transitions = _transitions, Merge = _merge };
    }

    private static TaskIntegrationRecord Record(string classification, DateTime at)
        => new() { Classification = classification, RecordedAtUtc = at };

    [Fact]
    public void In_flight_covers_progress_review_lanes_and_queued_merge_rounds()
    {
        Assert.True(OverlapComparisonSet.IsInFlight(Card("p", TaskStates.Progress)));
        Assert.True(OverlapComparisonSet.IsInFlight(Card("ar", TaskStates.AutoReview)));
        Assert.True(OverlapComparisonSet.IsInFlight(Card("hr", TaskStates.HumanReview)));
        Assert.True(OverlapComparisonSet.IsInFlight(Card("round", TaskStates.Ready,
            b => b.Entered(TaskStates.Progress, Start).Entered(TaskStates.AutoReview, Start.AddHours(1))
                .Entered(TaskStates.Ready, Start.AddHours(2)),
            intent: new PendingIntent { Prompt = "merge develop" })));
    }

    [Fact]
    public void Not_in_flight_fresh_parked_integrated_and_report_only_cards()
    {
        Assert.False(OverlapComparisonSet.IsInFlight(Card("fresh", TaskStates.Ready)));
        Assert.False(OverlapComparisonSet.IsInFlight(Card("fresh-intent", TaskStates.Ready,
            intent: new PendingIntent { Prompt = "x" })));
        Assert.False(OverlapComparisonSet.IsInFlight(Card("reissued", TaskStates.Ready,
            b => b.Entered(TaskStates.Progress, Start))));
        Assert.False(OverlapComparisonSet.IsInFlight(Card("done", TaskStates.Completed)));
        Assert.False(OverlapComparisonSet.IsInFlight(Card("parked", TaskStates.Escalated)));
        Assert.False(OverlapComparisonSet.IsInFlight(Card("ncc", TaskStates.CodeNotComplete)));
        Assert.False(OverlapComparisonSet.IsInFlight(Card("plan", TaskStates.AutoReview, mode: TaskModes.Planning)));
        Assert.False(OverlapComparisonSet.IsInFlight(Card("nobranch", TaskStates.HumanReview, noBranch: true)));

        Assert.False(OverlapComparisonSet.IsInFlight(Card("batch-published", TaskStates.HumanReview,
            b => b.Entered(TaskStates.Progress, Start),
            records: [Record(IntegrationRecordClasses.IntegratedVerified, Start.AddHours(3))])));
        Assert.False(OverlapComparisonSet.IsInFlight(Card("merged", TaskStates.HumanReview,
            b => b.Entered(TaskStates.Progress, Start).Merged(Start.AddHours(2)))));
    }

    [Fact]
    public void An_integration_before_the_current_run_does_not_end_the_exposure()
    {
        var reopened = Card("reopened", TaskStates.AutoReview,
            b => b.Entered(TaskStates.Progress, Start).Merged(Start.AddHours(1))
                .Entered(TaskStates.Progress, Start.AddHours(5)),
            records: [Record(IntegrationRecordClasses.IntegratedVerified, Start.AddHours(1))]);
        Assert.True(OverlapComparisonSet.IsInFlight(reopened));

        var missing = Card("missing", TaskStates.HumanReview,
            records: [Record(IntegrationRecordClasses.GenuinelyMissing, Start.AddHours(1))]);
        Assert.True(OverlapComparisonSet.IsInFlight(missing));
    }

    [Fact]
    public void Collect_strips_exclusive_skips_running_and_other_projects()
    {
        var cards = new[]
        {
            Card("running", TaskStates.Progress),
            Card("review", TaskStates.AutoReview),
            Card("elsewhere", TaskStates.AutoReview, project: "other"),
            Card("done", TaskStates.Completed),
        };
        var running = new HashSet<string>(["running"], StringComparer.OrdinalIgnoreCase);

        var set = OverlapComparisonSet.Collect(cards, Project, running, _ => new TaskParallelism(true, ["docs/a"]));

        var only = Assert.Single(set);
        Assert.Equal("review", only.TaskId);
        Assert.Equal(TaskStates.AutoReview, only.Lane);
        Assert.False(only.Parallelism.Exclusive);
        Assert.Equal(["docs/a"], only.Parallelism.PredictedScope);
    }

    [Fact]
    public void A_started_candidate_compares_only_with_post_coding_cards()
    {
        var inFlight = new[]
        {
            new InFlightTask("progress-orphan", Scope("docs/a"), TaskStates.Progress),
            new InFlightTask("round", Scope("docs/a"), TaskStates.Ready),
            new InFlightTask("review", Scope("docs/a"), TaskStates.AutoReview),
            new InFlightTask("acceptance", Scope("docs/a"), TaskStates.HumanReview),
        };

        var fresh = OverlapComparisonSet.For(Card("fresh", TaskStates.Ready), inFlight);
        Assert.Equal(4, fresh.Count);

        var started = OverlapComparisonSet.For(Card("again", TaskStates.Ready,
            b => b.Entered(TaskStates.Progress, Start), intent: new PendingIntent()), inFlight);
        Assert.Equal(["review", "acceptance"], started.Select(i => i.TaskId));

        var orphan = OverlapComparisonSet.For(Card("progress-orphan", TaskStates.Progress), inFlight);
        Assert.Equal(["review", "acceptance"], orphan.Select(i => i.TaskId));
    }

    [Fact]
    public void Remote_project_set_holds_progress_as_running_and_in_flight_only_in_that_window()
    {
        var cards = new[]
        {
            Card("leased", TaskStates.Progress),
            Card("review", TaskStates.AutoReview),
            Card("ready", TaskStates.Ready),
        };

        var inFlight = OverlapComparisonSet.ForProject(cards, Project, OverlapWindows.InFlight, _ => Scope("docs/a"));
        Assert.Equal(["leased"], inFlight.Running.Select(r => r.TaskId));
        Assert.Equal(["review"], inFlight.InFlight.Select(r => r.TaskId));

        var running = OverlapComparisonSet.ForProject(cards, Project, OverlapWindows.Running, _ => Scope("docs/a"));
        Assert.Equal(["leased"], running.Running.Select(r => r.TaskId));
        Assert.Empty(running.InFlight);
    }
}
