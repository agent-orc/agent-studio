using AgentRunner;
using AgentStudio.TaskServer.Contracts;
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
