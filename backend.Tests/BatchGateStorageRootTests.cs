using AgentStudio.Pipeline;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentStudio.Tests;

public sealed class BatchGateStorageRootTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "batch-state-restore-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Existing_instance_follows_workspace_identity_and_rebases_evidence_after_relocation()
    {
        var workspace = Path.Combine(_root, "old-workspace");
        var state = Path.Combine(_root, "old-state");
        Directory.CreateDirectory(workspace);
        var legacy = BatchGateStorageRoot.ForInstance(Path.Combine(_root, "old-app"), workspace,
            "batch-gates", state);
        var batch = Path.Combine(legacy, "batch-1");
        Directory.CreateDirectory(batch);
        var evidence = Path.Combine(batch, "gate-evidence.jsonl");
        File.WriteAllText(evidence, "pass\n");
        File.WriteAllText(Path.Combine(batch, "manifest.json"), "{}");
        File.WriteAllText(Path.Combine(batch, "run.json"),
            System.Text.Json.JsonSerializer.Serialize(new { EvidencePath = evidence }));

        BatchGateStorageRoot.Prepare(Path.Combine(_root, "old-app"), workspace,
            new ListLogger(), state);
        Assert.True(File.Exists(Path.Combine(workspace, ".batch-state-installation-id")));
        Assert.False(Directory.Exists(Path.Combine(workspace, ".metadata")));
        var identified = BatchGateStorageRoot.ForInstance(Path.Combine(_root, "old-app"),
            workspace, "batch-gates", state);
        Assert.True(File.Exists(Path.Combine(identified, "batch-1", "manifest.json")));

        var newWorkspace = Path.Combine(_root, "new-workspace");
        var newState = Path.Combine(_root, "new-state");
        CopyDirectory(workspace, newWorkspace);
        CopyDirectory(state, newState);
        BatchGateStorageRoot.Prepare(Path.Combine(_root, "new-app"), newWorkspace,
            new ListLogger(), newState);
        var restored = BatchGateStorageRoot.ForInstance(Path.Combine(_root, "new-app"),
            newWorkspace, "batch-gates", newState);
        var restoredEvidence = Path.Combine(restored, "batch-1", "gate-evidence.jsonl");
        Assert.True(File.Exists(restoredEvidence));
        Assert.Contains(restoredEvidence, File.ReadAllText(Path.Combine(restored, "batch-1", "run.json")));
        Assert.DoesNotContain(evidence, File.ReadAllText(Path.Combine(restored, "batch-1", "run.json")));
    }

    [Fact]
    public void Fresh_instance_records_origin_before_state_is_written_and_rebases_it_after_restore()
    {
        var workspace = Path.Combine(_root, "fresh-workspace");
        var state = Path.Combine(_root, "fresh-state");
        Directory.CreateDirectory(workspace);
        BatchGateStorageRoot.Prepare(Path.Combine(_root, "fresh-app"), workspace,
            new ListLogger(), state);
        var instance = BatchGateStorageRoot.ForInstance(Path.Combine(_root, "fresh-app"),
            workspace, "batch-gates", state);
        var instanceRoot = Path.GetDirectoryName(instance)!;
        Assert.Equal(instanceRoot, File.ReadAllText(Path.Combine(instanceRoot, "batch-state-origin")));

        var batch = Path.Combine(instance, "batch-1");
        Directory.CreateDirectory(batch);
        var evidence = Path.Combine(batch, "gate-evidence.jsonl");
        File.WriteAllText(evidence, "pass\n");
        File.WriteAllText(Path.Combine(batch, "run.json"),
            System.Text.Json.JsonSerializer.Serialize(new { EvidencePath = evidence }));

        var restoredWorkspace = Path.Combine(_root, "restored-workspace");
        var restoredState = Path.Combine(_root, "restored-state");
        CopyDirectory(workspace, restoredWorkspace);
        CopyDirectory(state, restoredState);
        BatchGateStorageRoot.Prepare(Path.Combine(_root, "restored-app"), restoredWorkspace,
            new ListLogger(), restoredState);

        var restored = BatchGateStorageRoot.ForInstance(Path.Combine(_root, "restored-app"),
            restoredWorkspace, "batch-gates", restoredState);
        var restoredEvidence = Path.Combine(restored, "batch-1", "gate-evidence.jsonl");
        var record = File.ReadAllText(Path.Combine(restored, "batch-1", "run.json"));
        Assert.True(File.Exists(restoredEvidence));
        Assert.Contains(restoredEvidence, record);
        Assert.DoesNotContain(evidence, record);
        Assert.Equal(Path.GetDirectoryName(restored),
            File.ReadAllText(Path.Combine(Path.GetDirectoryName(restored)!, "batch-state-origin")));
    }

    [Fact]
    public void Startup_warns_when_another_instance_contains_batch_manifests()
    {
        var workspace = Path.Combine(_root, "workspace");
        var state = Path.Combine(_root, "state");
        Directory.CreateDirectory(workspace);
        BatchGateStorageRoot.Prepare(Path.Combine(_root, "app"), workspace, new ListLogger(), state);
        var unmatched = Path.Combine(state, "agentstudio", "instances", "unmatched", "batch-gates", "old");
        Directory.CreateDirectory(unmatched);
        File.WriteAllText(Path.Combine(unmatched, "manifest.json"), "{}");
        var logger = new ListLogger();
        BatchGateStorageRoot.Prepare(Path.Combine(_root, "app"), workspace, logger, state);
        Assert.Contains(logger.Messages, message => message.Contains("batch-state-unmatched-instance", StringComparison.Ordinal));
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }

    [Fact]
    public void Missing_workspace_keeps_the_path_derived_instance_without_failing_startup()
    {
        var workspace = Path.Combine(_root, "absent-workspace");
        var state = Path.Combine(_root, "state");

        BatchGateStorageRoot.Prepare(Path.Combine(_root, "app"), workspace, new ListLogger(), state);

        Assert.False(Directory.Exists(workspace));
        Assert.False(Directory.Exists(state));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
