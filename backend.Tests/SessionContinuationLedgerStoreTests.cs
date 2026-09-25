using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace OrchestratorApi.Tests;

public sealed class SessionContinuationLedgerStoreTests
{
    [Fact]
    public void Fenced_generation_is_append_once_and_recovery_delta_is_single_use()
    {
        var folder = Path.Combine(Path.GetTempPath(), "session-ledger-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var entry = new SessionContinuationLedgerEntry(
                "attempt-1", "AGT-1", "codex", "repo", "/work", "branch",
                "refs/heads/result", new string('a', 40), "host|home", null,
                "session-1", "fresh-run", null, false, 0,
                10, 2, 3, 15, 4, DateTime.UtcNow);
            SessionContinuationLedgerStore.Append(folder, entry);
            SessionContinuationLedgerStore.Append(folder, entry);
            Assert.Equal("session-1", SessionContinuationLedgerStore.Latest(folder)?.CapturedSessionId);
            Assert.Single(File.ReadAllLines(Path.Combine(folder, SessionContinuationLedgerStore.LedgerFileName)));

            var delta = new MechanicalRoundDelta(new string('b', 40),
                "refs/heads/result", new string('a', 40), ["a.cs"], "Steer", "Verify");
            SessionContinuationLedgerStore.SaveDelta(folder, delta);
            var consumed = SessionContinuationLedgerStore.ConsumeDelta(folder);
            Assert.Equal(delta.DeliverySha, consumed?.DeliverySha);
            Assert.Equal(["a.cs"], consumed?.ConflictPaths);
            Assert.Null(SessionContinuationLedgerStore.ConsumeDelta(folder));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
