using Xunit;

namespace AgentStudio.Tests;

public sealed class DeterministicTestScopeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "deterministic-test-scope-" + Guid.NewGuid().ToString("N"));

    public DeterministicTestScopeTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void MappedFolderRunsOnlyItsProjectAndIncludesNewTestFile()
    {
        Write("src/One/Service.cs");
        Write("tests/One.Tests/One.Tests.csproj");
        Write("tests/Two.Tests/Two.Tests.csproj");
        var policy = Policy();

        var plan = DeterministicTestScope.Plan(_root, Verify(),
            ["src/One/Service.cs", "tests/One.Tests/NewTests.cs"],
            new Dictionary<string, string> { ["tests/One.Tests/NewTests.cs"] = "A" },
            policy with { FolderToTestProjects =
            [
                new TestFolderMapping { Folder = "src/One", Module = "one", TestProjects = ["tests/One.Tests/One.Tests.csproj"] },
                new TestFolderMapping { Folder = "tests/One.Tests", Module = "one", TestProjects = ["tests/One.Tests/One.Tests.csproj"] },
            ] }, TaskStates.AutoReview, null);

        Assert.False(plan.Audit.FullSuiteRequired);
        Assert.Contains(plan.Commands, command => command.Command.Contains("One.Tests.csproj"));
        Assert.DoesNotContain(plan.Commands, command => command.Command.Contains("Two.Tests.csproj"));
        Assert.Contains(plan.Audit.Reasons, reason => reason.Contains("new test file"));
        Assert.NotNull(plan.Audit.Digest);
        Assert.Contains("A tests/One.Tests/NewTests.cs", plan.Audit.DiffStatuses);
        var modified = DeterministicTestScope.Plan(_root, Verify(),
            ["src/One/Service.cs", "tests/One.Tests/NewTests.cs"],
            new Dictionary<string, string> { ["tests/One.Tests/NewTests.cs"] = "M" },
            policy with { FolderToTestProjects =
            [
                new TestFolderMapping { Folder = "src/One", Module = "one", TestProjects = ["tests/One.Tests/One.Tests.csproj"] },
                new TestFolderMapping { Folder = "tests/One.Tests", Module = "one", TestProjects = ["tests/One.Tests/One.Tests.csproj"] },
            ] }, TaskStates.AutoReview, null);
        Assert.NotEqual(plan.Audit.Digest, modified.Audit.Digest);
    }

    [Fact]
    public void UnmappedNewDirectoryForcesFullSuiteAndGuardFailsLoudly()
    {
        Write("src/One/Service.cs");
        Write("src/New/Service.cs");
        Write("tests/One.Tests/One.Tests.csproj");
        var policy = Policy();

        var plan = DeterministicTestScope.Plan(_root, Verify(),
            ["src/New/Service.cs"],
            new Dictionary<string, string> { ["src/New/Service.cs"] = "A" },
            policy, TaskStates.AutoReview, null);

        Assert.True(plan.Audit.FullSuiteRequired);
        Assert.Equal("deterministic-map-full-fallback", plan.Audit.Selector);
        Assert.Contains(plan.Commands, command => command.Command == "dotnet test --filter Category!=MachineBound");
        Assert.Contains(plan.Audit.Reasons, reason => reason.Contains("unmapped changed path"));
        Assert.Contains("src/New", Assert.Throws<InvalidOperationException>(
            () => DeterministicTestScope.AssertMapCurrent(_root, policy)).Message);
    }

    [Fact]
    public void CrossModuleChangeForcesFullSuite()
    {
        Write("src/One/Service.cs");
        Write("src/Two/Service.cs");
        Write("tests/One.Tests/One.Tests.csproj");
        Write("tests/Two.Tests/Two.Tests.csproj");
        var policy = Policy() with { FolderToTestProjects =
        [
            new TestFolderMapping { Folder = "src/One", TestProjects = ["tests/One.Tests/One.Tests.csproj"] },
            new TestFolderMapping { Folder = "src/Two", TestProjects = ["tests/Two.Tests/Two.Tests.csproj"] },
        ] };

        var plan = DeterministicTestScope.Plan(_root, Verify(),
            ["src/One/Service.cs", "src/Two/Service.cs"], null,
            policy, TaskStates.AutoReview, null);

        Assert.True(plan.Audit.FullSuiteRequired);
        Assert.Contains(plan.Audit.Reasons, reason => reason.Contains("spans modules"));
        DeterministicTestScope.AssertMapCurrent(_root, policy);
    }

    private static TestExecutionPolicy Policy() => new()
    {
        MappedSourceRoots = ["src"],
        FolderToTestProjects =
        [
            new TestFolderMapping { Folder = "src/One", TestProjects = ["tests/One.Tests/One.Tests.csproj"] },
        ],
    };

    private static VerifyPlan Verify() => new(
    [
        new VerifyCommand(VerifyEcosystem.DotNet, VerifyCommandKind.Build, "", "dotnet build"),
        new VerifyCommand(VerifyEcosystem.DotNet, VerifyCommandKind.Test, "",
            "dotnet test --filter Category!=MachineBound"),
    ], VerifyPlan.SourceBuildProfile);

    private void Write(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<Project />");
    }
}
