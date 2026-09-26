using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

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
            Assert.Equal(delta.DeliverySha, SessionContinuationLedgerStore.PeekDelta(folder)?.DeliverySha);
            var consumed = SessionContinuationLedgerStore.ConsumeDelta(folder);
            Assert.Equal(delta.DeliverySha, consumed?.DeliverySha);
            Assert.Equal(["a.cs"], consumed?.ConflictPaths);
            Assert.Null(SessionContinuationLedgerStore.ConsumeDelta(folder));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Resumed_generation_does_not_inherit_a_cross_generation_token_receipt()
    {
        var entry = new SessionContinuationLedgerEntry(
            "attempt-2", "AGT-1", "codex", "repo", "/work", "branch",
            "refs/heads/result", new string('a', 40), "host|home", "session-1",
            "session-1", "resumed", null, true, 1,
            null, null, null, 60, 4, DateTime.UtcNow);

        Assert.Equal(60, SessionContinuationLedgerStore.WithReceiptFallback(entry, 180).TotalTokens);
        Assert.Null(SessionContinuationLedgerStore.WithReceiptFallback(
            entry with { TotalTokens = null }, 180).TotalTokens);
        Assert.Equal(180, SessionContinuationLedgerStore.WithReceiptFallback(
            entry with { InputSessionId = null, TotalTokens = null }, 180).TotalTokens);
    }
}
