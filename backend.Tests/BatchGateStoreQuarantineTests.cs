using System.Text.Json;
using AgentStudio.Operator;
using AgentStudio.Pipeline;
using AgentStudio.TaskServer.Contracts.BatchGate;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>AGT-3681: an unreadable manifest, state or run record is quarantined per record.</summary>
public sealed class BatchGateStoreQuarantineTests : IDisposable
{
    private const string Base = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 10, 0, 0, TimeSpan.Zero);
    private static readonly BatchGateScope Scope = new("project", "repo", "develop", "full", "digest", "v1");
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "batch-quarantine-test-" + Guid.NewGuid().ToString("N"));
    private readonly BatchGateStore _store;

    public BatchGateStoreQuarantineTests() => _store = new BatchGateStore(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    [Fact]
    public void Truncated_manifest_is_skipped_by_every_listing_and_names_its_batch_and_project()
    {
        var running = Close("run");
        _store.AppendState(new BatchGateState(running.BatchId, running.MembershipDigest,
            BatchPhase.Running, null, 1, Now));
        var damaged = Close("damaged");
        var path = Truncate(Path.Combine(_store.BatchDirectory(damaged.BatchId), "manifest.json"));

        Assert.Equal(running.BatchId, Assert.Single(_store.ListManifests()).BatchId);
        Assert.Equal(running.BatchId, Assert.Single(_store.ListRecoverableManifests()).BatchId);
        Assert.Empty(_store.ListPausedManifests());
        var record = Assert.Single(_store.ListQuarantined());
        Assert.Equal(damaged.BatchId, record.BatchId);
        Assert.Equal("project", record.Project);
        Assert.Equal(BatchGateRecordKinds.Manifest, record.Kind);
        Assert.Equal(path, record.File);
        Assert.False(string.IsNullOrWhiteSpace(record.Error));
        Assert.True(_store.IsQuarantined(damaged.BatchId));
        var error = Assert.Throws<BatchGateRecordUnreadableException>(() => _store.ReadManifest(damaged.BatchId));
        Assert.IsAssignableFrom<JsonException>(error);
    }

    [Fact]
    public void Manifest_that_names_nothing_takes_its_batch_id_from_a_state_row()
    {
        var damaged = Close("garbage");
        var path = Path.Combine(_store.BatchDirectory(damaged.BatchId), "manifest.json");
        File.WriteAllText(path, "\u0000\u0001 not json");

        Assert.Empty(_store.ListManifests());
        var record = Assert.Single(_store.ListQuarantined());
        Assert.Equal(damaged.BatchId, record.BatchId);
        Assert.Null(record.Project);
    }

    [Fact]
    public void Unreadable_newest_state_row_reads_unknown_and_never_an_older_phase()
    {
        var batch = Close("state");
        _store.AppendState(new BatchGateState(batch.BatchId, batch.MembershipDigest,
            BatchPhase.Running, null, 1, Now));
        var other = Close("other");
        _store.AppendState(new BatchGateState(other.BatchId, other.MembershipDigest,
            BatchPhase.Running, null, 1, Now));
        var newest = Path.Combine(_store.BatchDirectory(batch.BatchId), "state", "00000003.json");
        File.WriteAllText(newest, "{\"batchId\":\"" + batch.BatchId + "\",\"phase\":\"Publi");

        var state = _store.LatestState(batch.BatchId);
        Assert.Equal(BatchPhase.Unknown, state.Phase);
        Assert.Equal(BatchGateStore.StateUnreadableReason, state.Reason);
        Assert.Equal(other.BatchId, Assert.Single(_store.ListRecoverableManifests()).BatchId);
        var record = Assert.Single(_store.ListQuarantined());
        Assert.Equal((batch.BatchId, "project", BatchGateRecordKinds.State, newest),
            (record.BatchId, record.Project, record.Kind, record.File));
        Assert.Contains("move the batch folder", record.NextStep);

        // A later state row supersedes the damaged one.
        File.WriteAllText(Path.Combine(_store.BatchDirectory(batch.BatchId), "state", "00000004.json"),
            JsonSerializer.Serialize(new BatchGateState(batch.BatchId, batch.MembershipDigest,
                BatchPhase.Abandoned, null, 2, Now), Web));
        Assert.Equal(BatchPhase.Abandoned, _store.LatestState(batch.BatchId).Phase);
        Assert.Empty(_store.ListQuarantined());
    }

    [Fact]
    public void Unreadable_run_record_hides_only_that_run()
    {
        var batch = Close("runs");
        var runs = Path.Combine(_store.BatchDirectory(batch.BatchId), "runs");
        Directory.CreateDirectory(runs);
        var readable = new BatchGateRunRecord("run-ok", batch.BatchId, batch.MembershipDigest, Base, Base,
            "full", "digest", "host", 1, ["dotnet test"], Now, "evidence.json");
        File.WriteAllText(Path.Combine(runs, "ok.json"), JsonSerializer.Serialize(readable, Web));
        var damaged = Path.Combine(runs, "damaged.json");
        File.WriteAllText(damaged, "{\"batchRunId\":\"run-bad\",\"start");

        Assert.Equal("run-ok", Assert.Single(_store.ListRuns(batch.BatchId)).BatchRunId);
        Assert.Equal(batch.BatchId, Assert.Single(_store.ListManifests()).BatchId);
        var record = Assert.Single(_store.ListQuarantined());
        Assert.Equal((BatchGateRecordKinds.Run, damaged, "project"), (record.Kind, record.File, record.Project));
        Assert.False(_store.IsQuarantined(batch.BatchId));
        Assert.Contains("run file", record.NextStep);
    }

    [Fact]
    public void State_route_lists_readable_batches_and_the_quarantine_for_its_project()
    {
        var running = Close("route");
        _store.AppendState(new BatchGateState(running.BatchId, running.MembershipDigest,
            BatchPhase.Running, null, 1, Now));
        var damaged = Close("route-damaged");
        Truncate(Path.Combine(_store.BatchDirectory(damaged.BatchId), "manifest.json"));
        var stateDamaged = Close("route-state");
        File.WriteAllText(Path.Combine(_store.BatchDirectory(stateDamaged.BatchId), "state", "00000002.json"), "{");

        var state = JsonSerializer.SerializeToElement(BatchGateStateSnapshotReader.Read(
            _store, "project", new BatchGateFormationOptions(Enabled: true)), Web);

        Assert.Equal([running.BatchId], state.GetProperty("batches").EnumerateArray()
            .Select(batch => batch.GetProperty("batchId").GetString()));
        Assert.Equal(new[] { damaged.BatchId, stateDamaged.BatchId }.Order(),
            state.GetProperty("quarantined").EnumerateArray()
                .Select(record => record.GetProperty("batchId").GetString()).Order());
        var other = JsonSerializer.SerializeToElement(BatchGateStateSnapshotReader.Read(
            _store, "other-project", new BatchGateFormationOptions(Enabled: true)), Web);
        Assert.Empty(other.GetProperty("quarantined").EnumerateArray());
    }

    [Fact]
    public void Decision_source_keeps_reporting_red_batches_next_to_the_quarantine()
    {
        var red = Close("red");
        _store.AppendState(new BatchGateState(red.BatchId, red.MembershipDigest, BatchPhase.Red,
            null, 1, Now, "suite red"));
        var damaged = Close("fact-damaged");
        Truncate(Path.Combine(_store.BatchDirectory(damaged.BatchId), "manifest.json"));
        var source = new OperatorBatchGateFactSource(_store);

        var facts = Assert.IsAssignableFrom<IReadOnlyList<OperatorDecisionFact>>(source.Collect(Now.UtcDateTime));

        Assert.Contains(facts, fact => fact.Type == OperatorDecisionTypes.BatchRed);
        var unreadable = Assert.Single(facts, fact => fact.Type == OperatorDecisionTypes.BatchRecordUnreadable);
        Assert.Equal("project", unreadable.Project);
        Assert.Contains(damaged.BatchId, unreadable.Reason);
        // A later sweep sees the same occurrence, so the stream publishes it once.
        var again = source.Collect(Now.UtcDateTime)!;
        Assert.Equal(unreadable.Key, Assert.Single(again, fact => fact.Type == OperatorDecisionTypes.BatchRecordUnreadable).Key);
    }

    private BatchGateManifest Close(string prefix)
    {
        var members = Enumerable.Range(0, 2).Select(i => Subject($"{prefix}-{i}", i)).ToArray();
        var manifest = BatchGatePolicy.Form(members, Scope, Base,
            new BatchGateFormationOptions(Enabled: true), Now.AddMinutes(20)).Manifest!;
        _store.CloseManifest(manifest);
        return manifest;
    }

    private static string Truncate(string path)
    {
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..(bytes.Length / 2)]);
        return path;
    }

    private static BatchGateSubject Subject(string key, int order) => new(
        key, "project", "repo", "develop", "full", "digest", "v1",
        "refs/heads/agent-studio/results/one", Base, "run-1", 1, 1, Base,
        true, true, true, true, false, null, true, Now.AddMinutes(order), order + 1);
}
