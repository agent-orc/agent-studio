using Microsoft.Extensions.Time.Testing;
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
    public void CrossModuleChangeRunsTheUnionOfMappedProjects()
    {
        Write("src/One/Service.cs");
        Write("src/Two/Service.cs");
        Write("tests/One.Tests/One.Tests.csproj");
        Write("tests/Two.Tests/Two.Tests.csproj");
        Write("tests/Three.Tests/Three.Tests.csproj");
        var policy = Policy() with { FolderToTestProjects =
        [
            new TestFolderMapping { Folder = "src/One", TestProjects = ["tests/One.Tests/One.Tests.csproj"] },
            new TestFolderMapping { Folder = "src/Two", TestProjects = ["tests/Two.Tests/Two.Tests.csproj"] },
            new TestFolderMapping { Folder = "src/Three", TestProjects = ["tests/Three.Tests/Three.Tests.csproj"] },
        ] };

        var plan = DeterministicTestScope.Plan(_root, Verify(),
            ["src/One/Service.cs", "src/Two/Service.cs"], null,
            policy, TaskStates.AutoReview, null);

        Assert.False(plan.Audit.FullSuiteRequired);
        Assert.Equal(
            [
                "dotnet test \"tests/One.Tests/One.Tests.csproj\" --filter Category!=MachineBound",
                "dotnet test \"tests/Two.Tests/Two.Tests.csproj\" --filter Category!=MachineBound",
            ],
            plan.Commands.Where(command => command.Kind == VerifyCommandKind.Test).Select(command => command.Command));
        Assert.Contains(plan.Audit.Reasons, reason => reason.Contains("spans modules: src/One, src/Two"));
        Assert.Contains("mapped modules: src/One, src/Two", plan.Audit.Reasons);
        Assert.Contains("dotnet test --filter Category!=MachineBound", plan.Audit.OmittedTestCommands);
        Assert.Null(plan.Audit.ProjectMapDigest);
        DeterministicTestScope.AssertMapCurrent(_root, policy);
    }

    [Fact]
    public void GlobalInputRunsTheFullSuiteEvenWhenItsFolderIsMapped()
    {
        Write("src/One/Service.cs");
        Write("tests/One.Tests/One.Tests.csproj");
        var policy = Policy() with { FolderToTestProjects =
        [
            new TestFolderMapping { Folder = "src/One", TestProjects = ["tests/One.Tests/One.Tests.csproj"] },
            new TestFolderMapping { Folder = "testsupport", TestProjects = ["tests/One.Tests/One.Tests.csproj"] },
        ] };

        var plan = DeterministicTestScope.Plan(_root, Verify(),
            ["src/One/Service.cs", "testsupport/Clock.cs"], null, policy, TaskStates.AutoReview, null);

        Assert.True(plan.Audit.FullSuiteRequired);
        Assert.Contains("global input changed: testsupport/Clock.cs (testsupport/**)", plan.Audit.Reasons);
    }

    [Fact]
    public void ProjectCommandReusesADeclaredCommandAndInheritsTheDeclaredFlags()
    {
        var verify = new VerifyPlan(
        [
            new VerifyCommand(VerifyEcosystem.Custom, VerifyCommandKind.Test, "",
                "dotnet test a.Tests/A.Tests.csproj --no-build --filter Category!=MachineBound")
            {
                Shell = VerifyCommandShell.Bash,
            },
        ], VerifyPlan.SourceProjectDefinition);

        var declared = DeterministicTestScope.DotNetProjectCommand(verify, "a.Tests/A.Tests.csproj", null)!;
        var sliced = DeterministicTestScope.DotNetProjectCommand(verify, "a.Tests/A.Tests.csproj", ["FooTests"])!;
        var other = DeterministicTestScope.DotNetProjectCommand(verify, "b.Tests/B.Tests.csproj", null)!;

        Assert.Same(verify.Commands[0], declared);
        Assert.Equal("dotnet test a.Tests/A.Tests.csproj --no-build --filter "
            + "\"(Category!=MachineBound)&(FullyQualifiedName~FooTests)\"", sliced.Command);
        Assert.Equal("dotnet test \"b.Tests/B.Tests.csproj\" --no-build --filter Category!=MachineBound", other.Command);
        Assert.Equal(VerifyCommandShell.Bash, other.Shell);
    }

    /// <summary>
    /// The generated map follows the project graph; class selection narrows a
    /// directly referencing test project and keeps a transitive one whole.
    /// </summary>
    [Fact]
    public void GeneratedMapSelectsClassesForDirectReferencesAndWholeProjectsForTransitiveOnes()
    {
        Project("lib/Lib.csproj");
        Project("app/App.csproj", "../lib/Lib.csproj");
        Project("app.Tests/App.Tests.csproj", "../app/App.csproj");
        Project("lib.Tests/Lib.Tests.csproj", "../lib/Lib.csproj");
        Code("lib/Core.cs", "namespace L;\npublic sealed class Core { }");
        Code("app/Widget.cs", "namespace A;\npublic sealed class Widget { }");
        TestClass("app.Tests", "WidgetTests", "new A.Widget()");
        TestClass("lib.Tests", "CoreTests", "new L.Core()");
        foreach (var name in new[] { "OneTests", "TwoTests", "ThreeTests" })
        {
            TestClass("app.Tests", name, "1");
            TestClass("lib.Tests", name, "1");
        }
        var verify = Verify();
        var map = GeneratedTestProjectMap.Generate(_root, verify);
        var selection = new DotNetReferenceSelection { Time = new FakeTimeProvider() };

        Assert.Equal(["app.Tests/App.Tests.csproj"], map.Rows.Single(row => row.Folder == "app").TestProjects);
        Assert.Equal(["app.Tests/App.Tests.csproj", "lib.Tests/Lib.Tests.csproj"],
            map.Rows.Single(row => row.Folder == "lib").TestProjects);

        var app = DeterministicTestScope.Plan(_root, verify, ["app/Widget.cs"], null, null,
            TaskStates.AutoReview, TestExecutionLevels.WorkPackage, map, selection);
        Assert.Equal(
            "dotnet test \"app.Tests/App.Tests.csproj\" --filter "
            + "\"(Category!=MachineBound)&(FullyQualifiedName~WidgetTests)\"",
            Assert.Single(app.Commands, command => command.Kind == VerifyCommandKind.Test).Command);
        Assert.Equal(["WidgetTests"], app.Audit.SelectedTestClasses);
        Assert.Equal(map.Digest, app.Audit.ProjectMapDigest);

        var lib = DeterministicTestScope.Plan(_root, verify, ["lib/Core.cs"], null, null,
            TaskStates.AutoReview, TestExecutionLevels.WorkPackage, map, selection);
        var tests = lib.Commands.Where(command => command.Kind == VerifyCommandKind.Test)
            .Select(command => command.Command).ToList();
        Assert.Contains("dotnet test \"app.Tests/App.Tests.csproj\" --filter Category!=MachineBound", tests);
        Assert.Contains("dotnet test \"lib.Tests/Lib.Tests.csproj\" --filter "
            + "\"(Category!=MachineBound)&(FullyQualifiedName~CoreTests)\"", tests);
        Assert.Contains("app.Tests/App.Tests.csproj: whole project, lib/Lib.csproj reaches it only through another project",
            lib.Audit.Reasons);
    }

    [Fact]
    public void ClassSelectionWithoutAReferencingClassDropsThatProjectsCommand()
    {
        Project("app/App.csproj");
        Project("app.Tests/App.Tests.csproj", "../app/App.csproj");
        Code("app/Unused.cs", "namespace A;\ninternal sealed class Unused { }");
        TestClass("app.Tests", "OneTests", "1");
        var verify = Verify();

        var plan = DeterministicTestScope.Plan(_root, verify, ["app/Unused.cs"], null, null,
            TaskStates.AutoReview, TestExecutionLevels.WorkPackage,
            GeneratedTestProjectMap.Generate(_root, verify),
            new DotNetReferenceSelection { Time = new FakeTimeProvider() });

        Assert.False(plan.Audit.FullSuiteRequired);
        Assert.DoesNotContain(plan.Commands, command => command.Kind == VerifyCommandKind.Test);
        Assert.Contains(plan.Audit.Reasons, reason => reason.StartsWith("app.Tests/App.Tests.csproj: no per-card command",
            StringComparison.Ordinal));
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

    private void Write(string relative, string content = "<Project />")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void Project(string relative, params string[] references)
        => Write(relative, "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>"
            + string.Concat(references.Select(reference => $"<ProjectReference Include=\"{reference}\" />"))
            + "</ItemGroup></Project>");

    private void Code(string relative, string content) => Write(relative, content + "\n");

    private void TestClass(string project, string name, string body)
        => Write($"{project}/{name}.cs",
            $"namespace T;\npublic sealed class {name}\n{{\n    [Fact]\n    public void Runs() => _ = {body};\n}}\n");
}
