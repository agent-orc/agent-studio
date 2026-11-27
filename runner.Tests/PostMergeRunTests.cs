using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentRunner.Tests;

public sealed class PostMergeRunTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(29, false)]
    [InlineData(30, true)]
    [InlineData(31, true)]
    public void ExpiredWindowsPromoteRegardlessOfCodingOccupancy(int age, bool promoted)
        => Assert.Equal(promoted, AgentStudio.OrchestrationDecisions.PostMergeSchedulingPolicy.DeadlineReached(
            Now.AddMinutes(-age), 30, Now));

    [Fact]
    public void OwnerDeadlineIsThreeWindowsAndSupersessionStopsAtTestStart()
    {
        Assert.False(AgentStudio.OrchestrationDecisions.PostMergeSchedulingPolicy.NeedsOwner(Now.AddMinutes(-90), 30, Now));
        Assert.True(AgentStudio.OrchestrationDecisions.PostMergeSchedulingPolicy.NeedsOwner(Now.AddMinutes(-91), 30, Now));
        Assert.True(AgentStudio.OrchestrationDecisions.PostMergeSchedulingPolicy.MaySupersede("materializing"));
        Assert.False(AgentStudio.OrchestrationDecisions.PostMergeSchedulingPolicy.MaySupersede("running"));
    }

    [Fact]
    public void WindowClosesAtCountOrAge()
    {
        Assert.False(PostMergeRunPlan.Window(5, Now.AddMinutes(-29), Now, 6, TimeSpan.FromMinutes(30)).Close);
        Assert.True(PostMergeRunPlan.Window(6, Now.AddMinutes(-1), Now, 6, TimeSpan.FromMinutes(30)).Close);
        Assert.True(PostMergeRunPlan.Window(1, Now.AddMinutes(-30), Now, 6, TimeSpan.FromMinutes(30)).Close);
    }

    [Fact]
    public void AffectedWindowUsesSharedSelectionAndDocsRunsNoBuildOrBackendTest()
    {
        var definition = EnabledDefinition();
        Assert.Equal(["node scripts/check-public-docs.mjs"],
            PostMergeRunPlan.Build(definition, "window", ["docs/start/README.md"]));
        Assert.Equal(["bash scripts/npm-frontend.sh ci --prefer-offline", "bash scripts/npm-frontend.sh run build", "bash scripts/npm-frontend.sh run lint",
                "bash scripts/npm-frontend.sh run test:ci"],
            PostMergeRunPlan.Build(definition, "window", ["frontend/src/app/app.ts"]));
        var mixed = PostMergeRunPlan.SelectAffected(definition, ["backend/X.cs", "frontend/src/app/app.ts"]);
        Assert.Contains(definition.TestSuites.Single(suite => suite.Id == "backend-routine").Command, mixed);
        Assert.Contains(definition.TestSuites.Single(suite => suite.Id == "frontend-unit").Command, mixed);
        Assert.DoesNotContain(mixed, command => command.Contains("runner.Tests"));
    }

    [Fact]
    public void AnUnassignedDeclaredTestCommandCannotDisappearFromFullFallback()
    {
        var definition = EnabledDefinition();
        definition = definition with { Commands = definition.Commands with
            { Test = [.. definition.Commands.Test, "new unassigned test command"] } };
        Assert.Contains("new unassigned test command", PostMergeRunPlan.Build(definition, "window", ["docs/a.md"]));
    }

    [Theory]
    [InlineData("nightly", null)]
    [InlineData("window", PostMergeWindowSuiteModes.Routine)]
    public void FullSuitePointsKeepAllRoutineSuites(string kind, string? mode)
    {
        var definition = EnabledDefinition();
        Assert.InRange(definition.TestSuites.Where(suite => suite.Category == "base")
            .Sum(suite => suite.ExpectedDurationSeconds), 1, 600);
        var commands = PostMergeRunPlan.Build(definition, kind, ["docs/a.md"], mode);
        Assert.All(definition.TestSuites.Where(suite => suite.Category == "routine"),
            suite => Assert.Contains(suite.Command, commands));
        Assert.All(definition.Commands.Build, command => Assert.Contains(command, commands));
        Assert.Contains(definition.Commands.Prepare, commands);
    }

    [Theory]
    [InlineData("contracts/TaskServer.Contracts/A.cs")]
    [InlineData(".agent-studio/project.yml")]
    [InlineData("global.json")]
    [InlineData("Directory.Build.props")]
    [InlineData("backend/Directory.Build.targets")]
    [InlineData("backend/Build.Common.props")]
    [InlineData("frontend/Build.Common.targets")]
    [InlineData("backend/build.proj")]
    [InlineData("frontend/affected.slnf")]
    [InlineData("Directory.Packages.props")]
    [InlineData("NuGet.config")]
    [InlineData("agent-taskboard.sln")]
    [InlineData("unknown/A.cs")]
    public void SharedOrUnmappedInputSelectsAllRoutineSuites(string path)
    {
        var definition = EnabledDefinition();
        Assert.Equal(definition.TestSuites.Where(suite => suite.Category == "routine").Select(suite => suite.Command),
            PostMergeRunPlan.SelectAffected(definition, [path]));
    }

    [Theory]
    [InlineData("true", true, true)]
    [InlineData("false", false, true)]
    [InlineData("yes", false, false)]
    public void DeliveryDeployAfterGreenWindowAcceptsOnlyTrueOrFalse(string value, bool expected, bool valid)
    {
        var read = ProjectDefinitionReader.Parse($$"""
            schemaVersion: 1
            stack: [custom]
            toolVersions:
            commands:
              prepare: .agent-studio/prepare
              build:
                - "true"
              test:
                - "true"
              lint:
            testSuites:
              - id: smoke
                category: base
                expectedDurationSeconds: 5
                command: "true"
            cachePaths:
            capabilities: [git]
            delivery:
              deployAfterGreenWindow: {{value}}
            """);
        Assert.Equal(valid, read.IsValid);
        Assert.Equal(expected, read.Parsed!.DeliveryDeployAfterGreenWindow);
        if (!valid) Assert.Contains(read.Issues, issue => issue.Path == "delivery.deployAfterGreenWindow");
    }

    [Fact]
    public void NightlyShardsUseOnlyFreeSlotsAndRetainAComplementShard()
    {
        var suite = new ProjectTestSuite("backend-routine", "routine", 1800,
            "dotnet test backend.Tests/tests.csproj --no-build --filter Category!=MachineBound")
        { Shards = ProjectTestSuite.AutoShards };
        Assert.Equal(1, PostMergeShards.Count(suite, 1, 10));
        Assert.Equal(3, PostMergeShards.Count(suite, 3, 10));
        var classes = PostMergeShards.Classes("The following Tests are available:\n  A.One.Test\n  A.Two.Test\n  A.Three.Test\n");
        var root = Path.Combine(Path.GetTempPath(), "post-merge-shards-" + Guid.NewGuid().ToString("N"));
        try
        {
            var commands = PostMergeShards.Commands(suite.Command,
                Enumerable.Range(0, 10).Select(i => $"A.Type{i}").ToArray(), 2, root);
            Assert.Contains(commands, command => command.Contains("--settings", StringComparison.Ordinal));
            Assert.Equal(2, commands.Count);
            Assert.All(commands, command => Assert.DoesNotContain("--filter", command, StringComparison.Ordinal));
            Assert.Contains(Directory.EnumerateFiles(root, "*.runsettings"), path =>
                File.ReadAllText(path).Contains("Category!=MachineBound", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DryRunPrintsPlanWithoutFetchingOrPosting()
    {
        var root = Path.Combine(Path.GetTempPath(), "post-merge-dry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".agent-studio"));
        try
        {
            File.WriteAllText(Path.Combine(root, ".agent-studio", "project.yml"), """
                schemaVersion: 1
                stack: [custom]
                toolVersions:
                commands:
                  prepare: .agent-studio/prepare
                  build:
                    - "true"
                  test:
                    - "true"
                  lint:
                testSuites:
                  - id: routine
                    category: routine
                    expectedDurationSeconds: 5
                    command: "true"
                cachePaths:
                capabilities: [git]
                """);
            File.WriteAllText(Path.Combine(root, ".agent-studio", "prepare"), "#!/bin/sh\ntrue\n");
            var options = new PostMergeRunOptions("Agent Studio", root, "http://invalid.invalid/",
                "develop", "origin", "missing", "nightly", 6, TimeSpan.FromMinutes(30), 1, [], true);
            var output = new StringWriter();
            using var http = new HttpClient();
            var exit = await new PostMergeRunCommand(options, output, new FixedClock(), http)
                .ExecuteAsync(CancellationToken.None);
            Assert.Equal(0, exit);
            Assert.Contains("Would fetch", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Would run: true", output.ToString(), StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(root, ".git")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ProjectExecutionDefinition EnabledDefinition()
        => ProjectDefinitionReader.Parse(File.ReadAllText(Path.Combine(RepositoryRoot.Find(),
            "testdata/project-test-selection.project.yml"))).Definition!;

    [Fact]
    public void MissingMapKeepsTheFullPostMergeInventory()
    {
        var definition = EnabledDefinition() with { TestSelection = [] };
        Assert.Empty(definition.TestSelection);
        var selection = ProjectTestSelectionPolicy.Select(definition, ["docs/a.md"]);
        Assert.True(selection.Full);
        Assert.Contains("The project has no valid test selection map.", selection.Reasons);
        var commands = PostMergeRunPlan.Build(definition, "window", ["docs/a.md"]);
        Assert.Contains(definition.Commands.Prepare, commands);
        Assert.All(definition.Commands.Build.Concat(definition.Commands.Test),
            command => Assert.Contains(command, commands));
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
