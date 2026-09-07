using Xunit;

namespace AgentStudio.Scenario.Tests;

/// <summary>
/// Guards the scenario definition every deployment card and the release run
/// against. A card that adds a step edits this document, so these checks keep
/// the additions well formed without needing a live topology.
/// </summary>
public sealed class ShippedScenarioTests
{
    private static ScenarioDocument Shipped()
    {
        var path = Path.Combine(RepositoryRoot(), ScenarioCommandLine.DefaultDocumentPath);
        Assert.True(File.Exists(path), $"The shipped scenario document is missing: {path}");
        var result = ScenarioDocumentLoader.Parse(File.ReadAllText(path));
        Assert.Null(result.Error);
        return result.Document!;
    }

    [Fact]
    public void The_shipped_document_parses()
    {
        var document = Shipped();

        Assert.Equal("deployment-regression", document.Id);
        Assert.NotEmpty(document.Steps);
    }

    [Fact]
    public void Every_step_names_an_action_the_runner_implements()
    {
        var unknown = Shipped().Steps
            .Where(step => !ScenarioActions.Known.Contains(step.Action))
            .Select(step => $"{step.Id} -> {step.Action}")
            .ToList();

        Assert.True(
            unknown.Count == 0,
            $"Steps name actions the runner does not implement: {string.Join(", ", unknown)}");
    }

    [Fact]
    public void Every_step_asserts_something_so_no_step_can_pass_by_doing_nothing()
    {
        var silent = Shipped().Steps
            .Where(step => step.Expect.Count == 0)
            .Select(step => step.Id)
            .ToList();

        Assert.True(silent.Count == 0, $"Steps without expectations: {string.Join(", ", silent)}");
    }

    [Fact]
    public void The_smoke_level_is_a_prefix_of_the_scenario()
    {
        // Later steps read what earlier steps recorded, so a smoke step may
        // never sit behind a full step: the smoke run would skip its input.
        var steps = Shipped().Steps;
        var firstFull = steps
            .Select((step, index) => (step, index))
            .FirstOrDefault(item => item.step.Level == ScenarioLevel.Full);
        if (firstFull.step is null) return;

        var smokeAfterFull = steps.Skip(firstFull.index)
            .Where(step => step.Level == ScenarioLevel.Smoke)
            .Select(step => step.Id)
            .ToList();

        Assert.True(
            smokeAfterFull.Count == 0,
            $"Smoke steps follow a full step: {string.Join(", ", smokeAfterFull)}");
    }

    [Fact]
    public void The_smoke_level_stays_small_enough_for_a_card_gate()
    {
        var smoke = Shipped().Steps.Count(step => step.Level == ScenarioLevel.Smoke);

        Assert.InRange(smoke, 1, 6);
    }

    [Fact]
    public void A_smoke_run_on_every_target_plans_at_least_one_step()
    {
        var document = Shipped();

        foreach (var target in new[]
                 {
                     ScenarioTargetKind.InProc,
                     ScenarioTargetKind.Compose,
                     ScenarioTargetKind.Remote,
                 })
        {
            var planned = ScenarioPlanner.Plan(document, target, ScenarioLevel.Smoke)
                .Count(step => step.Disposition == ScenarioStepDisposition.Run);
            Assert.True(planned > 0, $"The smoke level plans no step for target {target}.");
        }
    }

    [Fact]
    public void Store_destructive_steps_are_declared_for_the_inproc_target_only()
    {
        // Restoring a backup replaces a store. The scenario must never do that
        // to a deployment it does not own.
        var destructive = Shipped().Steps
            .Where(step => step.Action == ScenarioActions.RestoreStore)
            .ToList();

        Assert.NotEmpty(destructive);
        Assert.All(destructive, step =>
            Assert.Equal([ScenarioTargetKind.InProc], step.Targets));
    }

    [Fact]
    public void Runner_dependent_steps_are_not_declared_for_the_remote_target()
    {
        // The remote target drives an existing deployment and hosts no runner
        // of its own, so a step that waits for the fixture runner would hang.
        string[] runnerDependent =
        [
            ScenarioActions.RegisterRunner,
            ScenarioActions.AwaitClaim,
            ScenarioActions.AwaitRunCompleted,
        ];

        var offenders = Shipped().Steps
            .Where(step => runnerDependent.Contains(step.Action)
                           && step.Targets.Contains(ScenarioTargetKind.Remote))
            .Select(step => step.Id)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"Runner-dependent steps declared for the remote target: {string.Join(", ", offenders)}");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "agent-taskboard.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
