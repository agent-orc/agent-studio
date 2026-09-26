using AgentStudio.Areas;
using AgentStudio.Tags;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class TagMaintenanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tag-maintenance-tests-" + Guid.NewGuid().ToString("N"));
    private static TagRegistryEntry Tag(string id) => new() { Id = id, Label = id, Kind = "facet" };
    private static TagMaintenanceSnapshot Snapshot() => new([Tag("old"), Tag("new"), Tag("unused")],
        ["execution-and-runner"], new() { ["execution-and-runner"] = [] },
        [new("Project", "card", "one", "One", ["old", "new"], "term one", true),
         new("Project", "dossier", "two", "Two", ["old"], "decision", true, true),
         new("Project", "wiki", "three.md", "Three", ["old"], "ADR", true)]);
    private static TagMaintenanceProposal Merge() => new()
    {
        Kind = "merge", Source = "old", Target = "new", Area = "execution-and-runner",
        Reason = "These handles are synonyms.", Evidence = ["card:one"],
    };
    private TagMaintenanceService Service(FakeWorkspace workspace, FakeSynthesis? synthesis = null,
        TimeProvider? clock = null, Dictionary<string, string?>? settings = null)
    {
        settings ??= [];
        settings["TaskRepository"] = _root;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new(workspace, synthesis ?? new(), new TagGoldenSetEvaluator(new FakeClassifier(), configuration),
            configuration, clock);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public void MergePlansAllSubjectKindsAndRemovesRegistryLast()
    {
        var plan = TagMaintenancePolicy.Plan("Project", Snapshot(), Merge());
        Assert.Equal(new[] { "card", "dossier", "wiki", "registry" }, plan.Select(c => c.Kind));
        Assert.Equal("[\"new\"]", plan[0].After);
        Assert.Equal("null", plan[^1].After);
    }

    [Theory]
    [InlineData("old")]
    [InlineData("execution-and-runner")]
    [InlineData("missing")]
    public void RetirementRejectsUsedProtectedOrMissingTags(string source)
    {
        Assert.Throws<ArgumentException>(() => TagMaintenancePolicy.Plan("Project", Snapshot(), Merge() with
        { Kind = "retire", Source = source }));
    }

    [Fact]
    public void RetirementAllowsOnlyGloballyUnusedTag()
    {
        var plan = TagMaintenancePolicy.Plan("Project", Snapshot(), Merge() with { Kind = "retire", Source = "unused" });
        Assert.Single(plan);
        var snapshot = Snapshot();
        snapshot.Items.Add(new("Other", "card", "archived", "Old", ["unused"], "", false));
        Assert.Throws<ArgumentException>(() => TagMaintenancePolicy.Plan("Project", snapshot,
            Merge() with { Kind = "retire", Source = "unused" }));
    }

    [Fact]
    public void MergeRefusesOtherProjectsAndProjectAreas()
    {
        var snapshot = Snapshot();
        snapshot.Items.Add(new("Other", "wiki", "old.md", "Old", ["old"], "", true));
        Assert.Throws<ArgumentException>(() => TagMaintenancePolicy.Plan("Project", snapshot, Merge()));
        snapshot = Snapshot(); snapshot.AreaIds.Add("old");
        Assert.Throws<ArgumentException>(() => TagMaintenancePolicy.Plan("Project", snapshot, Merge()));
    }

    [Fact]
    public void AddAndGlossaryPlanExactRegistryAndTermChanges()
    {
        var proposal = Merge() with { Kind = "add", Target = "fresh", Label = "Fresh", Evidence = ["card:one", "dossier:two", "wiki:three.md"],
            Terms = [new() { Term = "Fresh", Definition = "A new term." }] };
        var plan = TagMaintenancePolicy.Plan("Project", Snapshot(), proposal);
        Assert.Equal(new[] { "registry", "glossary" }, plan.Select(c => c.Kind));
        Assert.Equal("null", plan[0].Before);
        Assert.Contains("A new term.", plan[1].After);
        Assert.Single(TagMaintenancePolicy.Plan("Project", Snapshot(), proposal with { Kind = "glossary" }));
    }

    [Theory]
    [InlineData("---\ntags: [old, new]\ntitle: Hello\n---\nBody", "---\ntags: [new]\ntitle: Hello\n---\nBody")]
    [InlineData("---\r\ntags:\r\n  - old\r\n  - new\r\ntitle: Hello\r\n---\r\nBody", "---\r\ntags: [new]\r\ntitle: Hello\r\n---\r\nBody")]
    public void WikiRewritePreservesAllOtherBytes(string before, string after) =>
        Assert.Equal(after, TagMaintenanceWorkspace.RewriteFrontmatter(before, ["new"]));

    [Fact]
    public async Task ReviewCreatesProseCardAndReportWithoutApplyingThenHonorsCadence()
    {
        var workspace = new FakeWorkspace(); var service = Service(workspace);
        var run = await service.RunAsync("Project");
        Assert.Equal("reported", run!.Status);
        Assert.Single(workspace.Cards);
        Assert.Empty(workspace.Writes);
        Assert.Contains("Keep", workspace.Cards[0]);
        Assert.Contains("Apply", workspace.Cards[0]);
        var reportJson = File.ReadAllText(Path.Combine(_root, "tag-maintenance",
            TagMaintenancePolicy.Fingerprint("Project") + ".json"));
        Assert.Contains("\"metrics\":\"unavailable (no approved golden set)\"", reportJson);
        Assert.DoesNotContain("\"precision\":", reportJson);
        Assert.Null(await Service(workspace).RunAsync("Project"));
        await service.RunAsync("Project", true);
        Assert.Single(workspace.Cards);
        Assert.Equal(2, service.Read("Project").Runs.Count);
    }

    [Fact]
    public async Task KeepIsDurableAndNeverWrites()
    {
        var workspace = new FakeWorkspace(); var service = Service(workspace);
        var run = await service.RunAsync("Project");
        var decision = await service.DecideAsync("Project", run!.Decisions.Single(), "keep", "operator");
        Assert.Equal("rejected", decision.Status);
        Assert.Empty(workspace.Writes);
        Assert.Equal("operator", Service(workspace).Read("Project").Audit.Single().Actor);
    }

    [Fact]
    public async Task ApplyRequiresExplicitChoiceAndActorAndIsIdempotent()
    {
        var workspace = new FakeWorkspace(); var service = Service(workspace);
        var run = await service.RunAsync("Project"); var id = run!.Decisions.Single();
        await Assert.ThrowsAsync<ArgumentException>(() => service.DecideAsync("Project", id, "yes", "operator"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.DecideAsync("Project", id, "apply", ""));
        var result = await service.DecideAsync("Project", id, "apply", "operator");
        Assert.Equal("applied", result.Status);
        Assert.Equal(4, workspace.Writes.Count);
        await Service(workspace).DecideAsync("Project", id, "apply", "operator");
        Assert.Equal(4, workspace.Writes.Count);
        Assert.Equal("approved", service.Read("Project").Audit.First().Outcome);
    }

    [Fact]
    public async Task StalePreimageAndNewReferencesBlockBeforeAnyWrite()
    {
        var workspace = new FakeWorkspace(); var service = Service(workspace);
        var run = await service.RunAsync("Project"); var id = run!.Decisions.Single();
        workspace.Values["card/one"] = "[\"unrelated\"]";
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DecideAsync("Project", id, "apply", "operator"));
        Assert.Empty(workspace.Writes);
        workspace.Values.Clear();
        workspace.Snapshot.Items.Add(new("Project", "wiki", "new.md", "New", ["old"], "", true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DecideAsync("Project", id, "apply", "operator"));
        Assert.Empty(workspace.Writes);
    }

    [Fact]
    public async Task PartialFailureIsReportedAndApprovedRetryResumesAfterRestart()
    {
        var workspace = new FakeWorkspace { FailOnce = "dossier/two" }; var service = Service(workspace);
        var run = await service.RunAsync("Project"); var id = run!.Decisions.Single();
        var partial = await service.DecideAsync("Project", id, "apply", "operator");
        Assert.Equal("partial", partial.Status);
        Assert.Single(workspace.Writes);
        Assert.DoesNotContain("registry/old", workspace.Writes);
        Assert.Equal(["card/one"], service.Read("Project").Audit
            .Where(entry => entry.Outcome == "written").Select(entry => entry.Detail));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DecideAsync("Project", id, "keep", "operator"));
        var result = await Service(workspace).DecideAsync("Project", id, "apply", "operator");
        Assert.Equal("applied", result.Status);
        Assert.Equal(4, workspace.Writes.Count);
        var audit = service.Read("Project").Audit;
        Assert.Contains(audit, a => a.Outcome == "partial");
        Assert.Equal(new[] { "card/one", "dossier/two", "wiki/three.md", "registry/old" },
            audit.Where(entry => entry.Outcome == "written").Select(entry => entry.Detail));
    }

    [Fact]
    public async Task SuccessfulMutationIsAuditedWhenVerificationReadFailsAndRetryIsIdempotent()
    {
        var workspace = new FakeWorkspace { ThrowReadAfterWriteOnce = "card/one" };
        var service = Service(workspace);
        var run = await service.RunAsync("Project");
        var id = run!.Decisions.Single();

        var partial = await service.DecideAsync("Project", id, "apply", "operator");

        Assert.Equal("partial", partial.Status);
        Assert.Contains("mutation applied, verification failed", partial.Error);
        Assert.Equal(["card/one"], workspace.Writes);
        var partialAudit = service.Read("Project").Audit;
        Assert.Single(partialAudit, entry => entry.Outcome == "written" && entry.Detail == "card/one");
        Assert.Contains(partialAudit, entry => entry.Outcome == "verification-failed"
            && entry.Detail.Contains("card/one: mutation applied, verification failed: Simulated verification read failure"));

        var applied = await Service(workspace).DecideAsync("Project", id, "apply", "operator");

        Assert.Equal("applied", applied.Status);
        Assert.Equal(4, workspace.Writes.Count);
        Assert.Single(workspace.Writes, write => write == "card/one");
        var finalAudit = service.Read("Project").Audit;
        Assert.Equal(new[] { "card/one", "dossier/two", "wiki/three.md", "registry/old" },
            finalAudit.Where(entry => entry.Outcome == "written").Select(entry => entry.Detail));
    }

    [Fact]
    public async Task InvalidSynthesisAndUnavailableModelLeaveFailedReportAndNoCards()
    {
        var workspace = new FakeWorkspace();
        var service = Service(workspace, new() { Fail = true });
        var run = await service.RunAsync("Project");
        Assert.Equal("failed", run!.Status);
        Assert.Empty(workspace.Cards);
        Assert.Empty(workspace.Writes);
        Assert.Contains("unavailable", service.Read("Project").Runs.Single().Error);
        service = Service(workspace, new() { Proposal = Merge() with { Kind = "unknown" } });
        Assert.Equal("failed", (await service.RunAsync("Project", true))!.Status);
        Assert.Empty(workspace.Cards);
    }

    [Fact]
    public async Task FailedRunRetriesAfterRetryDelayInsteadOfCadence()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero));
        var synthesis = new FakeSynthesis { Fail = true };
        var service = Service(new(), synthesis, clock, new() { ["TagMaintenance:RetryDelayMinutes"] = "30" });
        Assert.Equal("failed", (await service.RunAsync("Project"))!.Status);
        synthesis.Fail = false;
        clock.Advance(TimeSpan.FromMinutes(29));
        Assert.Null(await service.RunAsync("Project"));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal("reported", (await service.RunAsync("Project"))!.Status);
    }

    [Fact]
    public async Task CancelledRunLeavesNoRecordAndDoesNotChangeDueTime()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero));
        var synthesis = new FakeSynthesis { Cancel = true };
        var service = Service(new(), synthesis, clock);
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.RunAsync("Project"));
        Assert.Empty(service.Read("Project").Runs);
        synthesis.Cancel = false;
        Assert.Equal("reported", (await service.RunAsync("Project"))!.Status);
    }

    [Fact]
    public async Task SuccessfulRunUsesCadence()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero));
        var service = Service(new(), clock: clock);
        Assert.Equal("reported", (await service.RunAsync("Project"))!.Status);
        clock.Advance(TimeSpan.FromDays(7) - TimeSpan.FromSeconds(1));
        Assert.Null(await service.RunAsync("Project"));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.NotNull(await service.RunAsync("Project"));
    }

    [Fact]
    public async Task GoldenSetReportsBothTiersAndFallsBackWhenTierOnePrecisionIsLow()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "golden.json");
        var items = Enumerable.Range(0, 80).Select(index => new TagGoldenSetItem
        {
            Kind = index < 60 ? "card" : "dossier", Id = index.ToString(),
            Title = "Synthetic fixture", Text = "Synthetic fixture", Tags = ["new"],
        }).ToList();
        File.WriteAllText(path, TagMaintenancePolicy.Encode(new TagGoldenSet
        {
            ApprovedBy = "test-operator", ApprovedAt = DateTimeOffset.Parse("2026-09-18T00:00:00Z"), Items = items,
        }));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root, ["TagMaintenance:GoldenSetPath"] = path,
        }).Build();
        var classifier = new FakeClassifier { LowTags = ["old"], HighTags = ["new"] };
        var report = await new TagGoldenSetEvaluator(classifier, configuration)
            .EvaluateAsync("Project", Snapshot(), CancellationToken.None);
        Assert.Equal("evaluated", report.Status);
        Assert.Equal("available", report.Metrics);
        Assert.Equal(2, report.SelectedTier);
        Assert.Equal(2, report.Tiers.Count);
        Assert.Equal(0, report.Tiers[0].Precision);
        Assert.Equal(1, report.Tiers[1].Precision);
        Assert.Equal(new[] { "low", "high" }, classifier.Calls);
    }

    [Fact]
    public async Task MissingGoldenSetClaimsNoMetrics()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["TaskRepository"] = _root }).Build();
        var report = await new TagGoldenSetEvaluator(new FakeClassifier(), configuration)
            .EvaluateAsync("Project", Snapshot(), CancellationToken.None);
        Assert.Equal("not-available", report.Status);
        Assert.Equal("unavailable (no approved golden set)", report.Metrics);
        Assert.Empty(report.Tiers);
        Assert.Contains("no precision or recall is claimed", report.Message);
    }

    [Fact]
    public void EvidenceThresholdAndTerminologyProvenanceAreEnforced()
    {
        Assert.Throws<ArgumentException>(() => TagMaintenancePolicy.Plan("Project", Snapshot(),
            Merge() with { Kind = "add", Target = "fresh", Label = "Fresh" }));
        Assert.Throws<ArgumentException>(() => TagMaintenancePolicy.Plan("Project", Snapshot(),
            Merge() with { Kind = "glossary", Terms = [new() { Term = "X", Definition = "Y" }] }));
        Assert.Throws<ArgumentException>(() => TagMaintenancePolicy.Plan("Project", Snapshot(),
            Merge() with { Evidence = ["card:invented"] }));
    }

    [Fact]
    public void GlossaryChangesPreserveUnrelatedTermsAndCanonicalizeWhitespace()
    {
        var snapshot = Snapshot();
        snapshot.Glossaries["execution-and-runner"].Add(new() { Term = "Preserve", Definition = "Original" });
        Assert.Throws<ArgumentException>(() => TagMaintenancePolicy.Plan("Project", snapshot, Merge()));
        var plan = TagMaintenancePolicy.Plan("Project", Snapshot(), Merge() with
        {
            Kind = "glossary", Evidence = ["dossier:two"],
            Terms = [new() { Term = "  New  term ", Definition = "  One   definition  " }],
        });
        Assert.Contains("New term", plan.Single().After);
        Assert.DoesNotContain("  ", plan.Single().After);
    }

    [Fact]
    public void StrictRegistryFailureThrowsRestoresCacheAndCanBeRetried()
    {
        Directory.CreateDirectory(_root);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["TaskRepository"] = _root }).Build();
        var registry = new TagRegistryService(Microsoft.Extensions.Logging.Abstractions.NullLogger<TagRegistryService>.Instance, config);
        registry.GetAll();
        var path = Path.Combine(_root, "tags.json");
        File.Delete(path);
        Directory.CreateDirectory(path);
        Assert.ThrowsAny<Exception>(() => registry.Delete("incident", requireDurable: true));
        Assert.True(registry.Exists("incident"));
        Directory.Delete(path);
        Assert.True(registry.Delete("incident", requireDurable: true));
        var restarted = new TagRegistryService(Microsoft.Extensions.Logging.Abstractions.NullLogger<TagRegistryService>.Instance, config);
        Assert.False(restarted.Exists("incident"));
    }

    private sealed class FakeSynthesis : ITagMaintenanceSynthesis
    {
        public string Model => "test-sonnet";
        public bool Fail { get; set; }
        public bool Cancel { get; set; }
        public TagMaintenanceProposal Proposal { get; init; } = Merge();
        public Task<IReadOnlyList<TagMaintenanceProposal>> ProposeAsync(string project, string input, CancellationToken ct)
        {
            if (Cancel) throw new OperationCanceledException(ct);
            return Fail ? throw new InvalidOperationException("Model unavailable")
                : Task.FromResult<IReadOnlyList<TagMaintenanceProposal>>([Proposal]);
        }
    }
    private sealed class FakeClassifier : ITagGoldenSetClassifier
    {
        public string Model => "test-sonnet";
        public string[] LowTags { get; init; } = ["new"];
        public string[] HighTags { get; init; } = ["new"];
        public List<string> Calls { get; } = [];
        public Task<IReadOnlyList<TagClassificationPrediction>> ClassifyAsync(string project,
            IReadOnlyList<TagGoldenSetItem> items, TagMaintenanceSnapshot context, string thinkingLevel,
            CancellationToken ct)
        {
            Calls.Add(thinkingLevel);
            var tags = thinkingLevel == "low" ? LowTags : HighTags;
            return Task.FromResult<IReadOnlyList<TagClassificationPrediction>>(items.Select(item => new TagClassificationPrediction
            {
                Kind = item.Kind, Id = item.Id, Tags = tags, Confidence = 0.9,
            }).ToArray());
        }
    }
    private sealed class FakeWorkspace : ITagMaintenanceWorkspace
    {
        public TagMaintenanceSnapshot Snapshot { get; } = TagMaintenanceTests.Snapshot();
        public Dictionary<string, string> Values { get; } = [];
        public List<string> Writes { get; } = [];
        public List<string> Cards { get; } = [];
        public string? FailOnce { get; set; }
        public string? ThrowReadAfterWriteOnce { get; set; }
        private string? _pendingVerificationReadFailure;
        public IReadOnlyList<string> Projects() => ["Project"];
        public TagMaintenanceSnapshot Capture(string project) => Snapshot;
        public string CreateCard(string project, TagMaintenanceDecision decision)
        { Cards.Add(TagMaintenancePolicy.Card(project, decision)); return "card-" + decision.Id; }
        public string Read(TagMaintenanceChange change)
        {
            var key = change.Kind + "/" + change.Id;
            if (_pendingVerificationReadFailure == key)
            {
                _pendingVerificationReadFailure = null;
                throw new IOException("Simulated verification read failure");
            }
            return Values.GetValueOrDefault(key, change.Before);
        }
        public bool Write(TagMaintenanceChange change, string? taggingStatus = null)
        {
            var key = change.Kind + "/" + change.Id;
            if (key == FailOnce) { FailOnce = null; throw new IOException("Simulated write failure"); }
            if (Read(change) == change.After) return false;
            Values[key] = change.After; Writes.Add(key);
            if (key == ThrowReadAfterWriteOnce)
            {
                ThrowReadAfterWriteOnce = null;
                _pendingVerificationReadFailure = key;
            }
            return true;
        }
    }
}
