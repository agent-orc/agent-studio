using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// SetJobModel records an operator model switch as a
/// <c>[taskboard] Model changed from=X to=Y</c> line on the <c>[system]</c>
/// stream of <c>logs/cli-output.log</c>, so the conversation projection can
/// surface a "Model changed" notice in the chat. The marker is scoped to jobs
/// that already have a conversation log (a pre-run config tweak has no chat to
/// annotate) and is only written on a real change.
/// </summary>
public class ModelChangeMarkerTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _watchPath;
    private const string Project = "demo";

    public ModelChangeMarkerTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "rdo-model-marker-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", Project);
        Directory.CreateDirectory(_watchPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    private static string CliOutputLog(string folderPath) =>
        Path.Combine(folderPath, "logs", "cli-output.log");

    private static void SeedConversationLog(string folderPath)
    {
        var logDir = Path.Combine(folderPath, "logs");
        Directory.CreateDirectory(logDir);
        File.WriteAllText(
            Path.Combine(logDir, "cli-output.log"),
            "[10:00:00.000] [system] [taskboard] Started claude CLI (PID 1), model=claude-opus-4-8" + Environment.NewLine +
            "[10:00:01.000] [stdout] Working on it." + Environment.NewLine);
    }

    [Fact]
    public void SetJobModel_OnRealChange_AppendsMarkerToExistingConversationLog()
    {
        var (machine, scanner, mutations) = Build();
        machine.EnsureStateFoldersAndMigrate();

        mutations.CreateJob(new CreateTaskRequest
        {
            Id = "switch",
            Title = "Switch",
            WatchPath = _watchPath,
            Agent = "claude",
            CliType = "claude",
            Model = "claude-opus-4-8",
            TargetState = TaskStates.Ready,
        });
        var info = scanner.FindJob("switch", _watchPath)!;
        SeedConversationLog(info.FolderPath);

        Assert.True(mutations.SetJobModel("switch", "claude-sonnet-5", _watchPath));

        var log = File.ReadAllText(CliOutputLog(info.FolderPath));
        Assert.Contains("[system] [taskboard] Model changed from=claude-opus-4-8 to=claude-sonnet-5", log);
        // The persisted-line prefix must match what the parser consumes.
        Assert.Matches(@"\[\d{2}:\d{2}:\d{2}\.\d{3}\] \[system\] \[taskboard\] Model changed", log);
        // The original conversation is preserved, not overwritten.
        Assert.Contains("Working on it.", log);
    }

    [Fact]
    public void SetJobModel_WithoutExistingLog_WritesNoMarker()
    {
        var (machine, scanner, mutations) = Build();
        machine.EnsureStateFoldersAndMigrate();

        mutations.CreateJob(new CreateTaskRequest
        {
            Id = "prerun",
            Title = "Pre-run",
            WatchPath = _watchPath,
            Agent = "claude",
            CliType = "claude",
            Model = "claude-opus-4-8",
            TargetState = TaskStates.Ready,
        });
        var info = scanner.FindJob("prerun", _watchPath)!;

        Assert.True(mutations.SetJobModel("prerun", "claude-sonnet-5", _watchPath));

        // No conversation to annotate → the marker path creates nothing.
        Assert.False(File.Exists(CliOutputLog(info.FolderPath)));
    }

    [Fact]
    public void SetJobModel_SameModel_WritesNoMarker()
    {
        var (machine, scanner, mutations) = Build();
        machine.EnsureStateFoldersAndMigrate();

        mutations.CreateJob(new CreateTaskRequest
        {
            Id = "noop",
            Title = "No-op",
            WatchPath = _watchPath,
            Agent = "claude",
            CliType = "claude",
            Model = "claude-opus-4-8",
            TargetState = TaskStates.Ready,
        });
        var info = scanner.FindJob("noop", _watchPath)!;
        SeedConversationLog(info.FolderPath);
        var before = File.ReadAllText(CliOutputLog(info.FolderPath));

        Assert.True(mutations.SetJobModel("noop", "claude-opus-4-8", _watchPath));

        var after = File.ReadAllText(CliOutputLog(info.FolderPath));
        Assert.Equal(before, after);
        Assert.DoesNotContain("Model changed", after);
    }

    [Fact]
    public void SetJobModel_AliasToCanonicalSameModel_WritesNoMarker()
    {
        var (machine, scanner, mutations) = Build();
        machine.EnsureStateFoldersAndMigrate();

        // The stored model uses the registered alias (dot) spelling; the
        // operator picks the canonical (dash) id for the SAME model.
        mutations.CreateJob(new CreateTaskRequest
        {
            Id = "alias",
            Title = "Alias",
            WatchPath = _watchPath,
            Agent = "claude",
            CliType = "claude",
            Model = "claude-opus-4.8",
            TargetState = TaskStates.Ready,
        });
        var info = scanner.FindJob("alias", _watchPath)!;
        Assert.Equal("claude-opus-4.8", info.Model); // alias persisted verbatim
        SeedConversationLog(info.FolderPath);
        var before = File.ReadAllText(CliOutputLog(info.FolderPath));

        Assert.True(mutations.SetJobModel("alias", "claude-opus-4-8", _watchPath));

        var after = File.ReadAllText(CliOutputLog(info.FolderPath));
        Assert.Equal(before, after);
        Assert.DoesNotContain("Model changed", after);
    }

    [Fact]
    public void SetJobModel_ToCliDefault_RecordsDefaultOnTheChangedSide()
    {
        var (machine, scanner, mutations) = Build();
        machine.EnsureStateFoldersAndMigrate();

        mutations.CreateJob(new CreateTaskRequest
        {
            Id = "to-default",
            Title = "To default",
            WatchPath = _watchPath,
            Agent = "claude",
            CliType = "claude",
            Model = "claude-sonnet-5",
            TargetState = TaskStates.Ready,
        });
        var info = scanner.FindJob("to-default", _watchPath)!;
        SeedConversationLog(info.FolderPath);

        // Passing null selects the CLI default; the normalized default differs
        // from sonnet-5, so a marker is written with the resolved ids.
        Assert.True(mutations.SetJobModel("to-default", null, _watchPath));

        var log = File.ReadAllText(CliOutputLog(info.FolderPath));
        Assert.Contains("[taskboard] Model changed from=claude-sonnet-5 to=", log);
        Assert.DoesNotContain("to=default", log); // claude has a concrete default id
    }

    [Fact]
    public void MigrateNonExplicitModel_Updates_model_without_creating_a_pin()
    {
        var (machine, scanner, mutations) = Build();
        machine.EnsureStateFoldersAndMigrate();
        mutations.CreateJob(new CreateTaskRequest
        {
            Id = "auto-migrate",
            Title = "Auto migrate",
            WatchPath = _watchPath,
            Agent = "claude",
            CliType = "claude",
            Model = "claude-opus-4-8",
            ModelExplicit = false,
            TargetState = TaskStates.Ready,
        });

        var migrated = mutations.MigrateNonExplicitModel(
            "auto-migrate",
            _watchPath,
            "claude-opus-4-8",
            "claude-opus-5");

        Assert.NotNull(migrated);
        Assert.Equal("claude-opus-5", migrated.Model);
        Assert.False(migrated.ModelExplicit);
        var persisted = scanner.FindJob("auto-migrate", _watchPath)!;
        Assert.Equal("claude-opus-5", persisted.Model);
        Assert.False(persisted.ModelExplicit);
    }

    [Fact]
    public void MigrateNonExplicitModel_Refuses_an_explicit_pin()
    {
        var (machine, scanner, mutations) = Build();
        machine.EnsureStateFoldersAndMigrate();
        mutations.CreateJob(new CreateTaskRequest
        {
            Id = "pinned",
            Title = "Pinned",
            WatchPath = _watchPath,
            Agent = "claude",
            CliType = "claude",
            Model = "claude-opus-4-8",
            ModelExplicit = true,
            TargetState = TaskStates.Ready,
        });

        var migrated = mutations.MigrateNonExplicitModel(
            "pinned",
            _watchPath,
            "claude-opus-4-8",
            "claude-opus-5");

        Assert.Null(migrated);
        var persisted = scanner.FindJob("pinned", _watchPath)!;
        Assert.Equal("claude-opus-4-8", persisted.Model);
        Assert.True(persisted.ModelExplicit);
    }

    [Fact]
    public void MigrateNonExplicitModel_Rolls_back_when_the_required_audit_cannot_be_written()
    {
        var (machine, scanner, mutations) = Build();
        machine.EnsureStateFoldersAndMigrate();
        mutations.CreateJob(new CreateTaskRequest
        {
            Id = "audit-failure",
            Title = "Audit failure",
            WatchPath = _watchPath,
            Agent = "claude",
            CliType = "claude",
            Model = "claude-opus-4-8",
            ModelExplicit = false,
            ThinkingLevel = "high",
            TargetState = TaskStates.Ready,
        });

        var migrated = mutations.MigrateNonExplicitModel(
            "audit-failure",
            _watchPath,
            "claude-opus-4-8",
            "claude-opus-5",
            writeAudit: _ => false);

        Assert.Null(migrated);
        var persisted = scanner.FindJob("audit-failure", _watchPath)!;
        Assert.Equal("claude-opus-4-8", persisted.Model);
        Assert.False(persisted.ModelExplicit);
        Assert.Equal("high", persisted.ThinkingLevel);
    }

    private (TaskStateMachine machine, TaskScannerService scanner, TaskMutationService mutations) Build()
    {
        var config = BuildConfig();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var machine = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
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
