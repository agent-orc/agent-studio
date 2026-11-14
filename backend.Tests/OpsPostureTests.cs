using System.Text.Json;
using AgentStudio.Personas;
using AgentStudio.Tasks;
using AgentStudio.Shared;
using AgentStudio.Watcher;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: replay dates are passed explicitly to the pure posture reader and evaluator.
public sealed class OpsPostureTests
{
    private static readonly PersonaDescriptorV1 Descriptor = new PersonaCatalogue().Find("operations-warden")!.Descriptor;
    private readonly DateTime _now = new(2026, 11, 9, 6, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void November_ninth_replay_keeps_one_backup_finding_across_seven_days_even_when_allowance_is_exhausted()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "backend.Tests", "Fixtures",
            "ops-posture-2026-11-09.json")));
        var root = document.RootElement;
        var at = root.GetProperty("observedAt").GetDateTime();
        var scope = root.GetProperty("scope").GetString()!;
        var source = root.GetProperty("source").GetString()!;
        OpsPostureFactV1 Fact(string id, string key) => new(id, scope, root.GetProperty(key).GetString(), at,
            source, TimeSpan.FromDays(8));
        var published = new[] { Fact("backup.schedule", "timer"), Fact("backup.destination", "destination"),
            Fact("backup.last-good", "archive") };
        var store = new PersonaItemStore((string?)null);
        string? itemId = null;
        for (var day = 0; day < 7; day++)
        {
            var now = _now.AddDays(day);
            var snapshot = OpsPostureReader.Read(now, null, null, published);
            var finding = Assert.Single(OpsWardenEvaluator.Evaluate(snapshot, Descriptor)
                .Where(f => f.Fact.Id.StartsWith("backup.", StringComparison.Ordinal)));
            Assert.Equal(("backup.none-scheduled", 95), (finding.Rule, finding.Floor));
            var item = store.Upsert("main", "warden-one", "operations-warden", "daily-" + day,
                Draft(finding), finding.Floor, now);
            Assert.Equal(97, item.Importance);
            itemId ??= item.Id;
            Assert.Equal(itemId, item.Id);
            Assert.Equal("ops:backup.schedule:installation", item.Fingerprint);
        }
        Assert.Single(store.List("main"));
        Assert.Equal(7, store.List("main")[0].History.Count);
        // A decision request is generated independently of model allowance.
        var decision = OpsWardenDecision.ForMissingSchedule(_now);
        Assert.Equal(("Daily 03:15", "ops:backup.schedule-decision:installation"),
            (decision.RecommendedOption, decision.FingerprintKey));
        Assert.Contains("nothing changes", decision.Body);
        var receipt = new PersonaOccurrenceReceipt("blocked-day", "main", "warden-one",
            "operations-warden", Descriptor.Revision, _now, _now,
            PersonaOccurrenceStates.Waiting, "persona-allowance-exhausted",
            ["backup:backup-not-configured", "backup:backup-missing"], null)
        {
            FactFloors = new Dictionary<string, int>
            {
                ["backup:backup-not-configured"] = 95,
                ["backup:backup-missing"] = 90,
            },
        };
        var fallback = PersonaOccurrenceService.RefusalDrafts(receipt, Descriptor);
        Assert.Single(fallback.Where(row => row.Draft.Kind == PersonaItemKinds.Finding));
        Assert.Equal("ops:backup.schedule:installation", fallback[1].Draft.FingerprintKey);
        Assert.Equal(97, PersonaImportanceService.Score(fallback[1].Draft.Impact,
            fallback[1].Draft.Urgency, fallback[1].Draft.Confidence, fallback[1].Floor));
    }

    [Fact]
    public void Expired_referenced_credential_and_stale_source_get_their_floors()
    {
        var expired = new OpsPostureFactV1("security.credentials", "installation", "expired-referenced", _now,
            "credential-inventory", TimeSpan.FromDays(1));
        var old = new OpsPostureFactV1("security.audit", "installation", "verified", _now.AddDays(-2),
            "audit-verify", TimeSpan.FromDays(1));
        var snapshot = new OpsPostureFactsV1(_now, [expired, old]);
        var findings = OpsWardenEvaluator.Evaluate(snapshot, Descriptor);
        Assert.Equal(95, Assert.Single(findings.Where(f => f.Fact.Id == expired.Id)).Floor);
        var stale = Assert.Single(findings.Where(f => f.Fact.Id == old.Id));
        Assert.Equal(("fact.stale", 70), (stale.Rule, stale.Floor));
    }

    [Fact]
    public void Facts_and_items_contain_states_only_and_fix_targets_open_backup_card()
    {
        var secret = "provider-" + Guid.NewGuid().ToString("N");
        var unsafeFact = new OpsPostureFactV1("backup.schedule", "installation", secret, _now,
            "backup-status", TimeSpan.FromDays(1));
        var snapshot = OpsPostureReader.Read(_now, null, null, [unsafeFact]);
        Assert.Null(snapshot.Facts.Single(f => f.Id == "backup.schedule").Value);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(snapshot));
        var cards = new[] { new TaskInfo { Id = "AGT-3574", Title = "Backup scheduling", State = TaskStates.Ready } };
        Assert.Equal("AGT-3574", OpsWardenEvaluator.OpenBackupCard(cards));
        var proposal = OpsWardenFixProposal.ForOpenBackupCard("AGT-3574");
        Assert.False(proposal.DraftCard);
        Assert.Contains("AGT-3574", proposal.Body);
        Assert.Contains(proposal.Evidence, evidence => evidence.Ref == "card:AGT-3574");
        Assert.Equal("ops:backup.schedule-fix:installation", proposal.FingerprintKey);
    }

    private static PersonaItemDraftV1 Draft(OpsWardenFinding finding) => new(PersonaItemKinds.Finding,
        finding.Title, "Observed missing schedule.", null,
        [new("ops-source:backup-status", true), new("ops-fact:backup.schedule", true)],
        finding.Impact, finding.Urgency, finding.Confidence, OpsWardenEvaluator.Fingerprint(finding.Fact));

    private static string RepoRoot()
    {
        var path = AppContext.BaseDirectory;
        while (path is not null && !File.Exists(Path.Combine(path, "agent-taskboard.sln")))
            path = Directory.GetParent(path)?.FullName;
        return path ?? throw new InvalidOperationException("Repository root not found.");
    }
}
