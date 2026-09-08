using AgentStudio.Retention;

namespace AgentStudio.Retention.Tests;

public sealed class RetentionPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 0, 0, 0, TimeSpan.Zero);
    private readonly ArtifactClassifier _classifier = new();

    [Theory]
    [InlineData(29, 0)]
    [InlineData(30, 1)]
    [InlineData(179, 1)]
    [InlineData(180, 1)]
    public void AppliesAgeBoundaries(int ageDays, int expectedActions)
    {
        var task = Task("7-archive", Now.AddDays(-ageDays), File("logs/cli-output.log", 100));
        var plan = new RetentionPlanner().Plan([task], RetentionPolicy.Default(), Now);
        Assert.Equal(expectedActions, plan.Actions.Count(action => action.Kind is RetentionActionKind.ArchiveHeavy or RetentionActionKind.ArchiveTask));
        if (ageDays == 30) Assert.Equal(RetentionActionKind.ArchiveHeavy, plan.Actions.Single().Kind);
        if (ageDays == 180) Assert.Equal(RetentionActionKind.ArchiveTask, plan.Actions.Single().Kind);
    }

    [Theory]
    [InlineData("3-progress")]
    [InlineData("5-human-review")]
    [InlineData("5e-escalated")]
    public void NeverArchivesExcludedLanes(string lane)
    {
        var plan = new RetentionPlanner().Plan([Task(lane, Now.AddYears(-1), File("logs/cli-output.log", 100))],
            RetentionPolicy.Default(), Now);
        Assert.DoesNotContain(plan.Actions, action => action.Kind is RetentionActionKind.ArchiveHeavy or RetentionActionKind.ArchiveTask);
    }

    [Fact]
    public void BudgetOverflowArchivesOldestHeavyFirst()
    {
        var policy = RetentionPolicy.Default() with
        {
            WorkspaceDefaults = RetentionPolicy.Default().WorkspaceDefaults.ToDictionary(
                item => item.Key,
                item => item.Key == ArtifactClass.HeavyWorkingData
                    ? item.Value with { HotBudgetBytesPerTask = 100, HotCapBytesPerFile = 10, ArchiveAfterDaysTerminal = 30 }
                    : item.Value),
        };
        var task = Task("7-archive", Now.AddDays(-1),
            File("results/new.zip", 60, Now.AddHours(-1)),
            File("results/old.zip", 60, Now.AddHours(-3)),
            File("results/middle.zip", 60, Now.AddHours(-2)));
        var action = Assert.Single(new RetentionPlanner().Plan([task], policy, Now).Actions);
        Assert.Equal(["results/old.zip", "results/middle.zip"], action.Files.Select(file => file.RelativePath));
    }

    [Fact]
    public void RuntimeSweepDeletesOldDailyArchiveButNeverLiveAttemptAuthority()
    {
        var task = Task("runtime", Now,
            File(".metadata/attempt-authority.json", 10, Now.AddDays(-200)),
            File(".metadata/attempt-authority.archive-2026-01-01.json", 10, Now.AddDays(-100)));
        var action = Assert.Single(new RetentionPlanner().Plan([task], RetentionPolicy.Default(), Now).Actions);
        Assert.Equal(RetentionActionKind.DeleteRuntime, action.Kind);
        Assert.Equal(".metadata/attempt-authority.archive-2026-01-01.json", Assert.Single(action.Files).RelativePath);
    }

    [Fact]
    public void Stage_two_is_planned_after_stage_one_even_when_only_hot_stubs_remain()
    {
        var archivedHeavy = File("logs/cli-output.log", 100) with { IsArchived = true };
        var task = Task("7-archive", Now.AddDays(-180), archivedHeavy, File("status.md", 20));

        var action = Assert.Single(new RetentionPlanner().Plan([task], RetentionPolicy.Default(), Now).Actions);

        Assert.Equal(RetentionActionKind.ArchiveTask, action.Kind);
        Assert.Equal(2, action.Stage);
        Assert.Empty(action.Files);
    }

    [Fact]
    public void Stage_three_is_off_by_default_and_requires_an_enabled_policy()
    {
        var task = Task("7-archive", Now.AddDays(-730), File("logs/cli-output.log", 100) with { IsArchived = true });
        Assert.DoesNotContain(
            new RetentionPlanner().Plan([task], RetentionPolicy.Default(), Now).Actions,
            action => action.Kind == RetentionActionKind.DeleteCold);

        var defaults = RetentionPolicy.Default();
        var enabled = defaults with
        {
            WorkspaceDefaults = defaults.WorkspaceDefaults.ToDictionary(
                item => item.Key,
                item => item.Key == ArtifactClass.HeavyWorkingData
                    ? item.Value with { DeleteArchiveEnabled = true }
                    : item.Value),
        };
        var action = Assert.Single(new RetentionPlanner().Plan([task], enabled, Now).Actions);
        Assert.Equal(RetentionActionKind.DeleteCold, action.Kind);
        Assert.Equal(3, action.Stage);
    }

    [Theory]
    [InlineData(364, false)]
    [InlineData(365, true)]
    public void Archive_storage_years_enable_stage_three_at_the_policy_boundary(int ageDays, bool expected)
    {
        var task = Task("7-archive", Now.AddDays(-ageDays),
            File("logs/cli-output.log", 100) with { IsArchived = true });
        var policy = RetentionPolicy.Default() with
        {
            ArchiveStorage = new ArchiveStoragePolicy { DeleteArchivedAfterYears = 1 },
        };

        Assert.Equal(expected, new RetentionPlanner().Plan([task], policy, Now).Actions
            .Any(action => action.Kind == RetentionActionKind.DeleteCold));
    }

    private RetentionTaskInventory Task(string lane, DateTimeOffset terminalAt, params RetentionFile[] files)
        => new("P", "P-1", "id-1", lane, terminalAt, "projects/P/tasks/7-archive/P-1", files);

    private RetentionFile File(string path, long size, DateTimeOffset? modified = null)
        => new(path, size, modified ?? Now, _classifier.Classify(path));
}
