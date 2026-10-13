using System.Text;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3085: the deferred Remote Review plan selects the .NET test classes
/// that name a changed type instead of the whole impacted test project. The
/// planner is pure here: temp-directory fixtures, an in-memory overlay and
/// index memo, a fake clock, and no git process.
/// </summary>
public sealed class DotNetReferenceSelectionTests : IDisposable
{
    private const string Project = "backend.Tests/OrchestratorApi.Tests.csproj";
    private const string WholeProjectCommand =
        "dotnet test \"backend.Tests/OrchestratorApi.Tests.csproj\" --filter Category!=MachineBound";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "reference-selection-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new();

    public DotNetReferenceSelectionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void ProductionDiff_SelectsTheTestClassThatNamesTheChangedType()
    {
        FooServiceFixture();

        var result = Plan(["backend/Features/Foo/FooService.cs"]);

        Assert.Equal(
            "dotnet test \"backend.Tests/OrchestratorApi.Tests.csproj\" --filter "
            + "\"(Category!=MachineBound)&(FullyQualifiedName~FooServiceTests)\"",
            DotNetTestCommand(result));
        Assert.Equal(["FooServiceTests"], result.Audit.SelectedTestClasses);
        Assert.Equal(TestExecutionLevels.WorkPackage, result.Audit.Level);
    }

    [Fact]
    public void Closure_CarriesAFixtureToTheTestClassThatUsesIt()
    {
        BackendRepository();
        Write("backend/FooService.cs", "namespace AgentStudio;\npublic sealed class FooService { }\n");
        Write("backend.Tests/FooFixture.cs", """
            namespace AgentStudio.Tests;
            internal sealed class FooFixture
            {
                public FooService Service { get; } = new();
            }
            """);
        Write("backend.Tests/QuxTests.cs", """
            namespace AgentStudio.Tests;
            public sealed class QuxTests
            {
                [Fact]
                public void Uses() => _ = new FooFixture();
            }
            """);
        Unrelated("BarTests", "BazTests");

        var result = Plan(["backend/FooService.cs"]);

        Assert.Equal(["QuxTests"], result.Audit.SelectedTestClasses);
        Assert.DoesNotContain("FooFixture", DotNetTestCommand(result));
        var audit = Assert.Single(result.Audit.ReferenceSelections);
        Assert.Equal(2, audit.ClosureDepth);
        Assert.Equal(["backend.Tests/FooFixture.cs", "backend.Tests/QuxTests.cs"], audit.SelectedFiles);
    }

    [Fact]
    public void BaseClass_SelectsTheDerivedTestClassInAnotherFile()
    {
        BackendRepository();
        Write("backend/StoreService.cs", "namespace AgentStudio;\npublic sealed class StoreService { }\n");
        Write("backend.Tests/AbstractStoreTests.cs", """
            namespace AgentStudio.Tests;
            public abstract class AbstractStoreTests
            {
                [Fact]
                public void Stores() => _ = new StoreService();
            }
            """);
        Write("backend.Tests/JsonStoreTests.cs", """
            namespace AgentStudio.Tests;
            public sealed class JsonStoreTests : AbstractStoreTests { }
            """);
        Unrelated("BarTests", "BazTests");

        var result = Plan(["backend/StoreService.cs"]);

        // The abstract base cannot run on its own; its facts run as the derived class.
        Assert.Equal(["JsonStoreTests"], result.Audit.SelectedTestClasses);
    }

    [Fact]
    public void NestedType_IsNotASeed()
    {
        BackendRepository();
        Write("backend/FooService.cs", """
            namespace AgentStudio;
            public sealed class FooService
            {
                public sealed record Options(int Limit);
            }
            """);
        Write("backend.Tests/FooServiceTests.cs", """
            namespace AgentStudio.Tests;
            public sealed class FooServiceTests
            {
                private sealed record Options(string Name);

                [Fact]
                public void Runs() => _ = new FooService();
            }
            """);
        Write("backend.Tests/OptionsUserTests.cs", """
            namespace AgentStudio.Tests;
            public sealed class OptionsUserTests
            {
                [Fact]
                public void Reads() => _ = new Options();
            }
            """);
        Unrelated("BarTests");

        var result = Plan(["backend/FooService.cs"]);

        Assert.Equal(["FooServiceTests"], result.Audit.SelectedTestClasses);
        Assert.DoesNotContain("Options", Assert.Single(result.Audit.ReferenceSelections).SeedTypes);
    }

