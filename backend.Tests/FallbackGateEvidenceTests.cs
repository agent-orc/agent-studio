using System.Text;
using System.Text.Json;
using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

public sealed class FallbackGateEvidenceTests
{
    [Fact]
    public void CompactRecordKeepsQueueReaderFieldsAndBoundsOutput()
    {
        var path = Path.Combine(Path.GetTempPath(), "fallback-evidence-" + Guid.NewGuid().ToString("N"),
            "logs", "batch-fallback-review.json");
        try
        {
            var gate = new BuildTestGateResult(BuildTestGateVerdict.Fail, 1, 1234,
                new string('x', 100_000) + "é", "queue overrun", false, false)
            {
                TestedSha = "tested", ExpectedSha = "expected", GateRunId = "run-1",
                OriginEvidencePath = "/outside-workspace/gate-cache/result.json",
                GateCollisionDetected = true,
                ViolatedBudget = new BuildTestGateBudgetEvidence("gate-queue", 5000, 6000, "admission"),
                Processes = [new BuildTestGateProcessEvidence
                {
                    ExitCode = 1,
                    TestReport = new GateTestReport(new GateTestCounts(1, 0, 1, 0), ["Example.Failed"]),
                    StandardOutput = new string('z', 120_000),
                }],
            };

            FallbackGateEvidence.Write(path, gate);
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            Assert.Equal("tested", root.GetProperty("TestedSha").GetString());
            Assert.Equal("expected", root.GetProperty("ExpectedSha").GetString());
            Assert.Equal("run-1", root.GetProperty("GateRunId").GetString());
            Assert.Equal("/outside-workspace/gate-cache/result.json",
                root.GetProperty("OriginEvidencePath").GetString());
            Assert.Equal("Example.Failed",
                root.GetProperty("RecordedFailedTests")[0].GetString());
            Assert.False(root.GetRawText().Contains(new string('z', 1000), StringComparison.Ordinal));
            Assert.True(Encoding.UTF8.GetByteCount(root.GetProperty("Output").GetString()!) <= 64 * 1024);
            var reread = JsonSerializer.Deserialize<BuildTestGateResult>(File.ReadAllBytes(path));
            Assert.Equal("gate-queue", reread!.ViolatedBudget!.Name);
            Assert.Equal("admission", reread.ViolatedBudget.Phase);
            Assert.Equal(BuildTestGateVerdict.Fail, reread.Verdict);
            Assert.Equal(new[] { "Example.Failed" }, BatchBaseRedPolicy.FailedTests(reread!));
            Assert.True(BatchBaseRedPolicy.Provisional(reread));
            Assert.True(new FileInfo(path).Length < 70 * 1024);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(path)!)!, true);
        }
    }
}
