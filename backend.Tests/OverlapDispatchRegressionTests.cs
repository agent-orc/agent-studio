using AgentStudio.Runner;
using Xunit;

namespace AgentStudio.Tests;

public sealed class OverlapDispatchRegressionTests
{
    [Fact]
    public void Named_files_do_not_expand_to_a_shared_parent()
    {
        var left = TaskScopePrediction.FromText("backend/Features/Runner/ProjectRunner.cs docs/system/contracts/a.md");
        var right = TaskScopePrediction.FromText("backend/Features/Runner/ActiveRuns.cs docs/system/contracts/b.md");
        Assert.Null(ParallelSlotPolicy.FirstScopeConflict(left.PredictedScope, right.PredictedScope));
        Assert.Contains("backend/Features/Runner/ProjectRunner.cs", left.PredictedScope);
    }

    [Theory]
    [InlineData("docs/system/contracts/", "docs/system/contracts/a.md")]
    [InlineData("backend/", "backend/Features/Runner/ProjectRunner.cs")]
    [InlineData("results/status.md", "results/status.md")]
    [InlineData("frontend/src/app/generated/client.ts", "frontend/src/app/generated/client.ts")]
    public void Incidental_or_generated_paths_do_not_serialize(string left, string right)
    {
        Assert.Null(ParallelSlotPolicy.FirstScopeConflict(
            TaskScopePrediction.FromText(left).PredictedScope,
            TaskScopePrediction.FromText(right).PredictedScope));
    }

    [Theory]
    [InlineData("docs/system/contracts/a.md", "docs/system/contracts/a.md")]
    [InlineData("backend.Tests/OverlapWindowTests.cs", "backend.Tests/OverlapWindowTests.cs")]
    [InlineData("backend/Features/Runner/", "backend/Features/Runner/ProjectRunner.cs")]
    public void Exact_files_and_deep_code_folders_can_collide(string left, string right)
    {
        Assert.NotNull(ParallelSlotPolicy.FirstScopeConflict(
            TaskScopePrediction.FromText(left).PredictedScope,
            TaskScopePrediction.FromText(right).PredictedScope));
    }

    [Fact]
    public void Completed_dispatcher_card_never_enters_either_comparison_set()
    {
        var card = new TaskInfo { Id = "AGT-3974", ProjectName = "Agent Studio", State = TaskStates.Completed,
            Title = "docs/dispatcher-personas/index.html" };
        var set = OverlapComparisonSet.ForProject([card], card.ProjectName, OverlapWindows.InFlight, TaskScopePrediction.Predict);
        Assert.Empty(set.Running);
        Assert.Empty(set.InFlight);
    }
    [Fact]
    public void Integrated_unverified_human_review_delivery_does_not_block_the_same_file()
    {
        var parked = new TaskInfo { Id = "AGT-3481", TaskKey = "parked-key", ProjectName = "Agent Studio",
            State = TaskStates.HumanReview, Title = "backend/Features/Runner/ProjectRunner.cs",
            Commits = [new TaskCommitInfo { Sha = "delivery" }] };
        var membership = new Dictionary<string, TaskIntegrationStatus>
        {
            [parked.TaskKey] = new() { Status = IntegrationStatuses.Integrated, IntegrationBranch = "develop", Sha = "delivery" },
        };
        var set = OverlapComparisonSet.ForProject([parked], parked.ProjectName, "in-flight", TaskScopePrediction.Predict, membership, "develop");
        Assert.Empty(set.InFlight);
        Assert.True(ParallelSlotPolicy.DecideOverlap("AGT-4015", TaskScopePrediction.FromText(parked.Title), set.Running, set.InFlight, "in-flight").Admitted);
        Assert.Single(OverlapComparisonSet.ForProject([parked], parked.ProjectName, "in-flight", TaskScopePrediction.Predict, membership, "release").InFlight);
    }

    [Theory]
    [InlineData(TaskStates.HumanReview)]
    [InlineData(TaskStates.Escalated)]
    public void Parking_without_an_open_delivery_does_not_block(string lane)
        => Assert.False(OverlapComparisonSet.IsInFlight(new TaskInfo { Id = "parked", State = lane }));

    [Fact]
    public void Completed_persisted_state_cannot_be_blocked_by_an_older_review_snapshot()
    {
        var folder = Path.Combine(Path.GetTempPath(), "overlap-completed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "task.json"), "{\"state\":\"6-completed\"}");
            var stale = new TaskInfo { Id = "AGT-3974", ProjectName = "Agent Studio", State = TaskStates.AutoReview,
                FolderPath = folder, Title = "docs/dispatcher-personas/index.html" };
            var set = OverlapComparisonSet.ForProject([stale], stale.ProjectName, "in-flight", TaskScopePrediction.Predict);
            Assert.Empty(set.Running);
            Assert.Empty(set.InFlight);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void Incident_named_cards_replay_removes_incidental_documentation_serialization()
    {
        // Controlled replay: actual delivered document paths from the named cards' Git history,
        // plus the shared folder citation described in the incident. These are not recovered briefs.
        var briefs = new Dictionary<string, string>
        {
            ["AGT-3988"] = "docs/red-test-isolation/index.html docs/system/contracts/",
            ["AGT-3989"] = "docs/operator-elevated-session/index.html docs/system/contracts/",
            ["AGT-4003"] = "orchestration-decisions/RedTestTriagePolicy.cs docs/system/contracts/",
            ["AGT-4015"] = "backend/Features/Security/Iam/AccessSecurityStore.OperatorDeviceCertification.cs docs/system/contracts/",
        };
        TaskParallelism Before(string brief) => new(false, TaskScopePrediction.ScopePathRegex.Matches(brief)
            .Select(match => match.Groups[1].Value).Select(path => path[..path.LastIndexOf('/')]).ToArray());
        int Wave(Func<string, TaskParallelism> predict)
        {
            var running = new List<RunningTask>();
            foreach (var (key, brief) in briefs)
            {
                var scope = predict(brief);
                if (ParallelSlotPolicy.Decide(key, scope, running, 10).Admitted) running.Add(new(key, scope));
            }
            return running.Count;
        }
        Assert.Equal(1, Wave(Before));
        Assert.Equal(4, Wave(TaskScopePrediction.FromText));
    }

}