    [Fact]
    public void ChangedAttribute_SelectsAFileThatUsesItsShortForm()
    {
        BackendRepository();
        Write("backend/FooAttribute.cs", """
            namespace AgentStudio;
            public sealed class FooAttribute : System.Attribute { }
            """);
        Write("backend.Tests/MarkedTests.cs", """
            namespace AgentStudio.Tests;
            [Foo]
            public sealed class MarkedTests
            {
                [Fact]
                public void Marked() { }
            }
            """);
        Unrelated("BarTests");

        var result = Plan(["backend/FooAttribute.cs"]);

        Assert.Equal(["MarkedTests"], result.Audit.SelectedTestClasses);
    }

    [Fact]
    public void ChangedFixture_SelectsTheTestClassesThatUseIt()
    {
        BackendRepository();
        Write("backend.Tests/Fixtures/TempRepository.cs", """
            namespace AgentStudio.Tests;
            internal sealed class TempRepository { }
            """);
        Write("backend.Tests/UsesRepositoryTests.cs", """
            namespace AgentStudio.Tests;
            public sealed class UsesRepositoryTests
            {
                [Fact]
                public void Uses() => _ = new TempRepository();
            }
            """);
        Unrelated("BarTests", "BazTests");

        var result = Plan(["backend.Tests/Fixtures/TempRepository.cs"]);

        Assert.Equal(["UsesRepositoryTests"], result.Audit.SelectedTestClasses);
        Assert.Equal(
            "dotnet test \"backend.Tests/OrchestratorApi.Tests.csproj\" --filter "
            + "\"(Category!=MachineBound)&(FullyQualifiedName~UsesRepositoryTests)\"",
            DotNetTestCommand(result));
    }

