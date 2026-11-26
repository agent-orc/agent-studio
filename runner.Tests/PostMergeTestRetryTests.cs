using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class PostMergeTestRetryTests
{
    [Theory]
    [InlineData("runner", 0)]
    [InlineData("runner", 1)]
    [InlineData("backend", 0)]
    [InlineData("task-server", 0)]
    public async Task TheoryFailureIsRepeatedOnceAndOnlyRecoveryIsFlaky(string project, int repeatExit)
    {
        var tests = PostMergeAttributor.FailedTests(
            "  Failed AgentRunner.Tests.RunTargetRenderingTests.NetworkRemoval_RequiresProofOfAbsenceWhenInspectFails(listCommand: \"exit 1\", removed: False) [1 ms]");
        const string name = "AgentRunner.Tests.RunTargetRenderingTests.NetworkRemoval_RequiresProofOfAbsenceWhenInspectFails";
        Assert.Equal([name], tests);
        var commands = new List<string>();
        var verdict = await PostMergeTestRetry.RepeatAsync(new(
            $"dotnet test {project}.Tests/Tests.csproj --no-build --filter Category!=MachineBound",
            1, false, FailedTests: tests), observed => observed, command =>
        {
            commands.Add(command);
            return Task.FromResult((repeatExit, false, ""));
        });
        Assert.Contains("FullyQualifiedName~" + name, Assert.Single(commands));
        Assert.Equal(repeatExit, verdict.ExitCode);
        Assert.Equal(repeatExit == 0 ? new[] { name } : [], verdict.FlakyTests);
        Assert.True(verdict.Retried);
    }

    [Theory]
    [InlineData("bash scripts/npm-frontend.sh run test:ci", " FAIL  src/example.spec.ts > Example > renders", 0)]
    [InlineData("bash scripts/npm-frontend.sh run test:ci", " FAIL  src/example.spec.ts > Example > renders", 1)]
    [InlineData("dotnet test runner.Tests/Tests.csproj", "Test host crashed", 0)]
    public async Task EverySuiteKindRepeatsEvenWithoutAFocusedFilter(string command, string output, int repeatExit)
    {
        var calls = new List<string>();
        var verdict = await PostMergeTestRetry.RepeatAsync(new(command, 1, false, output), tests => tests,
            retry => { calls.Add(retry); return Task.FromResult((repeatExit, false, "")); });
        Assert.Equal(command, Assert.Single(calls));
        Assert.Equal(repeatExit, verdict.ExitCode);
        Assert.True(verdict.Retried);
        if (repeatExit == 0 && command.StartsWith("bash", StringComparison.Ordinal))
            Assert.Equal(["src/example.spec.ts > Example > renders"], verdict.FlakyTests);
    }

    [Fact]
    public async Task PartialRecoveryRecordsOnlyTheRecoveredTest()
    {
        var verdict = await PostMergeTestRetry.RepeatAsync(new("dotnet test runner.Tests/Tests.csproj",
            1, false, FailedTests: ["Runner.One", "Runner.Two"]), tests => tests,
            command => Task.FromResult((command.Contains("Runner.One", StringComparison.Ordinal) ? 0 : 1, false, "")));
        Assert.Equal(1, verdict.ExitCode);
        Assert.Equal(["Runner.One"], verdict.FlakyTests);
        Assert.Equal(["Runner.Two"], verdict.FailedTests);
    }

    [Fact]
    public async Task EveryRepeatIsRecordedWithItsTestsAndDuration()
    {
        var sha = new string('b', 40);
        var recorded = new List<PostMergeTestRun>();
        await PostMergeTestRetry.RepeatAsync(new("dotnet test runner.Tests/Tests.csproj",
            1, false, FailedTests: ["Runner.One", "Runner.Two"]), tests => tests,
            command => Task.FromResult((command.Contains("Runner.One", StringComparison.Ordinal) ? 0 : 1, false, "")),
            recorded.Add, sha);
        Assert.Equal([["Runner.One"], ["Runner.Two"]], recorded.Select(run => run.Tests.ToArray()));
        Assert.All(recorded, run =>
        {
            Assert.Equal(PostMergeRunPhases.Repeat, run.Phase);
            Assert.Equal(sha, run.Sha);
            Assert.True(run.DurationMs >= 0);
        });
        Assert.Equal([PostMergeTestResults.Pass, PostMergeTestResults.Fail],
            recorded.Select((run, index) => run.ResultOf(run.Tests[0])));
    }
}
