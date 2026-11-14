using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

// clock-independent: fixed dates are passed directly to the pure retention eligibility policy.
public sealed class RunnerWorktreeRetentionTests
{
    [Fact]
    public void Referenced_or_unsettled_worktree_is_kept()
    {
        var root = Path.Combine(Path.GetTempPath(), "worktree-retention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var now = new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);
            var old = new SalvageCardFacts("AGT-1", SalvageCardLifecycle.Terminal, now.AddDays(-20));
            Assert.False(RunnerWorktreeRetention.Eligible(root, old, now, ["AGT-1"], 14));
            Assert.False(RunnerWorktreeRetention.Eligible(root,
                new SalvageCardFacts("AGT-1", SalvageCardLifecycle.Open, null), now, [], 14));
            File.WriteAllText(root + ".retained.json", "retained");
            Assert.False(RunnerWorktreeRetention.Eligible(root, old, now, [], 14));
            File.Delete(root + ".retained.json");
            Assert.True(RunnerWorktreeRetention.Eligible(root, old, now, [], 14));
        }
        finally
        {
            File.Delete(root + ".retained.json");
            Directory.Delete(root, recursive: true);
        }
    }
}
