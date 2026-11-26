using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>RT-3 (AGT-3988, RT-D6 B): covering folders and partial builds of a failing test.</summary>
public sealed class PostMergeCoveringFoldersTests
{
    private static PostMergeCoveringFolders.Index Index(Dictionary<string, string> files)
        => new(files.Keys.ToArray(), path => files.GetValueOrDefault(path));

    [Fact]
    public void TestFileIsFoundByClassNameAndNamesTypeFolders()
    {
        var index = Index(new()
        {
            ["backend/Features/Pipeline/PostMerge/PostMergeRunStore.cs"] = "",
            ["backend/Features/Pipeline/PostMerge/PostMergeIncidentService.cs"] = "",
            ["contracts/TaskServer.Contracts/PostMergeRun.cs"] = "",
            ["backend/Features/Tasks/TaskStates.cs"] = "",
            ["backend/Features/Shared/Duplicate.cs"] = "",
            ["backend/Features/Other/Duplicate.cs"] = "",
            ["backend/Features/Third/Duplicate.cs"] = "",
            ["backend/Short/Short.cs"] = "",
            ["backend/bin/Debug/Generated.cs"] = "",
            ["backend.Tests/TestHostFactory.cs"] = "",
            // The file name differs from the class; the declaration decides.
            ["backend.Tests/PostMerge/StoreTests.cs"] = """
                namespace AgentStudio.Tests;
                public sealed class PostMergeRunStoreTests
                {
                    // PostMergeRunStore, PostMergeIncidentService, TaskStates, Duplicate, Short, Generated, TestHostFactory
                    PostMergeRun run;
                }
                """,
        });
        var result = index.For("AgentStudio.Tests.PostMergeRunStoreTests.Green_closes_incident", "backend.Tests");
        Assert.Equal("backend.Tests/PostMerge/StoreTests.cs", result.TestFile);
        // Hub types (TaskStates), types in more than two folders (Duplicate), identifiers
        // under eight characters (Short), build output and the test project's own helpers are ignored.
        Assert.Equal(["backend/Features/Pipeline/PostMerge", "contracts/TaskServer.Contracts"], result.Folders);
        Assert.False(result.Fallback);
    }

    [Fact]
    public void UnmappedOrUnknownTestsFallBackToTheArea()
    {
        var index = Index(new()
        {
            ["runner.Tests/PlainTests.cs"] = "public sealed class PlainTests { void Run() { } }",
            ["frontend/src/app/app.spec.ts"] = "",
        });
        var plain = index.For("AgentRunner.Tests.PlainTests.Run", "runner.Tests");
        Assert.Equal("runner.Tests/PlainTests.cs", plain.TestFile);
        Assert.Empty(plain.Folders);
        Assert.True(plain.Fallback);
        var missing = index.For("AgentRunner.Tests.MissingTests.Run", "runner.Tests");
        Assert.Null(missing.TestFile);
        Assert.True(missing.Fallback);
        var vitest = index.For("src/app/app.spec.ts > App > renders", null);
        Assert.Equal("frontend/src/app/app.spec.ts", vitest.TestFile);
        Assert.True(vitest.Fallback);
    }

    [Fact]
    public void MoreThanTwelveFoldersFallBackButAreStillRecorded()
    {
        var files = new Dictionary<string, string>();
        var names = Enumerable.Range(0, 13).Select(i => $"Component{i:00}Service").ToArray();
        foreach (var (name, i) in names.Select((name, i) => (name, i))) files[$"backend/Area{i}/{name}.cs"] = "";
        files["backend.Tests/WideTests.cs"] = "class WideTests { " + string.Join(" ", names) + " }";
        var result = Index(files).For("AgentStudio.Tests.WideTests.Everything", "backend.Tests");
        Assert.Equal(13, result.Folders.Count);
        Assert.True(result.Fallback);
    }

    [Fact]
    public void PartialBuildIsTheTestProjectOrTheFrontendBuildNeverTheSolution()
    {
        var definition = new ProjectExecutionDefinition(1, [], new Dictionary<string, string>(),
            new(".agent-studio/prepare", ["dotnet build agent-taskboard.sln --no-restore",
                "bash scripts/check-release-build.sh", "bash scripts/npm-frontend.sh run build"], [], []),
            [], [], [], new Dictionary<string, string>(), null, null, null, null, null);
        Assert.Equal("dotnet build backend.Tests/OrchestratorApi.Tests.csproj --no-restore",
            PostMergeAttributor.PartialBuildCommand(definition,
                "dotnet test backend.Tests/OrchestratorApi.Tests.csproj --no-build --filter Category!=MachineBound",
                ["AgentStudio.Tests.A.B"]));
        Assert.Equal("bash scripts/npm-frontend.sh run build",
            PostMergeAttributor.PartialBuildCommand(definition, "bash scripts/npm-frontend.sh run test:ci",
                ["src/app/app.spec.ts > App > renders"]));
        Assert.Null(PostMergeAttributor.PartialBuildCommand(definition, "bash scripts/base-product-start.sh",
            ["src/app/app.spec.ts > App > renders"]));
        Assert.Null(PostMergeAttributor.PartialBuildCommand(definition, "bash scripts/npm-frontend.sh run test:ci",
            ["Unnamed failure"]));
    }

