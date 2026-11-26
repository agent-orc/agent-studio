using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Personas;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentStudio.Tests;

public sealed class LineGuardianTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "guardian-" + Guid.NewGuid().ToString("N"));
    private const string Project = "PROJ-001";
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
    private static RedTestTriageFacts Facts(string fingerprint = "incident") => new("Test.A", "routine", RedTestCategory.Routine)
    {
        FirstResult = RedTestResult.Fail, SameShaRepeat = RedTestResult.Fail,
        EnvelopeCanary = RedTestResult.Pass, FingerprintMatchesReference = true,
        IncidentFingerprint = fingerprint,
    };
    private static PostMergeRunRecord Run(string id = "run") => new(id, Project, "window", new string('a', 40), null,
        null, null, 1, [], [], "red", Now, Now);
    private sealed class Classifier : ILineGuardianClassifier
    {
        public int Calls;
        public bool Fail;
        public GuardianClassificationCall Classify(string project, GuardianClassificationInput input)
        {
            Calls++;
            if (Fail) throw new IOException("Classification transport unavailable.");
            Assert.True(input.Excerpt.Length <= 2000);
            return new(new("Environment", "External fixture timed out.", .8), 500);
        }
    }
    [Fact]
    public void Shadow_compares_model_with_system_and_live_uses_guardian_after_owner_switch()
    {
        var classifier = new Classifier();
        var store = new LineGuardianStore(_root, classifier);
        var system = RedTestTriagePolicy.Decide(Facts());
        var record = store.Evaluate(Project, Run(), Facts(), system, Now);
        Assert.True(record.Shadow);
        Assert.Equal(RedTestClassification.Environment, record.Guardian.Classification);
        Assert.Equal(RedTestClassification.Ambiguous, record.Applied.Classification);
        Assert.Equal(LineGuardianStore.Principal(Project), record.Log.Principal);
        Assert.True(record.Log.Reason.Length <= 280);
        store.Evaluate(Project, Run(), Facts(), system, Now);
        Assert.Equal(1, classifier.Calls);
        Assert.Equal(500, Assert.Single(store.Read(Project).Models).TokensUsed);
        Assert.Throws<UnauthorizedAccessException>(() => store.SetLive(Project, true, "runner", false, Now));
        store.SetLive(Project, true, "owner", true, Now);
        var live = new LineGuardianStore(_root, classifier).Evaluate(Project, Run("next"), Facts(), system, Now);
        Assert.False(live.Shadow);
        Assert.Equal(live.Guardian, live.Applied);
        Assert.Equal(1, classifier.Calls);
        Assert.False(store.Read("PROJ-002").Live);
        Assert.True(store.Close(Project, "incident"));
        Assert.False(new LineGuardianStore(_root).Close(Project, "incident"));
    }
    [Fact]
    public void Failed_model_call_keeps_rules_running_and_is_not_retried_after_restart()
    {
        var classifier = new Classifier { Fail = true };
        var store = new LineGuardianStore(_root, classifier);
        var facts = Facts();
        var system = RedTestTriagePolicy.Decide(facts);
        var result = store.Evaluate(Project, Run(), facts, system, Now);
        Assert.Contains(result.Guardian.Decisions, a => a.Action == RedTestAction.StartHeld);
        new LineGuardianStore(_root, classifier).Evaluate(Project, Run("next"), facts, system, Now);
        Assert.Equal(1, classifier.Calls);
        Assert.Equal(8_000, Assert.Single(store.Read(Project).Models).ReservedTokens);
    }

    [Fact]
    public void T2_is_an_expiring_use_capped_owner_item_and_forbidden_actions_are_refused()
    {
        var store = new LineGuardianStore(_root);
        var facts = new GuardianOperationFacts("quarantine.hold.start", Facts() with { Category = RedTestCategory.Base }, Now: Now);
        var item = store.Request(Project, "incident", facts)!;
        Assert.Equal("Refuse", item.Recommended);
        Assert.Equal(1, item.UseCap);
        Assert.Equal(Now.AddHours(24), item.ExpiresAt);
        store.Request(Project, "incident", facts);
        Assert.Single(store.Read(Project).OwnerItems);
        Assert.Equal(GuardianPermission.OwnerRequired, LineGuardianPolicy.Authorize(facts));
        Assert.Equal(GuardianPermission.Allowed, LineGuardianPolicy.Authorize(facts with
            { ExplicitOwnerGrant = true, GrantExpires = Now.AddMinutes(1), UsesRemaining = 1 }));
        Assert.Equal(GuardianPermission.OwnerRequired, LineGuardianPolicy.Authorize(facts with
            { ExplicitOwnerGrant = true, GrantExpires = Now, UsesRemaining = 1 }));
        Assert.Throws<InvalidOperationException>(() => store.Request(Project, "incident", facts with { Operation = "tests.edit" }));
    }
    [Fact]
    public void Weekly_tokens_and_receipt_compute_stop_calls_and_deduplicate_owner_items_after_restart()
    {
        var classifier = new Classifier();
        var store = new LineGuardianStore(_root, classifier);
        for (var n = 0; n < 9; n++)
        {
            var facts = Facts("incident-" + n);
            store.Evaluate(Project, Run("run-" + n), facts, RedTestTriagePolicy.Decide(facts), Now);
        }
        Assert.Equal(7, classifier.Calls);
        Assert.Equal(56_000, store.Read(Project).Models.Sum(m => m.ReservedTokens));
        var exhausted = Facts("compute");
        var receipt = Run("compute") with { FocusedRuns = [new("repeat", "sha", 1, 1, false, 90 * 60000)] };
        var decision = new LineGuardianStore(_root, classifier).Evaluate(Project, receipt, exhausted,
            RedTestTriagePolicy.Decide(exhausted), Now);
        Assert.Contains(decision.Guardian.Decisions, a => a.Action == RedTestAction.StartHeld);
        store.Evaluate(Project, receipt, exhausted, decision.Applied, Now);
        Assert.Equal(7, classifier.Calls);
        Assert.Single(store.Read(Project).OwnerItems, i => i.Incident == "Test.A:compute");
        Assert.Single(store.Read(Project).Receipts, r => r.RunId == "compute");
    }
    [Fact]
    public void Running_receipts_cannot_hide_terminal_compute_from_the_weekly_budget()
    {
        var classifier = new Classifier();
        var store = new LineGuardianStore(_root, classifier);
        for (var n = 0; n < 6; n++)
        {
            var run = Run("run-" + n);
            var running = Facts("running-" + n) with { FirstResult = RedTestResult.Unknown, SameShaRepeat = RedTestResult.Unknown };
            store.Evaluate(Project, run with { Status = "running" }, running, RedTestTriagePolicy.Decide(running), Now,
                evaluationId: run.Id + ":running");
            var finished = run with { FocusedRuns = [new("control", "sha", 1, 1, false, 100 * 60000)] };
            var terminal = Facts("terminal-" + n);
            store.Evaluate(Project, finished, terminal, RedTestTriagePolicy.Decide(terminal), Now);
        }
        var fresh = Facts("fresh");
        store.Evaluate(Project, Run("fresh"), fresh, RedTestTriagePolicy.Decide(fresh), Now);
        Assert.Equal(0, classifier.Calls);
        Assert.Single(store.Read(Project).OwnerItems, i => i.Incident == "Test.A:fresh");

        var receipt = Run("same-incident");
        var facts = Facts("same-incident") with { FirstResult = RedTestResult.Unknown };
        store.Evaluate(Project, receipt, facts, RedTestTriagePolicy.Decide(facts), Now, "same:running");
        store.Evaluate(Project, receipt with { FocusedRuns = [new("repeat", "sha", 1, 1, false, 60000)] },
            facts, RedTestTriagePolicy.Decide(facts), Now, "same:terminal");
        Assert.Equal(1, Assert.Single(store.Read(Project).Receipts, r => r.RunId == "same-incident").CoreMinutes);
        store.Evaluate(Project, receipt with { FocusedRuns = [new("repeat", "sha", 1, 1, false, 60000)] },
            facts, RedTestTriagePolicy.Decide(facts), Now.AddDays(7), "same:next-week");
        Assert.Equal(Now, Assert.Single(store.Read(Project).Receipts, r => r.RunId == "same-incident").At);
    }

    [Fact]
    public void Budget_owner_item_is_projected_into_needs_you_with_its_recommendation()
    {
        var classifier = new Classifier();
        var store = new LineGuardianStore(_root, classifier);
        var facts = Facts();
        var receipt = Run() with { FocusedRuns = [new("control", "sha", 1, 1, false, 90 * 60000)] };
        store.Evaluate(Project, receipt, facts, RedTestTriagePolicy.Decide(facts), Now);
        var source = new AgentStudio.Operator.OperatorPostMergeRedIncidentFactSource(() => [],
            guardianStates: () => [(Project, store.Read(Project))]);
        var item = Assert.Single(source.Collect(Now.UtcDateTime));
        Assert.Contains("budget exhausted", item.Title!);
        Assert.True(Assert.Single(item.OwnerOptions!).Recommended);
        Assert.Equal(0, classifier.Calls);
        store.Close(Project, "Test.A:incident");
        Assert.Empty(source.Collect(Now.UtcDateTime));
    }

    [Fact]
    public void Routine_authority_preserves_confirmation_lift_and_review_lane_bounds()
    {
        var f = new GuardianOperationFacts("quarantine.hold.start", Facts(), HasCard: true, Now: Now);
        Assert.Equal(GuardianPermission.Allowed, LineGuardianPolicy.Authorize(f));
        Assert.Equal(GuardianPermission.Refused, LineGuardianPolicy.Authorize(f with
            { Incident = f.Incident with { SameShaRepeat = RedTestResult.Unknown } }));
        Assert.Equal(GuardianPermission.OwnerRequired, LineGuardianPolicy.Authorize(f with
            { Incident = f.Incident with { ProjectQuarantines = [new("a", "a", RedTestQuarantineKind.Held),
                new("b", "b", RedTestQuarantineKind.Held), new("c", "c", RedTestQuarantineKind.Held)] } }));
        Assert.Equal(GuardianPermission.Refused, LineGuardianPolicy.Authorize(f with { Operation = "testing.strategy.change" }));
        Assert.Equal(GuardianPermission.Refused, LineGuardianPolicy.Authorize(f with { Operation = "card.reopen" }));
        Assert.False(LineGuardianPolicy.MayClassify(new(false, 0, 0, 600)));
        Assert.False(LineGuardianPolicy.MayClassify(new(true, 0, 0, 0)));
        Assert.True(LineGuardianPolicy.MayClassify(new(false, 52_000, 89, 599)));
    }

    [Theory]
    [InlineData("{\"class\":\"Environment\",\"evidence\":\"timeout\",\"confidence\":0.8,\"action\":\"lift\"}")]
    [InlineData("{\"class\":\"Environment\",\"confidence\":0.8}")]
    [InlineData("{\"class\":\"17\",\"evidence\":\"timeout\",\"confidence\":2}")]
    public void Schema_rejects_actions_missing_evidence_and_invalid_values(string json)
        => Assert.Throws<JsonException>(() => LineGuardianClassifier.Parse(json));

    [Fact]
    public void Role_and_rights_are_revision_locked()
    {
        Assert.Equal("guardian", LineGuardianRole.Descriptor.GetProperty("kind").GetString());
        Assert.Equal("project", LineGuardianRole.Descriptor.GetProperty("scope").GetString());
        Assert.Equal("shadow", LineGuardianRole.Descriptor.GetProperty("defaultMode").GetString());
        var node = JsonSerializer.SerializeToNode(new { role = LineGuardianRole.Descriptor, grants = LineGuardianRole.Rights })!;
        JsonNode? Sort(JsonNode? value) => value switch
        {
            JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => KeyValuePair.Create(p.Key, Sort(p.Value)))),
            JsonArray array => new JsonArray(array.Select(Sort).ToArray()),
            _ => value?.DeepClone(),
        };
        var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Sort(node)!.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))));
        using var locked = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "backend/Policies/personas/revisions.lock.json")));
        Assert.Equal(locked.RootElement.GetProperty("revisions").GetProperty("line-guardian").GetProperty("2026.11.1").GetString(), digest);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
