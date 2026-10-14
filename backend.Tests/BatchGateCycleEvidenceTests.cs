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
            var exitAt = runStart.AddMinutes(10).UtcDateTime;
            var clipped = BatchGateCycleEvidence.Load(store, "Demo").ForTask("AGT-3095", ledger:
            [new TimelineEvent { Ts = exitAt, Kind = TimelineEventKinds.LaneChanged,
                Details = new() { ["from"] = TaskStates.AutoReview, ["to"] = TaskStates.Progress } }]);
            Assert.All(clipped, segment => Assert.True(segment.End <= exitAt));
            Assert.Equal(66 * 60, clipped.Where(s => s.Resource == "gateHost").Sum(s => s.Seconds));
            Assert.DoesNotContain(clipped, s => s.Step == "batchGate");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(BatchPhase.Red, true)]
    [InlineData(BatchPhase.Abandoned, true)]
    [InlineData(BatchPhase.Paused, false)]
    [InlineData(BatchPhase.Abandoned, false)]
    public void BatchEvidence_EndsAtReviewExit_AndRestartsAtNewReview(
        BatchPhase phase, bool secondBatch)
    {
        var root = Path.Combine(Path.GetTempPath(), "cycle-batch-rework-" + Guid.NewGuid().ToString("N"));
        var t0 = new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
        try
        {
            var store = new BatchGateStore(root);
            AddBatch("old", 0, 10, 20, phase);
            if (secondBatch) AddBatch("new", 60, 70, 80, BatchPhase.Green);
            var events = new[]
            {
                new TimelineEvent { Ts = t0.UtcDateTime, Kind = TimelineEventKinds.PromptCreated,
                    Details = new() { ["targetState"] = TaskStates.AutoReview } },
                Lane(25, TaskStates.AutoReview, TaskStates.Ready),
                Lane(30, TaskStates.Ready, TaskStates.Progress),
                Lane(55, TaskStates.Progress, TaskStates.AutoReview),
                Lane(90, TaskStates.AutoReview, TaskStates.Completed),
            };
            var evidence = BatchGateCycleEvidence.Load(store, "Demo").ForTask(
                "CARD", terminalAt: t0.AddMinutes(90).UtcDateTime, ledger: events);
            Assert.DoesNotContain(evidence, span => span.Start < t0.AddMinutes(60).UtcDateTime
                && span.End > t0.AddMinutes(25).UtcDateTime);
            Assert.Equal((secondBatch ? 20 : 10) * 60,
                evidence.Where(s => s.Resource == "gateHost").Sum(s => s.Seconds));
            var flow = TaskCycleTimeAnalyzer.Analyze(new TaskInfo
            {
                Id = "CARD", Key = "CARD", TaskKey = "CARD", Title = "CARD",
                State = TaskStates.Completed, CreatedAt = t0.UtcDateTime,
                EnteredLaneAt = t0.AddMinutes(90).UtcDateTime,
            }, events, null, gateEvidence: evidence).Row!.Flow!;
            Assert.Equal(5 * 60, flow.Segments.Where(s => s.Resource == "codingSlot").Sum(s => s.Seconds));
            Assert.Equal(25 * 60, flow.Segments.Where(s => s.Step == "coding").Sum(s => s.Seconds));
            Assert.Equal(flow.EpisodeSeconds, flow.Segments.Sum(s => s.Seconds));

            TimelineEvent Lane(int minutes, string from, string to) => new()
            {
                Ts = t0.AddMinutes(minutes).UtcDateTime, Kind = TimelineEventKinds.LaneChanged,
                Details = new() { ["from"] = from, ["to"] = to },
            };
            void AddBatch(string id, int reviewAt, int runAt, int endAt, BatchPhase state)
            {
                var scope = new BatchGateScope("Demo", "repo", "develop", "full", "digest", "v1");
                var subject = new BatchGateSubject("CARD", "Demo", "repo", "develop", "full", "digest", "v1",
                    "refs/heads/result", new string('a', 40), "run_" + id, 1, 1, new string('a', 40),
                    true, true, true, true, false, null, true, t0.AddMinutes(reviewAt), 1);
                var folder = store.BatchDirectory(id);
                Write(Path.Combine(folder, "manifest.json"), new BatchGateManifest(id, scope,
                    new string('a', 40), "membership", t0.AddMinutes(reviewAt + 1), [subject], [], ["CARD"]));
                Write(Path.Combine(folder, "runs", "run.json"), new BatchGateRunRecord("run", id,
                    "membership", new string('a', 40), new string('b', 40), "full", "digest", "host", 1,
                    ["build"], t0.AddMinutes(runAt), "gate.log"));
                Write(Path.Combine(folder, "state", "00000001.json"), new BatchGateState(id,
                    "membership", state, new string('b', 40), 1, t0.AddMinutes(endAt)));
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}