    [Fact]
    public void VitestFailuresFocusOnTheirSpecFiles()
    {
        Assert.Equal("bash scripts/npm-frontend.sh run test:ci -- --include src/a.spec.ts --include src/b.spec.ts",
            PostMergeAttributor.FocusedVitestCommand("bash scripts/npm-frontend.sh run test:ci",
                ["src/a.spec.ts > A > one", "src/a.spec.ts > A > two", "src/b.spec.ts > B"]));
        Assert.Equal("npm run test:ci -- --watch=false --include src/a.spec.ts",
            PostMergeAttributor.FocusedVitestCommand("npm run test:ci -- --watch=false", ["src/a.spec.ts > A"]));
        Assert.Null(PostMergeAttributor.FocusedVitestCommand("bash scripts/npm-frontend.sh run test:ci", ["../x.spec.ts > A"]));
        Assert.Null(PostMergeAttributor.FocusedVitestCommand("dotnet test x.csproj", ["src/a.spec.ts > A"]));
    }

    [Fact]
    public void FocusedRunResultsArePerTest()
    {
        var run = new PostMergeTestRun(PostMergeRunPhases.Bisect, new string('a', 40), ["A.One", "B.Two"], 1, false,
            "  Failed A.One [3 ms]\n", 12);
        Assert.Equal(PostMergeTestResults.Fail, run.ResultOf("A.One"));
        Assert.Equal(PostMergeTestResults.Pass, run.ResultOf("B.Two"));
        Assert.Equal(PostMergeTestResults.Unknown, (run with { Output = "host crashed" }).ResultOf("B.Two"));
        Assert.Equal(PostMergeTestResults.Fail, (run with { Tests = ["A.One"], Output = "" }).ResultOf("A.One"));
        Assert.Equal(new PostMergeFocusedRun(PostMergeRunPhases.Bisect, new string('a', 40), 2, 1, false, 12), run.ToReceipt());
    }

    [Fact]
    public void EnvelopeProbeDigestsNamesAndReadsTheQuota()
    {
        Assert.Equal(PostMergeEnvelopeProbe.AllowlistDigest(["PATH", "HOME"]),
            PostMergeEnvelopeProbe.AllowlistDigest(["HOME", "PATH", "PATH"]));
        Assert.NotEqual(PostMergeEnvelopeProbe.AllowlistDigest(["PATH"]),
            PostMergeEnvelopeProbe.AllowlistDigest(["PATH", "HOME"]));
        var declared = new Dictionary<string, string> { ["A"] = "1" };
        Assert.NotEqual(PostMergeEnvelopeProbe.DeclaredEnvironmentDigest(declared),
            PostMergeEnvelopeProbe.DeclaredEnvironmentDigest(new Dictionary<string, string> { ["A"] = "2" }));
        Assert.Equal(3, PostMergeEnvelopeProbe.ProcessorCount(declared, "300000 100000", 12));
        Assert.Equal(12, PostMergeEnvelopeProbe.ProcessorCount(declared, "max 100000", 12));
        Assert.Equal(6, PostMergeEnvelopeProbe.ProcessorCount(
            new Dictionary<string, string> { ["DOTNET_PROCESSOR_COUNT"] = "6" }, "300000 100000", 12));
        Assert.Equal("backend-routine=4,runner-routine=1",
            PostMergeEnvelopeProbe.ShardPlan([("backend-routine", "4"), ("runner-routine", "1")]));
    }

    [Fact]
    public async Task LoadSamplerKeepsThePeak()
    {
        var samples = new Queue<double?>([0.2, 1.5, null, 0.9]);
        var sampler = new PostMergeLoadSampler(0.4, () => samples.Count > 0 ? samples.Dequeue() : 0.1,
            TimeSpan.FromMilliseconds(5));
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (samples.Count > 0 && waited.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
        await sampler.DisposeAsync();
        Assert.Equal(1.5, sampler.Peak);
    }
}
