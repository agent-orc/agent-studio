using Xunit;

namespace AgentStudio.Tests;

public sealed class ProjectExecutionPolicyTests
{
    [Theory]
    [InlineData(PickupModes.Auto, ExecutionLocations.Local, true, false)]
    [InlineData(PickupModes.Auto, "runner-01", false, true)]
    [InlineData(PickupModes.Manual, ExecutionLocations.Local, false, false)]
    [InlineData(PickupModes.Manual, "runner-01", false, false)]
    [InlineData(PickupModes.Paused, ExecutionLocations.Local, false, false)]
    [InlineData(PickupModes.Paused, "runner-01", false, false)]
    public void ClaimPolicy_SeparatesAutomaticOfferFromPlacement(
        string pickupMode,
        string executionLocation,
        bool localMayClaim,
        bool remoteMayClaim)
    {
        var settings = new ProjectSettings
        {
            PickupMode = pickupMode,
            ExecutionLocation = executionLocation,
        };

        Assert.Equal(
            localMayClaim,
            ProjectExecutionPolicy.AllowsAutomaticPickup(settings)
            && ProjectExecutionPolicy.IsLocalExecution(settings));
        Assert.Equal(
            remoteMayClaim,
            ProjectExecutionPolicy.AllowsAutomaticPickup(settings)
            && ProjectExecutionPolicy.IsAssignedRemote(settings, "runner-01"));
    }

    [Theory]
    [InlineData("auto-continuous", PickupModes.Auto, ExecutionLocations.Local)]
    [InlineData("manual", PickupModes.Manual, ExecutionLocations.Local)]
    [InlineData("paused", PickupModes.Paused, ExecutionLocations.Local)]
    [InlineData("runner-01", PickupModes.Auto, "runner-01")]
    public void Migrate_ResolvesLegacyCompositeValues(
        string legacyValue,
        string expectedPickupMode,
        string expectedLocation)
    {
        var migrated = ProjectExecutionPolicy.Migrate(new ProjectSettings
        {
            ExecutionRunner = legacyValue,
        });

        Assert.Equal(expectedPickupMode, migrated.PickupMode);
        Assert.Equal(expectedLocation, migrated.ExecutionLocation);
    }

    [Fact]
    public void Migrate_PausedLegacyMode_KeepsCanonicalRemoteLocation()
    {
        var migrated = ProjectExecutionPolicy.Migrate(new ProjectSettings
        {
            PickupMode = PickupModes.Paused,
            ExecutionLocation = "runner-01",
            ExecutionRunner = "paused",
        });

        Assert.Equal(PickupModes.Paused, migrated.PickupMode);
        Assert.Equal("runner-01", migrated.ExecutionLocation);
        Assert.Equal("runner-01", migrated.ExecutionRunner);
    }

    [Theory]
    [InlineData("local", false)]
    [InlineData("runner-01", true)]
    public void RemoteClaimability_MissingRepositoryUrlWarnsOnlyForRemotePlacement(
        string executionLocation,
        bool expectedWarning)
    {
        var project = new ProjectRecord { Id = "PROJ-001", DisplayName = "demo" };
        var settings = new ProjectSettings
        {
            PickupMode = PickupModes.Auto,
            ExecutionLocation = executionLocation,
        };

        Assert.Equal(
            expectedWarning,
            RemoteProjectClaimabilityPolicy.IsMissingRepositoryUrl(project, settings));
    }

    [Fact]
    public void ClassPlacement_OffersAnyRunnerForCapabilityAdmissionWithoutChangingPins()
    {
        var shared = new ProjectSettings
        {
            PickupMode = PickupModes.Auto,
            ExecutionLocation = "class:linux",
        };
        Assert.Equal("platform:linux", ExecutionLocations.RequiredClassCapability(
            ProjectExecutionPolicy.ResolveExecutionLocation(shared)));
        Assert.True(ProjectExecutionPolicy.IsAssignedRemote(shared, "runner-a"));
        Assert.True(ProjectExecutionPolicy.IsAssignedRemote(shared, "runner-b"));

        var pinned = shared with { ExecutionLocation = "runner-a" };
        Assert.True(ProjectExecutionPolicy.IsAssignedRemote(pinned, "runner-a"));
        Assert.False(ProjectExecutionPolicy.IsAssignedRemote(pinned, "runner-b"));
    }

    [Theory]
    [InlineData(0, 1, true)]
    [InlineData(1, 1, false)]
    [InlineData(1, 2, true)]
    [InlineData(2, 2, false)]
    public void ProjectSlots_RemainSequentialUntilParallelismIsConfigured(
        int occupied, int maximum, bool expected)
        => Assert.Equal(expected, ProjectExecutionPolicy.HasProjectSlot(occupied, maximum));

    [Fact]
    public void ProjectOccupancy_CountsLiveProgressOnlyAndExcludesTheLeasedTask()
    {
        TaskInfo Task(string id, string project, string state, bool fixture = false)
            => new() { Id = id, ProjectName = project, State = state, Fixture = fixture };
        var tasks = new[]
        {
            Task("a-1", "Alpha", TaskStates.Progress),
            Task("a-2", "alpha", TaskStates.Progress),
            Task("a-3", "Alpha", TaskStates.Ready),
            Task("a-4", "Alpha", TaskStates.Progress, fixture: true),
            Task("b-1", "Beta", TaskStates.Progress),
        };

        var all = ProjectExecutionPolicy.ProjectOccupancy(tasks);
        var sequential = new ProjectSettings { ExecutionLocation = "class:linux" };
        Assert.Equal(new ProjectSlotVerdict(false, 2, 1),
            ProjectExecutionPolicy.EvaluateProjectSlot(all, "Alpha", sequential));
        Assert.Equal(new ProjectSlotVerdict(true, 2, 3),
            ProjectExecutionPolicy.EvaluateProjectSlot(all, "Alpha", sequential with { MaxParallelism = 3 }));
        Assert.Equal(new ProjectSlotVerdict(true, 0, 1),
            ProjectExecutionPolicy.EvaluateProjectSlot(all, "Gamma", sequential));

        var excludingSelf = ProjectExecutionPolicy.ProjectOccupancy(tasks, excludingTaskId: "B-1");
        Assert.Equal(new ProjectSlotVerdict(true, 0, 1),
            ProjectExecutionPolicy.EvaluateProjectSlot(excludingSelf, "Beta", sequential));
    }

    [Theory]
    [InlineData("runner-a")]
    [InlineData(null)]
    public void ProjectSlot_LeavesPinnedAndLegacyProjectsToHostSlots(string? location)
    {
        var occupancy = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Alpha"] = 3 };
        var pinned = new ProjectSettings { ExecutionLocation = location, MaxParallelism = 1 };

        var verdict = ProjectExecutionPolicy.EvaluateProjectSlot(occupancy, "Alpha", pinned);
        Assert.Equal(new ProjectSlotVerdict(true, 3, null), verdict);
        Assert.Equal("Project has 3 active tasks and no project limit.", verdict.Detail);
    }
}