    [Theory]
    [InlineData("attribute")]
    [InlineData("element")]
    public void ProcessLevelReference_KeepsTheWholeProject(string form)
    {
        var reference = form == "attribute"
            ? """<ProjectReference Include="../task-server/TaskServer.csproj" ReferenceOutputAssembly="false" />"""
            : """
              <ProjectReference Include="..\task-server\TaskServer.csproj">
                <ReferenceOutputAssembly>false</ReferenceOutputAssembly>
              </ProjectReference>
              """;
        BackendRepository(reference);
        Write("task-server/TaskServer.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write("task-server/LeaseStore.cs", "namespace AgentStudio.TaskServer;\npublic sealed class LeaseStore { }\n");
        Write("backend.Tests/LeaseStoreTests.cs", """
            namespace AgentStudio.Tests;
            public sealed class LeaseStoreTests { [Fact] public void Runs() => _ = new LeaseStore(); }
            """);
        Unrelated("BarTests", "BazTests");

        AssertWholeProject(Plan(["task-server/LeaseStore.cs"]), "process-level reference: task-server/TaskServer.csproj");
    }

    [Fact]
    public void ChangedProjectFile_KeepsTheWholeProject()
    {
        FooServiceFixture();

        AssertWholeProject(
            Plan(["backend/OrchestratorApi.csproj", "backend/Features/Foo/FooService.cs"]),
            "non-C# input changed: backend/OrchestratorApi.csproj");
    }

    [Fact]
    public void ChangedNonCSharpTestInput_KeepsTheWholeProject()
    {
        FooServiceFixture();
        Write("backend.Tests/Fixtures/sample.json", "{}");

        AssertWholeProject(
            Plan(["backend.Tests/Fixtures/sample.json"]),
            "non-C# input changed: backend.Tests/Fixtures/sample.json");
    }

    [Fact]
    public void RepositoryBuildInput_KeepsTheWholeProject()
    {
        FooServiceFixture();
        Write("Directory.Build.props", "<Project />");

        AssertWholeProject(
            Plan(["Directory.Build.props", "backend/Features/Foo/FooService.cs"]),
            "repository-level build input changed: Directory.Build.props");
    }

    [Theory]
    [InlineData("backend/Host/EndpointMapping.cs")]
    [InlineData("backend/Program.cs")]
    public void CompositionRoot_KeepsTheWholeProject(string path)
    {
        FooServiceFixture();
        Write(path, "namespace AgentStudio.Host;\npublic static class EndpointMapping { }\n");

        AssertWholeProject(Plan([path]), $"composition root changed: {path}");
    }

    [Fact]
    public void ChangedFileWithoutTopLevelType_KeepsTheWholeProject()
    {
        FooServiceFixture();
        Write("backend/GlobalUsings.cs", "global using System.Text;\n");

        AssertWholeProject(
            Plan(["backend/GlobalUsings.cs"]),
            "changed file declares no top-level type: backend/GlobalUsings.cs");
    }

    [Fact]
    public void DeletedFile_KeepsTheWholeProject()
    {
        FooServiceFixture();

        var result = Plan(
            ["backend/Features/Foo/Removed.cs"],
            Overlay(new Dictionary<string, string?> { ["backend/Features/Foo/Removed.cs"] = null }));

        AssertWholeProject(result, "changed file was deleted: backend/Features/Foo/Removed.cs");
    }

    [Fact]
    public void SelectionAboveHalfOfTheClasses_KeepsTheWholeProject()
    {
        BackendRepository();
        Write("backend/FooService.cs", "namespace AgentStudio;\npublic sealed class FooService { }\n");
        Write("backend.Tests/FirstTests.cs", "public sealed class FirstTests { FooService _s = new(); }\n");
        Write("backend.Tests/SecondTests.cs", "public sealed class SecondTests { FooService _s = new(); }\n");
        Unrelated("BarTests");

        var result = Plan(["backend/FooService.cs"]);

        AssertWholeProject(result, "2 of 3 test classes selected, more than half of the project");
        var audit = Assert.Single(result.Audit.ReferenceSelections);
        Assert.Equal(["FooService"], audit.SeedTypes);
    }

    [Fact]
    public void FilterAbove16Kilobytes_KeepsTheWholeProject()
    {
        BackendRepository();
        Write("backend/FooService.cs", "namespace AgentStudio;\npublic sealed class FooService { }\n");
        var referencing = new StringBuilder("namespace AgentStudio.Tests;\n");
        var unrelated = new StringBuilder("namespace AgentStudio.Tests;\n");
        for (var index = 0; index < 400; index++)
        {
            referencing.Append($"public sealed class ReferencingTheChangedServiceNumber{index:000}Tests ")
                .Append("{ FooService _s = new(); }\n");
            unrelated.Append($"public sealed class UnrelatedNumber{index:000}Tests {{ }}\n");
        }
        unrelated.Append("public sealed class UnrelatedLastTests { }\n");
        Write("backend.Tests/Referencing.cs", referencing.ToString());
        Write("backend.Tests/Unrelated.cs", unrelated.ToString());

        var result = Plan(["backend/FooService.cs"]);

        AssertWholeProject(result, "filter of 400 classes is");
        Assert.Contains("above 16384", Assert.Single(result.Audit.ReferenceSelections).FallbackReason);
    }

    [Fact]
    public void EmptySelection_GetsNoCommandAndNamesTheReason()
    {
        BackendRepository();
        Write("backend/RunnerReleaseIdentity.cs", "namespace AgentStudio;\ninternal static class RunnerReleaseIdentity { }\n");
        Unrelated("BarTests", "BazTests");

        var result = Plan(["backend/RunnerReleaseIdentity.cs"]);

        Assert.DoesNotContain(result.Commands, command => command.Command.StartsWith("dotnet test", StringComparison.Ordinal));
        var unselected = Assert.Single(result.Audit.Candidates, candidate => candidate.Command.Command == WholeProjectCommand);
        Assert.DoesNotContain(unselected.Id, result.Audit.SelectedCandidateIds);
        var audit = Assert.Single(result.Audit.ReferenceSelections);
        Assert.Equal(
            "no test class references the changed types; the batch gate runs the full suite",
            audit.FallbackReason);
        Assert.Contains(result.Audit.Reasons, reason =>
            reason == "backend.Tests/OrchestratorApi.Tests.csproj: no test class references the changed types; "
                + "the batch gate runs the full suite");
    }

    [Fact]
    public void Overlay_SuppliesFilesThatExistOnlyInTheDelivery()
    {
        BackendRepository();
        Unrelated("BarTests", "BazTests");
        var reads = new List<IReadOnlyList<string>>();
        var overlay = new DotNetReferenceSelection
        {
            Overlay = paths =>
            {
                reads.Add(paths);
                return new DotNetSourceOverlay(new Dictionary<string, string?>
                {
                    ["backend/NewService.cs"] = "namespace AgentStudio;\npublic sealed class NewService { }\n",
                    ["backend.Tests/Services/NewServiceTests.cs"] = """
                        namespace AgentStudio.Tests;
                        public sealed class NewServiceTests { [Fact] public void Runs() => _ = new NewService(); }
                        """,
                });
            },
            Time = _clock,
        };

        var result = Plan(["backend/NewService.cs", "backend.Tests/Services/NewServiceTests.cs"], overlay);

        Assert.Equal(["NewServiceTests"], result.Audit.SelectedTestClasses);
        var read = Assert.Single(reads);
        Assert.Equal(["backend.Tests/Services/NewServiceTests.cs", "backend/NewService.cs"], read);
        Assert.Contains("NewService", Assert.Single(result.Audit.ReferenceSelections).SeedTypes);
    }

    [Fact]
    public void OverlayReadFailure_KeepsTheWholeProject()
    {
        FooServiceFixture();

        var thrown = Plan(["backend/Features/Foo/FooService.cs"], new DotNetReferenceSelection
        {
            Overlay = _ => throw new InvalidOperationException("cat-file died"),
            Time = _clock,
        });
        var reported = Plan(["backend/Features/Foo/FooService.cs"],
            Overlay(failure: "output exceeds the cap"));

        AssertWholeProject(thrown, "delivery overlay could not be read: cat-file died");
        AssertWholeProject(reported, "delivery overlay could not be read: output exceeds the cap");
    }

    [Fact]
    public void MixedDiff_SelectsTheUnionOfBothSelections()
    {
        FooServiceFixture();
        Write("backend.Tests/Gate/GateTests.cs", """
            namespace AgentStudio.Tests;
            public sealed class GateTests { [Fact] public void Runs() { } }
            """);
        Write("backend.Tests/Gate/GateSiblingTests.cs", """
            namespace AgentStudio.Tests;
            public sealed class GateSiblingTests { [Fact] public void Runs() { } }
            """);
        Unrelated("FirstTests", "SecondTests", "ThirdTests");

        var result = Plan(["backend.Tests/Gate/GateTests.cs", "backend/Features/Foo/FooService.cs"]);

        Assert.Equal(["FooServiceTests", "GateSiblingTests", "GateTests"], result.Audit.SelectedTestClasses);
    }

    [Fact]
    public void Memo_BuildsTheIndexOncePerTestProject()
    {
        FooServiceFixture();
        var memo = new Dictionary<string, DotNetTestIndex>(StringComparer.Ordinal);
        var builds = 0;
        var selection = new DotNetReferenceSelection
        {
            IndexMemo = (project, build) =>
            {
                if (memo.TryGetValue(project, out var hit)) return hit;
                builds++;
                return memo[project] = build();
            },
            Time = _clock,
        };

        var first = Plan(["backend/Features/Foo/FooService.cs"], selection);
        var second = Plan(["backend/Features/Foo/FooService.cs"], selection);

        Assert.Equal(1, builds);
        Assert.Equal(first.Audit.SelectedTestClasses, second.Audit.SelectedTestClasses);
        Assert.Equal([Project], memo.Keys);
    }

    [Fact]
    public void Audit_RecordsTheSelectionLineAndThePlanBuildTime()
    {
        FooServiceFixture();
        var selection = new DotNetReferenceSelection
        {
            Overlay = paths =>
            {
                _clock.Advance(TimeSpan.FromMilliseconds(250));
                return new DotNetSourceOverlay(paths.ToDictionary(
                    path => path,
                    path => (string?)File.ReadAllText(Path.Combine(_root, path))));
            },
            Time = _clock,
        };

        var result = Plan(["backend/Features/Foo/FooService.cs"], selection);

        var audit = Assert.Single(result.Audit.ReferenceSelections);
        Assert.Equal(
            "work package: 1 of 2 test classes selected (1 files, closure depth 1) "
            + "by reference to 1 changed types in 1 files",
            audit.Summary);
        Assert.Equal(["FooService"], audit.SeedTypes);
        Assert.Equal(["backend/Features/Foo/FooService.cs"], audit.SeedFiles);
        Assert.Equal(2, audit.TotalClasses);
        Assert.Null(audit.FallbackReason);
        Assert.Equal(TimeSpan.FromMilliseconds(250), result.Audit.PlanBuildTime);
        Assert.Contains("reference selection plan built in 250 ms", result.Audit.Reasons);
        Assert.Contains(audit.Summary, result.Audit.Reasons);
        Assert.Contains(result.Audit.Candidates, candidate =>
            candidate.TestClasses.SequenceEqual(["FooServiceTests"])
            && candidate.Reasons.Contains(audit.Summary));
    }

    [Fact]
    public void WithoutTheParameter_TheWholeProjectStillRuns()
    {
        FooServiceFixture();

        var result = TestSelectionPlanner.Plan(
            _root, BackendVerifyPlan(), ["backend/Features/Foo/FooService.cs"], policy: null,
            "remote-review", TestExecutionLevels.WorkPackage);

        Assert.Equal(WholeProjectCommand, DotNetTestCommand(result));
        Assert.Empty(result.Audit.SelectedTestClasses);
        Assert.Empty(result.Audit.ReferenceSelections);
        Assert.Null(result.Audit.PlanBuildTime);
    }

    [Fact]
    public void FullLevel_IgnoresTheParameter()
    {
        FooServiceFixture();

        var result = TestSelectionPlanner.Plan(
            _root, BackendVerifyPlan(), ["backend/Features/Foo/FooService.cs"], policy: null,
            "remote-review", TestExecutionLevels.Full, new DotNetReferenceSelection { Time = _clock });

        Assert.Contains(result.Commands, command =>
            command.Command == "dotnet test backend.Tests/OrchestratorApi.Tests.csproj --no-build --filter Category!=MachineBound");
        Assert.Empty(result.Audit.SelectedTestClasses);
    }

    [Fact]
    public void Scanner_FindsTopLevelTypesAndTestClasses()
    {
        var file = DotNetSourceScanner.Scan("backend.Tests/Sample.cs", """
            namespace AgentStudio.Tests;
            public sealed partial class GateFlakyRerunPolicyTests { }
            internal sealed record Probe(int Value);
            public readonly record struct Point(int X);
            internal interface IClock { }
            public enum Mode { A }
            public sealed class Behavior
            {
                [SkippableFact]
                public void Runs() { var hex = 0x1F; }

                private sealed class Nested { }
            }
            public static class Helpers { }
            """);

        Assert.Equal(
            ["GateFlakyRerunPolicyTests", "Probe", "Point", "IClock", "Mode", "Behavior", "Helpers"],
            file.DeclaredTypes);
        Assert.Equal(["GateFlakyRerunPolicyTests", "Behavior"], file.TestClasses);
        Assert.DoesNotContain("x1F", file.Tokens);
        Assert.Contains("Nested", file.Tokens);
    }

    private StagedVerifyPlan Plan(IReadOnlyList<string> changedFiles, DotNetReferenceSelection? selection = null)
        => TestSelectionPlanner.Plan(
            _root, BackendVerifyPlan(), changedFiles, policy: null,
            "remote-review", TestExecutionLevels.WorkPackage,
            selection ?? new DotNetReferenceSelection { Time = _clock });

    private DotNetReferenceSelection Overlay(
        IReadOnlyDictionary<string, string?>? files = null,
        string? failure = null)
        => new()
        {
            Overlay = _ => new DotNetSourceOverlay(files ?? new Dictionary<string, string?>(), failure),
            Time = _clock,
        };

    private static string DotNetTestCommand(StagedVerifyPlan result)
        => Assert.Single(result.Commands, command =>
            command.Command.StartsWith("dotnet test", StringComparison.Ordinal)).Command;

    private static void AssertWholeProject(StagedVerifyPlan result, string reason)
    {
        Assert.Equal(WholeProjectCommand, DotNetTestCommand(result));
        Assert.Empty(result.Audit.SelectedTestClasses);
        var audit = Assert.Single(result.Audit.ReferenceSelections);
        Assert.NotNull(audit.FallbackReason);
        Assert.StartsWith(reason, audit.FallbackReason);
        Assert.Contains(result.Audit.Reasons, line =>
            line.Contains(reason, StringComparison.Ordinal) && line.Contains(Project, StringComparison.Ordinal));
    }

    private void FooServiceFixture()
    {
        BackendRepository();
        Write("backend/Features/Foo/FooService.cs", """
            namespace AgentStudio.Foo;
            public sealed class FooService
            {
                public sealed record Options(int Limit);
            }
            """);
        Write("backend.Tests/FooServiceTests.cs", """
            namespace AgentStudio.Tests;
            public sealed class FooServiceTests
            {
                [Fact]
                public void Runs() => _ = new FooService();
            }
            """);
        Unrelated("BarTests");
    }

    private void Unrelated(params string[] classes)
    {
        foreach (var name in classes)
        {
            Write($"backend.Tests/{name}.cs",
                $"namespace AgentStudio.Tests;\npublic sealed class {name}\n{{\n    [Fact]\n    public void Runs() {{ }}\n}}\n");
        }
    }

    private void BackendRepository(string extraReference = "")
    {
        Write("backend/OrchestratorApi.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write(Project, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\backend\OrchestratorApi.csproj" />
                {extraReference}
              </ItemGroup>
            </Project>
            """);
    }

    private static VerifyPlan BackendVerifyPlan()
        => new([
            new(VerifyEcosystem.Custom, VerifyCommandKind.Build, "", "dotnet build agent-taskboard.sln --no-restore"),
            new(VerifyEcosystem.Custom, VerifyCommandKind.Test, "",
                "dotnet test backend.Tests/OrchestratorApi.Tests.csproj --no-build --filter Category!=MachineBound"),
        ], VerifyPlan.SourceProjectDefinition);

    private void Write(string relativePath, string contents)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }
}
