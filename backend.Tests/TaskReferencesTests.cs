using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// F34 cross-references: the pure <see cref="TaskReferenceValidator"/> rules
/// (existence, self-reference, dependsOn-is-a-DAG), the
/// <see cref="TaskReferenceIndex"/> reverse-index, and the on-disk round-trip
/// through <see cref="TaskMutationService.SetTaskReferences"/> →
/// <see cref="TaskScannerService"/>.
/// </summary>
public class TaskReferencesTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _watchPath;
    private const string Project = "demo";

    public TaskReferencesTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "rdo-f34-tests-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", Project);
        Directory.CreateDirectory(_watchPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    // ---- pure validator -------------------------------------------------

    [Fact]
    public void Normalize_Trims_Dedupes_DropsBlanks_PerKind()
    {
        var norm = TaskReferenceValidator.Normalize(new TaskReferences
        {
            DependsOn = new() { " ATP-1 ", "ATP-1", "atp-1", "", "  ", "ATP-2" },
            RelatedTo = new() { "ATP-3" },
            Workbenches = new() { " ATP-W4 ", "atp-w4", "", "ATP-W5" },
        });

        Assert.Equal(new[] { "ATP-1", "ATP-2" }, norm.DependsOn.Select(edge => edge.Key).ToArray());
        Assert.Equal(new[] { "ATP-3" }, norm.RelatedTo.ToArray());
        Assert.Empty(norm.BlockedBy);
        Assert.Empty(norm.Supersedes);
        Assert.Equal(new[] { "ATP-W4", "ATP-W5" }, norm.Workbenches);
    }

    [Fact]
    public void Validate_SelfReference_IsRejected()
    {
        var proposed = new TaskReferences { DependsOn = new() { "ATP-1" } };
        var result = TaskReferenceValidator.Validate(
            "ATP-1", proposed, Keys("ATP-1"), Graph());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == TaskReferenceErrorCode.SelfReference);
    }

    [Fact]
    public void Validate_SelfReference_IsCaseInsensitive()
    {
        var proposed = new TaskReferences { RelatedTo = new() { "atp-1" } };
        var result = TaskReferenceValidator.Validate(
            "ATP-1", proposed, Keys("ATP-1"), Graph());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == TaskReferenceErrorCode.SelfReference);
    }

    [Fact]
    public void Validate_UnknownKey_IsWarningNotError()
    {
        var proposed = new TaskReferences { DependsOn = new() { "ATP-999" } };
        var result = TaskReferenceValidator.Validate(
            "ATP-1", proposed, Keys("ATP-1", "ATP-2"), Graph());

        // AGT-2029: an unknown key no longer blocks the write - the referenced
        // (waits-on) task may be created later. It surfaces as a warning and the
        // write still persists.
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.True(result.HasWarnings);
        var warn = Assert.Single(result.Warnings);
        Assert.Equal(TaskReferenceErrorCode.UnknownKey, warn.Code);
        Assert.Equal("ATP-999", warn.Target);
    }

    [Fact]
    public void Validate_SelfReferenceAndCycle_StayHardErrors()
    {
        // Self-reference is a hard error even though unknown keys are lenient.
        var self = TaskReferenceValidator.Validate(
            "ATP-1", new TaskReferences { DependsOn = new() { "ATP-1" } },
            Keys("ATP-1"), Graph());
        Assert.False(self.IsValid);
        Assert.Contains(self.Errors, e => e.Code == TaskReferenceErrorCode.SelfReference);

        // A dependsOn cycle among existing keys is a hard error (rejected on write).
        var graph = Graph(("ATP-2", new[] { "ATP-1" }));
        var cycle = TaskReferenceValidator.Validate(
            "ATP-1", new TaskReferences { DependsOn = new() { "ATP-2" } },
            Keys("ATP-1", "ATP-2"), graph);
        Assert.False(cycle.IsValid);
        Assert.Contains(cycle.Errors, e => e.Code == TaskReferenceErrorCode.DependsOnCycle);
    }

    [Fact]
    public void Validate_DirectCycle_IsRejected()
    {
        // B already dependsOn A; proposing A dependsOn B closes A→B→A.
        var graph = Graph(("ATP-2", new[] { "ATP-1" }));
        var proposed = new TaskReferences { DependsOn = new() { "ATP-2" } };
        var result = TaskReferenceValidator.Validate(
            "ATP-1", proposed, Keys("ATP-1", "ATP-2"), graph);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == TaskReferenceErrorCode.DependsOnCycle);
    }

    [Fact]
    public void Validate_IndirectCycle_IsRejected()
    {
        // Existing chain B→C→A; proposing A→B closes A→B→C→A.
        var graph = Graph(
            ("ATP-2", new[] { "ATP-3" }),
            ("ATP-3", new[] { "ATP-1" }));
        var proposed = new TaskReferences { DependsOn = new() { "ATP-2" } };
        var result = TaskReferenceValidator.Validate(
            "ATP-1", proposed, Keys("ATP-1", "ATP-2", "ATP-3"), graph);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == TaskReferenceErrorCode.DependsOnCycle);
    }

    [Fact]
    public void Validate_DiamondDag_IsAccepted()
    {
        // A→B, A→C, B→D, C→D — no cycle. Validate A's proposed edges.
        var graph = Graph(
            ("ATP-2", new[] { "ATP-4" }),
            ("ATP-3", new[] { "ATP-4" }));
        var proposed = new TaskReferences { DependsOn = new() { "ATP-2", "ATP-3" } };
        var result = TaskReferenceValidator.Validate(
            "ATP-1", proposed, Keys("ATP-1", "ATP-2", "ATP-3", "ATP-4"), graph);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_CycleRuleAppliesOnlyToDependsOn()
    {
        // A relatedTo B, B relatedTo A is fine — only dependsOn is a DAG.
        var graph = Graph(); // relatedTo edges are not part of the dependsOn graph
        var proposed = new TaskReferences { RelatedTo = new() { "ATP-2" } };
        var result = TaskReferenceValidator.Validate(
            "ATP-1", proposed, Keys("ATP-1", "ATP-2"), graph);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_EmptyReferences_IsValid()
    {
        var result = TaskReferenceValidator.Validate(
            "ATP-1", new TaskReferences(), Keys("ATP-1"), Graph());
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_WorkbenchReference_MustResolveInOwningProject()
    {
        var accepted = TaskReferenceValidator.Validate(
            "ATP-1",
            new TaskReferences { Workbenches = new() { "ATP-W4" } },
            Keys("ATP-1"),
            Graph(),
            Keys("ATP-W4"));
        Assert.True(accepted.IsValid);

        var rejected = TaskReferenceValidator.Validate(
            "ATP-1",
            new TaskReferences { Workbenches = new() { "ATP-W9" } },
            Keys("ATP-1"),
            Graph(),
            Keys("ATP-W4"));
        var error = Assert.Single(rejected.Errors);
        Assert.Equal(TaskReferenceErrorCode.UnknownWorkbenchKey, error.Code);
        Assert.Equal(TaskReferenceKinds.Workbenches, error.Kind);
        Assert.Equal("ATP-W9", error.Target);
    }

    [Fact]
    public void Normalize_DuplicateDependency_PreservesStrongestReleaseGate()
    {
        var norm = TaskReferenceValidator.Normalize(new TaskReferences
        {
            DependsOn =
            [
                new TaskDependencyReference(" ATP-2 "),
                new TaskDependencyReference("atp-2", releaseGate: true),
                new TaskDependencyReference("Atp-2", requiresCompletion: true),
            ],
        });

        var edge = Assert.Single(norm.DependsOn);
        Assert.Equal("ATP-2", edge.Key);
        Assert.True(edge.ReleaseGate);
        Assert.True(edge.RequiresCompletion);
    }

    // ---- reverse index --------------------------------------------------

    [Fact]
    public void Index_Dependents_ReturnsIncomingLinks_AcrossKinds()
    {
        var tasks = new[]
        {
            Task("a", "ATP-1", deps: new[] { "ATP-9" }),
            Task("b", "ATP-2", related: new[] { "ATP-9" }),
            Task("c", "ATP-3"), // references nothing
            Task("z", "ATP-9"),
        };
        var index = TaskReferenceIndex.Build(tasks);

        var dependents = index.Dependents("ATP-9");
        Assert.Equal(2, dependents.Count);
        Assert.Contains(dependents, d => d.SourceJobId == "a" && d.Kind == TaskReferenceKinds.DependsOn);
        Assert.Contains(dependents, d => d.SourceJobId == "b" && d.Kind == TaskReferenceKinds.RelatedTo);
    }

    [Fact]
    public void Index_Dependents_KindFilter_Narrows()
    {
        var tasks = new[]
        {
            Task("a", "ATP-1", deps: new[] { "ATP-9" }),
            Task("b", "ATP-2", related: new[] { "ATP-9" }),
            Task("z", "ATP-9"),
        };
        var index = TaskReferenceIndex.Build(tasks);

        var onlyDeps = index.Dependents("ATP-9", TaskReferenceKinds.DependsOn);
        var link = Assert.Single(onlyDeps);
        Assert.Equal("a", link.SourceJobId);
    }

    [Fact]
    public void Index_Dependents_ResolvesWorkbenchReferences()
    {
        var index = TaskReferenceIndex.Build(new[]
        {
            Task("a", "ATP-1", workbenches: new[] { "ATP-W4" }),
            Task("b", "ATP-2", workbenches: new[] { "ATP-W4", "ATP-W5" }),
        });

        var links = index.Dependents("atp-w4", TaskReferenceKinds.Workbenches);
        Assert.Equal(new[] { "ATP-1", "ATP-2" }, links.Select(link => link.SourceKey).ToArray());
        Assert.All(links, link => Assert.Equal(TaskReferenceKinds.Workbenches, link.Kind));
    }

    [Fact]
    public void Index_KnownKeys_And_DependsOnGraph()
    {
        var tasks = new[]
        {
            Task("a", "ATP-1", deps: new[] { "ATP-2" }),
            Task("b", "ATP-2"),
            Task("k", null), // keyless: not a graph node, but its edges still count
        };
        var index = TaskReferenceIndex.Build(tasks);

        Assert.Contains("ATP-1", index.KnownKeys);
        Assert.Contains("ATP-2", index.KnownKeys);
        Assert.Equal(2, index.KnownKeys.Count);
        Assert.Equal(new[] { "ATP-2" }, index.DependsOnGraph["ATP-1"].ToArray());
    }

    [Fact]
    public void Index_TransitiveWaiters_DeduplicatesDiamonds_StopsAtFulfilledNodes_AndSurvivesCycles()
    {
        var tasks = new[]
        {
            Task("decision", "ATP-1", state: TaskStates.HumanReview),
            Task("direct", "ATP-2", deps: new[] { "ATP-1" }, state: TaskStates.Ready),
            Task("left", "ATP-3", deps: new[] { "ATP-2" }, state: TaskStates.Ready),
            Task("diamond", "ATP-4", deps: new[] { "ATP-2", "ATP-3" }, state: TaskStates.Ready),
            Task("cycle-a", "ATP-5", deps: new[] { "ATP-4", "ATP-6" }, state: TaskStates.Ready),
            Task("cycle-b", "ATP-6", deps: new[] { "ATP-5" }, state: TaskStates.Ready),
            Task("done", "ATP-7", deps: new[] { "ATP-1" }, state: TaskStates.Completed),
            Task("behind-done", "ATP-8", deps: new[] { "ATP-7" }, state: TaskStates.Ready),
        };

        var impact = TaskReferenceIndex.Build(tasks).FindTransitiveWaiters("ATP-1");

        Assert.Equal(5, impact.Count);
        Assert.Equal(new[] { "ATP-2", "ATP-3", "ATP-4", "ATP-5", "ATP-6" }, impact.Keys);

        var visibleImpact = TaskReferenceIndex.Build(tasks).FindTransitiveWaiters(
            "ATP-1",
            new HashSet<string>(new[] { "ATP-4", "ATP-5" }, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(new[] { "ATP-4", "ATP-5" }, visibleImpact.Keys);
    }

    // ---- on-disk round-trip --------------------------------------------

    [Fact]
    public void SetTaskReferences_RoundTrips_Through_Scan()
    {
        var (machine, scanner, mutations) = Build();
        machine.EnsureStateFoldersAndMigrate();

        var (a, b, c) = (CreateJob(mutations, "a"), CreateJob(mutations, "b"), CreateJob(mutations, "c"));
        var infoB = scanner.FindJob("b", _watchPath)!;
        var infoC = scanner.FindJob("c", _watchPath)!;
        Assert.False(string.IsNullOrWhiteSpace(infoB.Key));
        Assert.False(string.IsNullOrWhiteSpace(infoC.Key));

        var ok = mutations.SetTaskReferences("a", new TaskReferences
        {
            DependsOn = new() { infoB.Key!, infoB.Key! }, // duplicate collapses
            RelatedTo = new() { infoC.Key! },
            Workbenches = new() { "DEM-W4" },
        }, _watchPath);
        Assert.True(ok);

        var infoA = scanner.FindJob("a", _watchPath)!;
        Assert.Equal(new[] { infoB.Key }, infoA.References.DependsOn.Select(edge => edge.Key).ToArray());
        Assert.Equal(new[] { infoC.Key }, infoA.References.RelatedTo.ToArray());
        Assert.Empty(infoA.References.BlockedBy);
        Assert.Equal(new[] { "DEM-W4" }, infoA.References.Workbenches);
    }

    [Fact]
    public void ReadReferences_AbsentField_YieldsEmptyNonNull()
    {
        var (machine, scanner, mutations) = Build();
        machine.EnsureStateFoldersAndMigrate();
        CreateJob(mutations, "solo");

        var info = scanner.FindJob("solo", _watchPath)!;
        Assert.NotNull(info.References);
        Assert.True(info.References.IsEmpty);
    }

    [Fact]
    public void SetTaskReferences_WritesCamelCaseSchemaToDisk()
    {
        var (machine, scanner, mutations) = Build();
        machine.EnsureStateFoldersAndMigrate();
        CreateJob(mutations, "a");
        CreateJob(mutations, "b");
        var keyB = scanner.FindJob("b", _watchPath)!.Key!;

        mutations.SetTaskReferences("a", new TaskReferences { DependsOn = new() { keyB } }, _watchPath);

        var jobJsonPath = Path.Combine(scanner.FindJob("a", _watchPath)!.FolderPath, "task.json");
        var disk = File.ReadAllText(jobJsonPath);
        // The documented schema uses camelCase keys; the writer must honour it
        // even though the shared WriteOpts carry no naming policy.
        Assert.Contains("\"dependsOn\"", disk);
        Assert.DoesNotContain("\"DependsOn\"", disk);
    }

    [Fact]
    public void SetTaskReferences_RoundTripsReleaseGateObject_AndLegacyString()
    {
        var (machine, scanner, mutations) = Build();
        machine.EnsureStateFoldersAndMigrate();
        CreateJob(mutations, "a");
        CreateJob(mutations, "b");
        CreateJob(mutations, "c");
        var keyB = scanner.FindJob("b", _watchPath)!.Key!;
        var keyC = scanner.FindJob("c", _watchPath)!.Key!;

        mutations.SetTaskReferences("a", new TaskReferences
        {
            DependsOn =
            [
                new TaskDependencyReference(keyB, releaseGate: true),
                new TaskDependencyReference(keyC, requiresCompletion: true),
            ],
        }, _watchPath);

        var info = scanner.FindJob("a", _watchPath)!;
        Assert.True(info.References.DependsOn.Single(edge => edge.Key == keyB).ReleaseGate);
        Assert.False(info.References.DependsOn.Single(edge => edge.Key == keyC).ReleaseGate);
        Assert.True(info.References.DependsOn.Single(edge => edge.Key == keyC).RequiresCompletion);

        var disk = File.ReadAllText(Path.Combine(info.FolderPath, "task.json"));
        Assert.Contains("\"releaseGate\": true", disk);
        Assert.Contains("\"requiresCompletion\": true", disk);
    }

    // ---- helpers --------------------------------------------------------

    private static IReadOnlySet<string> Keys(params string[] keys) =>
        new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, IReadOnlyCollection<string>> Graph(
        params (string Key, string[] DependsOn)[] edges)
    {
        var dict = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, deps) in edges) dict[key] = deps;
        return dict;
    }

    private static TaskInfo Task(
        string id,
        string? key,
        string[]? deps = null,
        string[]? related = null,
        string[]? workbenches = null,
        string state = TaskStates.Backlog) => new()
    {
        Id = id,
        Key = key,
        Title = id.ToUpperInvariant(),
        State = state,
        WatchPath = "/ws/demo",
        References = new TaskReferences
        {
            DependsOn = (deps ?? Array.Empty<string>()).Select(key => new TaskDependencyReference(key)).ToList(),
            RelatedTo = (related ?? Array.Empty<string>()).ToList(),
            Workbenches = (workbenches ?? Array.Empty<string>()).ToList(),
        },
    };

    private string CreateJob(TaskMutationService mutations, string id) =>
        mutations.CreateJob(new CreateTaskRequest
        {
            Id = id,
            Title = id,
            WatchPath = _watchPath,
            Agent = "claude",
        });

    private (TaskStateMachine machine, TaskScannerService scanner, TaskMutationService mutations) Build()
    {
        var config = BuildConfig();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var machine = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        // Register the project so CreateJob can mint F33 stable keys (the
        // reference targets). Production does this via boot auto-discovery.
        registry.EnsureProjectForStorage(_watchPath, Project, DefaultWorkspace.Id);
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            registry,
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        return (machine, scanner, mutations);
    }

    private IConfiguration BuildConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
            })
            .Build();
}
