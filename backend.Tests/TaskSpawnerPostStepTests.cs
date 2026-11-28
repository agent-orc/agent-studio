using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2028 task-spawner post-step: the tolerant relevance-sentinel parser, the
/// per-source dedup ledger, the best-available-model default, and the end-to-end
/// runner that writes a follow-up card into a target project's store the way the
/// file-watcher expects (flat <c>tasks/&lt;bucket&gt;/&lt;KEY&gt;/task.json</c> layout,
/// with a <c>relatedTo</c> back-reference to the source task).
/// </summary>
public class TaskSpawnerPostStepTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _srcPath;
    private readonly string _webPath;

    public TaskSpawnerPostStepTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "rdo-spawner-tests-" + Guid.NewGuid().ToString("N"));
        _srcPath = Path.Combine(_workspace, "projects", "src");
        _webPath = Path.Combine(_workspace, "projects", "web");
        Directory.CreateDirectory(_srcPath);
        Directory.CreateDirectory(_webPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    // ---- relevance sentinel parser -------------------------------------

    [Fact]
    public void Parse_RelevantYes_ExtractsReasonTitleAndPrompt()
    {
        var reply = """
            The change adds a new public endpoint, which the website documents.

            [[TASK_SPAWN: relevant=yes; reason=New public API surface]]

            ### SPAWN_TITLE
            Document the new export endpoint on the website

            ### SPAWN_PROMPT
            The backend added POST /export. Update the website API docs to cover it.
            Acceptance: the docs page lists the endpoint with a request/response example.
            """;

        var d = TaskSpawnerDecisionParser.Parse(reply);

        Assert.True(d.Relevant);
        Assert.True(d.CanSpawn);
        Assert.Equal("New public API surface", d.Reason);
        Assert.Equal("Document the new export endpoint on the website", d.Title);
        Assert.Contains("POST /export", d.Prompt);
    }

    [Fact]
    public void Parse_RelevantNo_IsNotRelevant_AndCannotSpawn()
    {
        var reply = "Internal refactor only.\n[[TASK_SPAWN: relevant=no; reason=internal-only refactor]]";
        var d = TaskSpawnerDecisionParser.Parse(reply);

        Assert.False(d.Relevant);
        Assert.False(d.CanSpawn);
        Assert.Equal("internal-only refactor", d.Reason);
        Assert.Null(d.Prompt);
    }

    [Fact]
    public void Parse_NoSentinel_DefaultsToNotRelevant()
    {
        var d = TaskSpawnerDecisionParser.Parse("I think maybe this could matter but I'm not sure.");
        Assert.False(d.Relevant);
        Assert.False(d.CanSpawn);
    }

    [Fact]
    public void Parse_RelevantButNoPrompt_CannotSpawn()
    {
        var d = TaskSpawnerDecisionParser.Parse("[[TASK_SPAWN: relevant=yes; reason=yes but forgot the body]]");
        Assert.True(d.Relevant);
        Assert.False(d.CanSpawn); // conservative: no generated prompt => nothing to spawn
    }

    [Fact]
    public void Parse_LastSentinelWins_AndStripsCodeFence()
    {
        var reply = """
            ```
            [[TASK_SPAWN: relevant=no; reason=first draft]]
            On reflection it is relevant.
            [[TASK_SPAWN: relevant=yes; reason=final answer]]
            ### SPAWN_TITLE
            Do the thing
            ### SPAWN_PROMPT
            Body here.
            ```
            """;
        var d = TaskSpawnerDecisionParser.Parse(reply);
        Assert.True(d.Relevant);
        Assert.Equal("final answer", d.Reason);
        Assert.Equal("Do the thing", d.Title);
    }

    // ---- dedup ledger --------------------------------------------------

    [Fact]
    public void Ledger_Empty_AllowsSpawn_ThenBlocksAfterAppend()
    {
        var folder = Path.Combine(_workspace, "ledger-a");
        Directory.CreateDirectory(folder);

        Assert.True(SpawnedTaskLedger.CanSpawn(folder, _webPath, maxPerSourceTask: 1));

        SpawnedTaskLedger.Append(folder, new SpawnedTaskRecord
        {
            At = DateTime.UtcNow,
            SourceKey = "SRC-1",
            TargetProject = _webPath,
            TargetKey = "WEB-1",
            TargetJobId = "web-1",
        });

        // Budget of 1 is spent, and the same target is already covered.
        Assert.False(SpawnedTaskLedger.CanSpawn(folder, _webPath, maxPerSourceTask: 1));
        Assert.Single(SpawnedTaskLedger.Read(folder));
    }

    [Fact]
    public void Ledger_SameTarget_AlwaysBlocks_EvenUnderHigherBudget()
    {
        var folder = Path.Combine(_workspace, "ledger-b");
        Directory.CreateDirectory(folder);
        SpawnedTaskLedger.Append(folder, new SpawnedTaskRecord { At = DateTime.UtcNow, TargetProject = _webPath, TargetKey = "WEB-1" });

        // Budget 3 leaves room by count, but a second spawn into the SAME target
        // is still refused (never spawn the same follow-up twice).
        Assert.False(SpawnedTaskLedger.CanSpawn(folder, _webPath, maxPerSourceTask: 3));
        // A different target is still allowed under the remaining budget.
        Assert.True(SpawnedTaskLedger.CanSpawn(folder, _srcPath, maxPerSourceTask: 3));
    }

    // ---- best-available model default ----------------------------------

    [Fact]
    public void ModelSelector_DefaultsToLiveCodexFlagship_AtTopEffort()
    {
        var (model, cli, thinking) = TaskSpawnerModelSelector.Resolve(null, null, null);
        Assert.Equal(ModelMetadataRegistry.DefaultForCli(CliTypes.Codex), model);
        Assert.Equal(CliTypes.Codex, cli);
        Assert.Equal(ModelMetadataRegistry.DefaultThinkingLevelForCli(cli, model), thinking);

        var overridden = TaskSpawnerModelSelector.Resolve("claude-opus-4-7", "claude", "high");
        Assert.Equal(("claude-opus-4-7", "claude", "high"), overridden);
    }

    // ---- end-to-end runner: target-store write path --------------------

    [Fact]
    public async Task Run_Relevant_SpawnsCardInTargetProject_WithSourceReference()
    {
        var (scanner, mutations) = Build();
        var source = CreateSource(scanner, mutations, "web-relevance");

        var runner = SpawnerWithReply(scanner, mutations, RelevantReply());
        var ctx = Context(source, _webPath, TaskStates.Backlog);

        var result = await runner.RunAsync(ctx, CancellationToken.None);

        Assert.Equal(TaskSpawnerVerdict.Spawned, result.Verdict);
        Assert.False(string.IsNullOrWhiteSpace(result.TargetKey));
        Assert.False(string.IsNullOrWhiteSpace(result.TargetJobId));

        // The card landed in the TARGET project, in the flat layout the watcher
        // reads (tasks/<bucket>/<KEY>/task.json), in the configured lane.
        var spawned = scanner.FindJob(result.TargetJobId!, _webPath);
        Assert.NotNull(spawned);
        Assert.Equal("web", spawned!.ProjectName);
        Assert.Equal(TaskStates.Backlog, spawned.State);
        Assert.Equal(CliTypes.Codex, spawned.CliType);
        Assert.Equal(ModelIds.Gpt62Sol, spawned.Model);
        Assert.Equal("medium", spawned.ThinkingLevel);
        var jobJson = Path.Combine(spawned.FolderPath, "task.json");
        Assert.True(File.Exists(jobJson));
        Assert.Contains($"{Path.DirectorySeparatorChar}tasks{Path.DirectorySeparatorChar}", spawned.FolderPath);

        // It references the source task (relatedTo, not dependsOn: spawn creates a
        // reference; the separate dependencies feature turns it into a wait).
        Assert.Contains(source.Key!, spawned.References.RelatedTo);
        Assert.Empty(spawned.References.DependsOn);

        // The generated prompt carries the machine-written source provenance header.
        var prompt = File.ReadAllText(Path.Combine(spawned.FolderPath, "prompt.md"));
        Assert.Contains("Auto-spawned from src " + source.Key, prompt);
        Assert.Contains("POST /export", prompt);

        // The source task's dedup ledger recorded the spawn.
        var ledger = SpawnedTaskLedger.Read(source.FolderPath);
        Assert.Single(ledger);
        Assert.Equal(result.TargetKey, ledger[0].TargetKey);
    }

    [Fact]
    public async Task Run_RelevantToReadyLane_LandsInReady()
    {
        var (scanner, mutations) = Build();
        var source = CreateSource(scanner, mutations, "web-ready");
        var runner = SpawnerWithReply(scanner, mutations, RelevantReply());

        var result = await runner.RunAsync(Context(source, _webPath, TaskStates.Ready), CancellationToken.None);

        Assert.Equal(TaskSpawnerVerdict.Spawned, result.Verdict);
        var spawned = scanner.FindJob(result.TargetJobId!, _webPath);
        Assert.Equal(TaskStates.Ready, spawned!.State);
    }

    [Fact]
    public async Task Run_NotRelevant_DoesNotSpawn()
    {
        var (scanner, mutations) = Build();
        var source = CreateSource(scanner, mutations, "no-spawn");
        var runner = SpawnerWithReply(scanner, mutations,
            "[[TASK_SPAWN: relevant=no; reason=internal refactor, no user-facing effect]]");

        var result = await runner.RunAsync(Context(source, _webPath, TaskStates.Backlog), CancellationToken.None);

        Assert.Equal(TaskSpawnerVerdict.NotRelevant, result.Verdict);
        Assert.Empty(ScanProject(scanner, _webPath));
        Assert.Empty(SpawnedTaskLedger.Read(source.FolderPath));
    }

    [Fact]
    public async Task Run_Twice_IsDeduped_SpawnsOnce()
    {
        var (scanner, mutations) = Build();
        var source = CreateSource(scanner, mutations, "dedup");
        var runner = SpawnerWithReply(scanner, mutations, RelevantReply());
        var ctx = Context(source, _webPath, TaskStates.Backlog);

        var first = await runner.RunAsync(ctx, CancellationToken.None);
        Assert.Equal(TaskSpawnerVerdict.Spawned, first.Verdict);

        // Re-processing the same source (e.g. after a reissue loop) must not
        // spawn a second card - the ledger enforces max 1 per source task.
        var second = await runner.RunAsync(ctx, CancellationToken.None);
        Assert.Equal(TaskSpawnerVerdict.Deduped, second.Verdict);

        Assert.Single(ScanProject(scanner, _webPath));
    }

    [Fact]
    public async Task Run_CliFailure_RecordsError_NoSpawn()
    {
        var (scanner, mutations) = Build();
        var source = CreateSource(scanner, mutations, "cli-fail");
        var runner = NewRunner(scanner, mutations);
        runner.OneShotOverride = (_, _) => Task.FromResult(
            CliOneShotResult.SpawnFailure("boom", DateTime.UtcNow, DateTime.UtcNow));

        var result = await runner.RunAsync(Context(source, _webPath, TaskStates.Backlog), CancellationToken.None);

        Assert.Equal(TaskSpawnerVerdict.Error, result.Verdict);
        Assert.Empty(ScanProject(scanner, _webPath));
    }

    // ---- helpers -------------------------------------------------------

    private static string RelevantReply() => """
        The backend added a new public export endpoint the website should document.

        [[TASK_SPAWN: relevant=yes; reason=new public export endpoint]]

        ### SPAWN_TITLE
        Document the new export endpoint on the website

        ### SPAWN_PROMPT
        The backend added POST /export. Update the website API docs to describe it.
        Acceptance: the docs page lists the endpoint with an example.
        """;

    private static CliOneShotResult Ok(string reply) => new(
        Ok: true, ExitCode: 0, Stdout: reply, Stderr: string.Empty, Duration: TimeSpan.Zero,
        ParsedText: reply, Usage: null, RichUsage: null, Latency: new AgentMessageLatency(), Error: null);

    private TaskSpawnerPostStepRunner SpawnerWithReply(
        TaskScannerService scanner, TaskMutationService mutations, string reply)
    {
        var runner = NewRunner(scanner, mutations);
        runner.OneShotOverride = (_, _) => Task.FromResult(Ok(reply));
        return runner;
    }

    private TaskSpawnerPostStepRunner NewRunner(TaskScannerService scanner, TaskMutationService mutations)
    {
        var prompts = new RuntimePromptService(BuildConfig(), NullLogger<RuntimePromptService>.Instance);
        return new TaskSpawnerPostStepRunner(
            mutations, scanner, prompts, NullLogger<TaskSpawnerPostStepRunner>.Instance);
    }

    private TaskSpawnerRunContext Context(TaskInfo source, string targetProject, string lane) => new()
    {
        Source = source,
        SourceProjectName = "src",
        TargetProject = targetProject,
        SpawnLane = lane,
        MaxPerSourceTask = 1,
        TaskBody = "Add a public export endpoint.",
        StatusSummary = "Result: Success",
        DiffSummary = "+ POST /export",
        ResultsInventory = "No results/ inventory available.",
        Model = TaskSpawnerModelSelector.DefaultModel,
        Cli = TaskSpawnerModelSelector.DefaultCli,
        ThinkingLevel = TaskSpawnerModelSelector.DefaultThinkingLevel,
    };

    private TaskInfo CreateSource(TaskScannerService scanner, TaskMutationService mutations, string id)
    {
        var jobId = mutations.CreateJob(new CreateTaskRequest
        {
            Id = id,
            Title = "Source: " + id,
            WatchPath = _srcPath,
            Agent = "claude",
            PromptMarkdown = "Add a public export endpoint.",
        });
        Assert.False(string.IsNullOrWhiteSpace(jobId));
        var source = scanner.FindJob(jobId!, _srcPath);
        Assert.NotNull(source);
        Assert.False(string.IsNullOrWhiteSpace(source!.Key));
        return source;
    }

    private static IReadOnlyList<TaskInfo> ScanProject(TaskScannerService scanner, string watchPath)
        => scanner.ScanAllJobs()
            .Where(j => WatchPathComparison.PathsEqual(j.WatchPath, watchPath))
            .ToList();

    private (TaskScannerService scanner, TaskMutationService mutations) Build()
    {
        var config = BuildConfig();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        // Register both projects so CreateJob mints stable keys in each.
        registry.EnsureProjectForStorage(_srcPath, "src", DefaultWorkspace.Id);
        registry.EnsureProjectForStorage(_webPath, "web", DefaultWorkspace.Id);
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            registry,
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        return (scanner, mutations);
    }

    private IConfiguration BuildConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["WatchPaths:0:Name"] = "src",
                ["WatchPaths:0:Path"] = _srcPath,
                ["WatchPaths:0:RootPath"] = _srcPath,
                ["WatchPaths:1:Name"] = "web",
                ["WatchPaths:1:Path"] = _webPath,
                ["WatchPaths:1:RootPath"] = _webPath,
            })
            .Build();
}
