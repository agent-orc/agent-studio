using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class PostMergeAttributorTests
{
    [Fact]
    public void ReadsFailingTestsAndNarrowsTheExistingFilter()
    {
        var tests = PostMergeAttributor.FailedTests("  Failed A.One [2 ms]\n  Failed B.Two [1 ms]\n");
        Assert.Equal(["A.One", "B.Two"], tests);
        var command = PostMergeAttributor.FocusedCommand(
            "dotnet test backend.Tests/OrchestratorApi.Tests.csproj --no-build --filter Category!=MachineBound", tests);
        Assert.Contains("(Category!=MachineBound)&(FullyQualifiedName~A.One|FullyQualifiedName~B.Two)", command);
        Assert.Equal("task_123", PostMergeAttributor.TaskIdFromMergeMessage(
            "Integrate task_123 (abcdef012345)"));
    }

    [Fact]
    public void ActiveQuarantineRemovesOnlyItsExactTest()
    {
        var root = Path.Combine(Path.GetTempPath(), "post-merge-quarantine-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".agent-studio"));
            // clock-independent: BlockingTests receives fixed dates below; the wall clock is never read.
            File.WriteAllText(Path.Combine(root, ".agent-studio", "test-quarantine.json"),
                """
                {"tests":[{"test":"A.One","card":"AGT-1","expiresOn":"2026-10-28"}]}
                """);
            Assert.Equal(["B.Two"], PostMergeAttributor.BlockingTests(root, ["A.One", "B.Two"],
                new DateTimeOffset(2026, 10, 27, 0, 0, 0, TimeSpan.Zero)));
            Assert.Equal(["A.One"], PostMergeAttributor.BlockingTests(root, ["A.One"],
                new DateTimeOffset(2026, 10, 29, 0, 0, 0, TimeSpan.Zero)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void SuspectMapIncludesTransitiveTestProjectReferences()
    {
        var root = Path.Combine(Path.GetTempPath(), "post-merge-map-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var folder in new[] { "backend", "runner.Tests", "other" })
                Directory.CreateDirectory(Path.Combine(root, folder));
            File.WriteAllText(Path.Combine(root, "backend", "App.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(root, "other", "Other.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(root, "runner.Tests", "Runner.Tests.csproj"),
                "<Project><ItemGroup><ProjectReference Include=\"..\\backend\\App.csproj\" />"
                + "</ItemGroup></Project>");
            var definition = new ProjectExecutionDefinition(1, [], new Dictionary<string, string>(),
                new("", [], [], []),
                [new("backend-routine", "routine", 10, "dotnet test backend/App.csproj"),
                 new("runner-routine", "routine", 10, "dotnet test runner.Tests/Runner.Tests.csproj")],
                [], [], new Dictionary<string, string>(), null, null, null, null, null);
            var map = new PostMergeProjectMap(root,
                ["backend/App.csproj", "other/Other.csproj", "runner.Tests/Runner.Tests.csproj"]);
            const string failed = "dotnet test runner.Tests/Runner.Tests.csproj --no-build";
            Assert.True(map.CouldAffect(definition, ["backend/Changed.cs"], failed));
            Assert.False(map.CouldAffect(definition, ["other/Changed.cs"], failed));
            Assert.True(map.CouldAffect(definition, ["other/Other.csproj"], failed));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
