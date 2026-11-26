using AgentStudio.OrchestrationDecisions;
using AgentStudio.Pipeline;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: every store operation receives an explicit observation; file dates meet only those fixed observations.

public sealed class TestQuarantineStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "quarantine-store-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
    private const string Project = "PROJ-001";

    internal static ProjectTestQuarantine Entry(string id, DateTimeOffset now, string kind = "held")
        => new(id, "Tests." + id, "card-" + id, DateOnly.FromDateTime(now.AddDays(1).UtcDateTime), null,
            "confirmed twice", kind, "system:post-merge", now, now.AddHours(kind == "held" ? 24 : 168),
            "fingerprint-" + id, ["red"], ["backend/Features/Pipeline"]);

    [Fact]
    public void Owner_can_exempt_cards_on_an_existing_hold_without_extending_it()
    {
        var store = new TestQuarantineStore(_root);
        var entry = store.Start(Project, Entry("a", Now), []);
        var intent = entry with { OwnerExemptCardIds = ["approved"], AttributedRevertCardId = "revert",
            StartedBy = "owner", StartedAtUtc = Now.AddHours(1), ExpiresAtUtc = Now.AddDays(2) };
        Assert.Empty(store.Start(Project, intent, [], owner: false).OwnerExemptCardIds);
        var updated = store.Start(Project, intent, [], owner: true);
        Assert.Equal(["approved"], updated.OwnerExemptCardIds);
        Assert.Equal("revert", updated.AttributedRevertCardId);
        Assert.Equal(entry.ExpiresAtUtc, updated.ExpiresAtUtc);
        Assert.Equal("exemptions", store.Read(Project).Audit.Last().Operation);
    }

    [Fact]
    public void Restart_preserves_lifted_entries_and_all_mutations_are_audited()
    {
        var store = new TestQuarantineStore(_root);
        var entry = store.Start(Project, Entry("a", Now), []);
        store.Renew(Project, entry.Id, "system:post-merge", false, TaskStates.Progress, Now.AddHours(24));
        Assert.Throws<InvalidOperationException>(() => store.Renew(Project, entry.Id, "owner", true,
            TaskStates.Progress, Now.AddHours(25)));
        Assert.Throws<InvalidOperationException>(() => store.Lift(Project, entry.Id, "system", false, [], [], Now.AddHours(26)));
        store.Lift(Project, entry.Id, "system", false,
            [new(RedTestResult.Pass, true, true), new(RedTestResult.Pass, true, true)], ["green-1", "green-2"], Now.AddHours(26));
        var restored = new TestQuarantineStore(_root).Read(Project);
        var lifted = Assert.Single(restored.Entries);
        Assert.Equal("system", lifted.LiftedBy);
        Assert.Equal(["green-1", "green-2"], lifted.LiftEvidence);
        Assert.Equal(["start", "renew", "lift"], restored.Audit.Select(row => row.Operation));
        Assert.Empty(store.Active(Project, null, Now.AddHours(27)));
    }

    [Fact]
    public void Caps_are_enforced_under_the_write_lock_and_owner_may_lift_without_evidence()
    {
        var store = new TestQuarantineStore(_root);
        foreach (var id in new[] { "a", "b", "c" }) store.Start(Project, Entry(id, Now), []);
        Assert.Throws<InvalidOperationException>(() => store.Start(Project, Entry("d", Now), []));
        store.Lift(Project, "a", "owner", true, [], [], Now);
        store.Start(Project, Entry("d", Now), []);
        foreach (var id in new[] { "e", "f", "g", "h", "i" })
            store.Start(Project, Entry(id, Now, "report-only"), []);
        Assert.Throws<InvalidOperationException>(() => store.Start(Project, Entry("j", Now, "report-only"), []));
        Assert.Throws<InvalidOperationException>(() => store.Renew(Project, "e", "system", false, TaskStates.Progress, Now));
        Assert.Equal(8, store.Active(Project, null, Now).Count);
    }

    [Fact]
    public void Reclassification_to_report_only_preserves_the_entry_and_audits_the_kind_change()
    {
        var store = new TestQuarantineStore(_root);
        var held = store.Start(Project, Entry("a", Now), []);
        store.Start(Project, Entry("a", Now.AddHours(1), "report-only"), []);
        var state = store.Read(Project);
        Assert.Equal(held.Id, Assert.Single(state.Entries).Id);
        Assert.Equal("report-only", state.Entries[0].Kind);
        Assert.Equal(["start", "change-kind"], state.Audit.Select(row => row.Operation));
    }

    [Fact]
    public void File_entries_remain_owner_report_only_and_expire_after_the_whole_date()
    {
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".agent-studio"));
        File.WriteAllText(Path.Combine(repo, TestQuarantineFile.RelativePath),
            """{"tests":[{"test":"File.Test","card":"fix","expiresOn":"2026-11-26"}]}""");
        var store = new TestQuarantineStore(Path.Combine(_root, "state"));
        store.Start(Project, Entry("a", Now), []);
        var union = store.Active(Project, repo, Now.AddHours(11));
        Assert.Equal(2, union.Count);
        var owner = Assert.Single(union, entry => entry.Source == "repository");
        Assert.Equal("owner", owner.StartedBy);
        Assert.Equal("report-only", owner.Kind);
        Assert.Single(store.Active(Project, repo, Now.AddHours(12)));
        Assert.Empty(store.Active(Project, repo, Now.AddHours(24)));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
