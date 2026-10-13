using System.Text.Json;
using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: fixture instants are compared only with other fixture instants.

public sealed class BatchGateCycleEvidenceTests
{
    [Fact]
    public void OneStoreRead_ProjectsBatchQueueHostQueueAndExecution()
    {
        var root = Path.Combine(Path.GetTempPath(), "cycle-batch-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new BatchGateStore(root);
            var batchFolder = store.BatchDirectory("batch-1");
            var reviewDone = new DateTimeOffset(2026, 10, 13, 2, 38, 32, TimeSpan.Zero);
            var runStart = reviewDone.AddMinutes(56);
            var runEnd = runStart.AddMinutes(85);
            var scope = new BatchGateScope("Demo", "repo", "develop", "full", "digest", "v1");
            var subject = new BatchGateSubject("AGT-3095", "Demo", "repo", "develop", "full", "digest", "v1",
                "refs/heads/result", new string('a', 40), "run_1", 1, 1, new string('a', 40),
                true, true, true, true, false, null, true, reviewDone, 1);
            var manifest = new BatchGateManifest("batch-1", scope, new string('a', 40), "membership",
                reviewDone.AddMinutes(10), [subject], [], ["AGT-3095"]);
            var run = new BatchGateRunRecord("run-1", "batch-1", "membership", new string('a', 40),
                new string('b', 40), "full", "digest", "TUFI2", 1, ["build"], runStart, "gate.log");
            var state = new BatchGateState("batch-1", "membership", BatchPhase.Green,
                new string('b', 40), 1, runEnd);
            var execution = new BatchGateExecutionFact("Demo", "batch-1", "run-1", new string('b', 40),
                "Ok", "TUFI2", runStart, runEnd, 3_240_000, 0, "gate.log");
            Write(Path.Combine(batchFolder, "manifest.json"), manifest);
            Write(Path.Combine(batchFolder, "runs", "run-1.json"), run);
            Write(Path.Combine(batchFolder, "state", "00000001.json"), state);
            Write(Path.Combine(root, "executions", "execution-1.json"), execution);

            var loaded = Assert.Single(store.ListManifests());
            Assert.Equal("Demo", loaded.Scope.Project);
            Assert.Equal("AGT-3095", Assert.Single(loaded.Members).TaskKey);
            Assert.Single(store.ListRuns("batch-1"));
            var evidence = BatchGateCycleEvidence.Load(store, "Demo").ForTask("AGT-3095");
            Assert.NotEmpty(evidence);
            Assert.Equal(56 * 60, evidence.Where(s => s.Resource == "gateHost" && s.Start < runStart.UtcDateTime)
                .Sum(s => s.Seconds));
            Assert.Equal(54 * 60, evidence.Where(s => s.Resource == "gateHost" && s.Start == runStart.UtcDateTime)
                .Sum(s => s.Seconds));
            Assert.Equal(31 * 60, evidence.Where(s => s.Step == "batchGate").Sum(s => s.Seconds));
            Assert.All(evidence.Where(s => s.Step == "batchGate"), s => Assert.Equal("TUFI2", s.Executor));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}
