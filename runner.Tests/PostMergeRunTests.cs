using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentRunner.Tests;

public sealed class PostMergeRunTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WindowClosesAtCountOrAge()
    {
        Assert.False(PostMergeRunPlan.Window(5, Now.AddMinutes(-29), Now, 6, TimeSpan.FromMinutes(30)).Close);
        Assert.True(PostMergeRunPlan.Window(6, Now.AddMinutes(-1), Now, 6, TimeSpan.FromMinutes(30)).Close);
        Assert.True(PostMergeRunPlan.Window(1, Now.AddMinutes(-30), Now, 6, TimeSpan.FromMinutes(30)).Close);
    }

    [Fact]
    public void ChangedModulesSelectUnionAndUnknownFallsBackToFullRoutineSuite()
    {
        var definition = new ProjectExecutionDefinition(1, [],
            new Dictionary<string, string>(), new("prepare", ["build"], [], []),
            [new("backend-routine", "routine", 10, "backend tests"),
             new("runner-routine", "routine", 10, "runner tests"),
             new("frontend-unit", "routine", 10, "frontend tests")],
            [], [], new Dictionary<string, string>(), null, null, null, null, null);
        Assert.Equal(["prepare", "build", "backend tests", "frontend tests"],
            PostMergeRunPlan.Build(definition, "window", ["backend/Foo.cs", "frontend/src/bar.ts"]));
        Assert.Equal(["backend tests", "runner tests", "frontend tests"],
            PostMergeRunPlan.SelectAffected(definition, ["global.json"]));
        Assert.Equal(["prepare", "build", "backend tests", "runner tests", "frontend tests"],
            PostMergeRunPlan.Build(definition, "nightly", []));
    }

    [Fact]
    public void WindowSuitesFollowTheProjectTestingStrategy()
    {
        // AGT-3965: fast keeps the affected union, balanced runs every routine suite.
        var definition = new ProjectExecutionDefinition(1, [],
            new Dictionary<string, string>(), new("prepare", ["build"], [], []),
            [new("backend-routine", "routine", 10, "backend tests"),
             new("runner-routine", "routine", 10, "runner tests"),
             new("backend-coverage", "release", 10, "backend coverage")],
            [], [], new Dictionary<string, string>(), null, null, null, null, null);
        string[] changed = ["backend/Foo.cs"];

        Assert.Equal(["prepare", "build", "backend tests"],
            PostMergeRunPlan.Build(definition, "window", changed, PostMergeWindowSuiteModes.Affected));
        Assert.Equal(["prepare", "build", "backend tests"],
            PostMergeRunPlan.Build(definition, "window", changed, null));
        Assert.Equal(["prepare", "build", "backend tests", "runner tests"],
            PostMergeRunPlan.Build(definition, "window", changed, PostMergeWindowSuiteModes.Routine));
    }

    private static ProjectExecutionDefinition WithBase() => new(1, [],
        new Dictionary<string, string>(), new("prepare", ["build"], [], []),
        [new("backend-base", "base", 60, "backend smoke"),
         new("product-start", "base", 120, "product start"),
         new("backend-routine", "routine", 10, "backend tests"),
         new("runner-routine", "routine", 10, "runner tests"),
         new("task-server-routine", "routine", 10, "task-server tests"),
         new("frontend-unit", "routine", 10, "frontend tests"),
         new("backend-coverage", "release", 10, "backend coverage")],
        [], [], new Dictionary<string, string>(), null, null, null, null, null);

    [Fact]
    public void DocsOnlyWindowRunsTheBaseSuiteOnly()
    {
        // AGT-3966: a path that maps to no area selects the base suite, not every routine suite.
        Assert.Equal(["prepare", "build", "backend smoke", "product start"],
            PostMergeRunPlan.Build(WithBase(), "window", ["docs/start/README.md", "CHANGELOG.md"],
                PostMergeWindowSuiteModes.Affected));
    }

    [Fact]
    public void BackendWindowRunsTheBaseSuiteAndTheBackendRoutineSuite()
    {
        string[] expected = ["prepare", "build", "backend smoke", "product start", "backend tests"];
        Assert.Equal(expected, PostMergeRunPlan.Build(WithBase(), "window",
            ["backend/Foo.cs", "docs/system/domains/pipeline.md"], PostMergeWindowSuiteModes.Affected));
        // A test project selects its own area's suite instead of every routine suite.
        Assert.Equal(expected, PostMergeRunPlan.Build(WithBase(), "window",
            ["backend.Tests/FooTests.cs"], null));
    }

    [Fact]
    public void SharedLibrarySelectsEveryDotnetRoutineSuite()
    {
        Assert.Equal(["backend tests", "runner tests", "task-server tests"],
            PostMergeRunPlan.SelectAffected(WithBase(), ["contracts/TaskServer.Contracts/PostMergeRun.cs"]));
        Assert.Equal(["runner tests", "frontend tests"],
            PostMergeRunPlan.SelectAffected(WithBase(), ["runner.Tests/A.cs", "frontend/src/a.ts", "scripts/x.sh"]));
    }

    [Theory]
    [InlineData("global.json")]
    [InlineData("Directory.Build.props")]
    [InlineData("backend/Directory.Build.targets")]
    [InlineData("Directory.Packages.props")]
    [InlineData("NuGet.config")]
    [InlineData("agent-taskboard.sln")]
    [InlineData(".agent-studio/project.yml")]
    public void GlobalBuildInputSelectsEveryRoutineSuiteDespiteTheBaseSuite(string path)
    {
        // A repository-wide build input changes every area, so it is not a docs-only path.
        Assert.Equal(["backend tests", "runner tests", "task-server tests", "frontend tests"],
            PostMergeRunPlan.SelectAffected(WithBase(), ["docs/a.md", path]));
    }

    [Fact]
    public void AreaWhoseSuiteTheProjectDoesNotConfigureSelectsEveryRoutineSuite()
    {
        var definition = new ProjectExecutionDefinition(1, [],
            new Dictionary<string, string>(), new("prepare", ["build"], [], []),
            [new("backend-base", "base", 60, "backend smoke"),
             new("backend-routine", "routine", 10, "backend tests"),
             new("frontend-unit", "routine", 10, "frontend tests")],
            [], [], new Dictionary<string, string>(), null, null, null, null, null);

        Assert.Equal(["backend tests", "frontend tests"],
            PostMergeRunPlan.SelectAffected(definition, ["task-server/Program.cs"]));
        Assert.Empty(PostMergeRunPlan.SelectAffected(definition, ["docs/a.md"]));
    }

    [Fact]
    public void ProjectTheAreaMapDoesNotDescribeKeepsEveryRoutineSuiteBesideItsBaseSuite()
    {
        // The area map names Agent Studio's suites. A project with other suite ids has no
        // areas, so a base suite must not silently drop its routine suites from every window.
        var definition = new ProjectExecutionDefinition(1, [],
            new Dictionary<string, string>(), new("prepare", ["build"], [], []),
            [new("smoke", "base", 60, "smoke"),
             new("app-routine", "routine", 10, "app tests"),
             new("web-routine", "routine", 10, "web tests")],
            [], [], new Dictionary<string, string>(), null, null, null, null, null);

        Assert.Equal(["prepare", "build", "smoke", "app tests", "web tests"],
            PostMergeRunPlan.Build(definition, "window", ["src/App/Program.cs"], PostMergeWindowSuiteModes.Affected));
        Assert.Equal(["prepare", "build", "smoke", "app tests", "web tests"],
            PostMergeRunPlan.Build(definition, "window", ["docs/a.md"], PostMergeWindowSuiteModes.Affected));
    }

    [Fact]
    public void NightlyKeepsTheFullRoutineSuiteAndBalancedAddsTheBaseSuite()
    {
        Assert.Equal(["prepare", "build", "backend tests", "runner tests", "task-server tests", "frontend tests"],
            PostMergeRunPlan.Build(WithBase(), "nightly", ["docs/a.md"]));
        Assert.Equal(["prepare", "build", "backend smoke", "product start",
                "backend tests", "runner tests", "task-server tests", "frontend tests"],
            PostMergeRunPlan.Build(WithBase(), "window", ["docs/a.md"], PostMergeWindowSuiteModes.Routine));
    }

    [Fact]
    public void RepositoryBaseSuiteFitsTheTenMinuteBudgetAndRunsAloneForDocs()
    {
        var read = ProjectDefinitionReader.ReadWorkspace(RepositoryRoot.Find());
        Assert.True(read.IsValid, string.Join("; ", read.Issues.Select(issue => $"{issue.Path}: {issue.Message}")));
        var definition = read.Definition!;
        var baseSuites = definition.TestSuites.Where(suite => suite.Category == ProjectTestSuiteCategories.Base).ToArray();
        Assert.NotEmpty(baseSuites);
        Assert.True(baseSuites.Sum(suite => suite.ExpectedDurationSeconds) <= 600,
            "The base suite must finish within about ten minutes on the run host.");
        var docsOnly = PostMergeRunPlan.Build(definition, "window", ["docs/start/README.md"],
            PostMergeWindowSuiteModes.Affected);
        Assert.Equal(baseSuites.Select(suite => suite.Command),
            docsOnly.Skip(1 + definition.Commands.Build.Count));
        var backend = PostMergeRunPlan.Build(definition, "window", ["backend/Host/Program.cs"],
            PostMergeWindowSuiteModes.Affected);
        Assert.Equal([.. baseSuites.Select(suite => suite.Command),
                definition.TestSuites.Single(suite => suite.Id == "backend-routine").Command],
            backend.Skip(1 + definition.Commands.Build.Count));
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

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
