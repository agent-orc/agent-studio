using System.Text.Json;
using AgentStudio.Pipeline;
using AgentStudio.TaskServer.Contracts.BatchGate;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: every record carries a fixed timestamp and the state reader receives an explicit now.
/// <summary>
/// AGT-3577: the state route names, per isolation finding, the run that
/// proved it and that run's failed tests, so the UI can say which member the
/// bisection proved red and why.
/// </summary>
public sealed class BatchGateIsolationStateTests : IDisposable
{
    private const string Base = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Candidate = "cccccccccccccccccccccccccccccccccccccccc";
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);
    private static readonly BatchGateScope Scope = new("project", "repo", "develop", "full", "digest", "v1");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "batch-isolation-state-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _evidenceReads = [];
    private readonly BatchGateStore _store;

    public BatchGateIsolationStateTests()
    {
        _store = new BatchGateStore(_root, fact =>
        {
            _evidenceReads.Add(fact.BatchRunId!);
            return [
                new BatchTestFailure("Product.Tests.Checkout", fact.BatchId!, fact.BatchRunId!,
                    fact.MemberKeys ?? [], fact.CompletedAtUtc, "Error Message: expected 3 but was 4", false),
                new BatchTestFailure("Product.Tests.Cart", fact.BatchId!, fact.BatchRunId!,
                    fact.MemberKeys ?? [], fact.CompletedAtUtc, "", false),
            ];
        });
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    [Fact]
    public void Proven_red_finding_carries_the_proving_run_and_its_failed_tests()
    {
        var parent = CloseParent();
        var (child, run, evidence) = RedChild(parent, "task-2");
        _store.RecordIsolationFinding(new BatchGateIsolationFinding("project", parent.BatchId, "task-2",
            "run-1", Base, Base, "proven-red", evidence, Now, child.BatchId, run));
        _store.RecordIsolationFinding(new BatchGateIsolationFinding("project", parent.BatchId, "task-1",
            "run-1", Base, Base, "proven-green", evidence, Now));

        var findings = State().GetProperty("isolationFindings").EnumerateArray().ToArray();

        var red = Assert.Single(findings, item => item.GetProperty("status").GetString() == "proven-red");
        Assert.Equal("task-2", red.GetProperty("taskKey").GetString());
        Assert.Equal(parent.BatchId, red.GetProperty("parentBatchId").GetString());
        Assert.Equal(child.BatchId, red.GetProperty("provingBatchId").GetString());
        Assert.Equal(run, red.GetProperty("provingRunId").GetString());
        Assert.Equal(evidence, red.GetProperty("evidencePath").GetString());
        Assert.True(red.GetProperty("evidenceAvailable").GetBoolean());
        var failures = red.GetProperty("failures").EnumerateArray().ToArray();
        Assert.Equal(["Product.Tests.Checkout", "Product.Tests.Cart"],
            failures.Select(item => item.GetProperty("testName").GetString()));
        Assert.Equal("Error Message: expected 3 but was 4", failures[0].GetProperty("errorExcerpt").GetString());
        var green = Assert.Single(findings, item => item.GetProperty("status").GetString() == "proven-green");
        Assert.Empty(green.GetProperty("failures").EnumerateArray());
        Assert.False(green.GetProperty("evidenceAvailable").GetBoolean());
    }

    [Fact]
    public void Legacy_finding_resolves_its_proving_run_from_the_evidence_path()
    {
        var parent = CloseParent();
        var (child, run, evidence) = RedChild(parent, "task-3");
        _store.RecordIsolationFinding(new BatchGateIsolationFinding("project", parent.BatchId, "task-3",
            "run-1", Base, Base, "proven-red", evidence, Now));

        var red = Assert.Single(State().GetProperty("isolationFindings").EnumerateArray());

        Assert.Equal(child.BatchId, red.GetProperty("provingBatchId").GetString());
        Assert.Equal(run, red.GetProperty("provingRunId").GetString());
        Assert.Equal(2, red.GetProperty("failures").GetArrayLength());
    }

    [Fact]
    public void Polling_the_state_reads_the_proving_evidence_once()
    {
        var parent = CloseParent();
        var (child, run, evidence) = RedChild(parent, "task-2");
        _store.RecordIsolationFinding(new BatchGateIsolationFinding("project", parent.BatchId, "task-2",
            "run-1", Base, Base, "proven-red", evidence, Now, child.BatchId, run));

        State();
        State();

        Assert.Equal([run], _evidenceReads);
    }

    [Fact]
    public void Evidence_route_path_stays_inside_the_batch_store()
    {
        var parent = CloseParent();
        var (child, run, evidence) = RedChild(parent, "task-2");
        _store.RecordIsolationFinding(new BatchGateIsolationFinding("project", parent.BatchId, "task-2",
            "run-1", Base, Base, "proven-red", evidence, Now, child.BatchId, run));
        var outside = Path.Combine(Path.GetTempPath(), "outside-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllText(outside, "{}");
        try
        {
            _store.RecordIsolationFinding(new BatchGateIsolationFinding("project", parent.BatchId, "task-1",
                "run-1", Base, Base, "proven-red", outside, Now));

            Assert.Equal(Path.GetFullPath(evidence), _store.IsolationEvidencePath("project", parent.BatchId, "task-2"));
            Assert.Null(_store.IsolationEvidencePath("project", parent.BatchId, "task-1"));
            Assert.Null(_store.IsolationEvidencePath("project", parent.BatchId, "task-0"));
            Assert.Null(_store.StoredEvidenceFile(Path.Combine(_root, "..", Path.GetFileName(outside))));
        }
        finally { File.Delete(outside); }
    }

    [Fact]
    public void Evidence_route_never_falls_back_to_an_older_finding_of_the_member()
    {
        var parent = CloseParent();
        var (child, run, evidence) = RedChild(parent, "task-2");
        _store.RecordIsolationFinding(new BatchGateIsolationFinding("project", parent.BatchId, "task-2",
            "run-1", Base, Base, "proven-red", evidence, Now, child.BatchId, run));
        _store.RecordIsolationFinding(new BatchGateIsolationFinding("project", parent.BatchId, "task-2",
            "run-2", Base, Base, "proven-green", "", Now.AddMinutes(5)));

        Assert.Null(_store.IsolationEvidencePath("project", parent.BatchId, "task-2"));
    }

    [Fact]
    public void State_names_live_isolation_and_the_interrupted_terminal_time()
    {
        var parent = CloseParent();
        var active = JsonSerializer.SerializeToElement(BatchGateStateSnapshotReader.Read(
            _store, "project", new BatchGateFormationOptions(Enabled: true), now: Now,
            activeIsolationFlights: new HashSet<string> { parent.BatchId }),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var running = Assert.Single(active.GetProperty("batches").EnumerateArray());
        Assert.True(running.GetProperty("isolationActive").GetBoolean());
        Assert.Equal(Now, running.GetProperty("phaseAtUtc").GetDateTimeOffset());

        _store.AppendState(new BatchGateState(parent.BatchId, parent.MembershipDigest,
            BatchPhase.Paused, Candidate, 2, Now.AddMinutes(1), "isolation interrupted"));
        var ended = Assert.Single(State().GetProperty("batches").EnumerateArray());
        Assert.False(ended.GetProperty("isolationActive").GetBoolean());
        Assert.Equal("isolation interrupted", ended.GetProperty("phaseReason").GetString());
        Assert.Equal(Now.AddMinutes(1), ended.GetProperty("phaseAtUtc").GetDateTimeOffset());
    }

    private JsonElement State()
        => JsonSerializer.SerializeToElement(BatchGateStateSnapshotReader.Read(
            _store, "project", new BatchGateFormationOptions(Enabled: true), now: Now),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static BatchGateSubject Subject(string key, int order) => new(
        key, "project", "repo", "develop", "full", "digest", "v1",
        "refs/heads/agent-studio/results/one", Base, "run-1", 1, 1, Base,
        true, true, true, true, false, null, true, Now.AddMinutes(order), order + 1);

    private BatchGateManifest CloseParent()
    {
        var members = Enumerable.Range(0, 4).Select(i => Subject($"task-{i}", i)).ToArray();
        var manifest = new BatchGateManifest("batch-14", Scope, Base,
            BatchGatePolicy.MembershipDigest(Base, Scope, members), Now, members, [],
            members.Select(member => member.TaskKey).ToArray());
        _store.CloseManifest(manifest);
        _store.AppendState(new BatchGateState(manifest.BatchId, manifest.MembershipDigest,
            BatchPhase.Red, Candidate, 1, Now));
        return manifest;
    }

    private (BatchGateManifest Child, string RunId, string Evidence) RedChild(BatchGateManifest parent, string key)
    {
        var members = parent.Members.Where(member => member.TaskKey == key).ToArray();
        var digest = BatchGatePolicy.MembershipDigest(parent.BaseSha, parent.Scope, members);
        var child = parent with
        {
            BatchId = "batch-14-" + key, MembershipDigest = digest, Members = members,
            EligibleKeys = [key], ParentBatchId = parent.BatchId, Diagnostic = true,
        };
        _store.CloseManifest(child);
        _store.AppendState(new BatchGateState(child.BatchId, digest, BatchPhase.Assembled, Candidate, 1, Now));
        var evidence = Path.Combine(_store.BatchDirectory(child.BatchId), "gate-evidence.jsonl");
        File.WriteAllText(evidence, "{}\n");
        var runId = "run-" + key;
        _store.RecordRun(new BatchGateRunRecord(runId, child.BatchId, digest, Base, Candidate,
            "full", "digest", "agent-runner-01", 1, ["dotnet test"], Now, evidence));
        _store.RecordVerdict(new BatchGateRunVerdict(runId, child.BatchId, digest, Candidate,
            "digest", "fail", "DeterministicSuiteRed", evidence, Now));
        _store.AppendState(new BatchGateState(child.BatchId, digest, BatchPhase.Red, Candidate, 1, Now));
        _store.RecordExecution(new BatchGateExecutionFact("project", child.BatchId, runId, Candidate,
            "fail", "agent-runner-01", Now, Now, 0, 0, evidence, MemberKeys: [key]));
        return (child, runId, evidence);
    }
}
