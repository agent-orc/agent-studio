using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The write half of the Workbench Decision gate (AGT-2375). The decision must land in
/// the Workbench's own <c>workbench.json</c> - that file, not a
/// <c>.meta.json</c> sidecar, is what the catalogue and the Wiki read.
/// </summary>
public sealed class WorkbenchDecisionServiceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "workbench-decision-" + Guid.NewGuid().ToString("N"));

    public WorkbenchDecisionServiceTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Theory]
    [InlineData("old-head", "same-fingerprint", "new-head", "same-fingerprint", null)]
    [InlineData("same-head", "old-fingerprint", "same-head", "new-fingerprint", "content changed")]
    [InlineData("old-head", null, "new-head", "same-fingerprint", "revision changed")]
    [InlineData(null, null, "new-head", "same-fingerprint", "must name")]
    public void StalenessPolicy_IsFileScopedWhenFingerprintIsAvailable(
        string? expectedRevision,
        string? expectedFingerprint,
        string? currentRevision,
        string? currentFingerprint,
        string? errorFragment)
    {
        var error = WorkbenchDecisionContracts.StalenessError(
            expectedRevision, expectedFingerprint, currentRevision, currentFingerprint);

        if (errorFragment == null) Assert.Null(error);
        else Assert.Contains(errorFragment, error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Prepare_RejectsAnIncompleteFeatureDraftWithoutTouchingTheDescriptor()
    {
        WriteSchemaTwo("routing-policy");
        var (catalogue, decisions) = Services();
        var before = File.ReadAllText(Descriptor("routing-policy"));

        var result = decisions.Prepare("Project", "routing-policy", new PrepareWorkbenchDecisionRequest
        {
            OperationId = "workbench-ui-prepare-1",
            Outcome = "feature-spawn",
            Actor = "Robert",
            ExpectedFingerprint = catalogue.Read("Project", "routing-policy")!.Fingerprint,
            Task = new WorkbenchTaskDraft { Title = "Implement it", Goal = "" },
        });

        Assert.False(result.Success);
        Assert.Equal("validation", result.ErrorCode);
        Assert.Equal(before, File.ReadAllText(Descriptor("routing-policy")));
    }

    [Fact]
    public void Prepare_ValidatesAgainstTheCurrentFingerprintWithoutWriting()
    {
        WriteSchemaTwo("routing-policy");
        var (catalogue, decisions) = Services();
        var fingerprint = catalogue.Read("Project", "routing-policy")!.Fingerprint;

        var result = decisions.Prepare("Project", "routing-policy", FeaturePrepare(fingerprint));

        Assert.True(result.Success);
        Assert.Equal("prepared", result.DecisionStage);
        Assert.Equal(fingerprint, result.Fingerprint);
        Assert.Equal("Implement the routing policy", result.TaskDraft!.Title);
        Assert.Null(catalogue.List("Project", includeHistory: true)!.Items.Single().Decision);
    }

    [Fact]
    public void Confirm_WritesTheDecisionIntoWorkbenchJsonWithLifecycleHistory()
    {
        WriteSchemaTwo("routing-policy");
        var notifier = new WorkbenchChangeNotifier(NullLogger<WorkbenchChangeNotifier>.Instance);
        WorkbenchDecisionRecordedEvent? recorded = null;
        notifier.DecisionRecorded += evt => recorded = evt;
        var (catalogue, decisions) = Services(notifier);
        var fingerprint = catalogue.Read("Project", "routing-policy")!.Fingerprint;

        var result = decisions.Confirm("Project", "routing-policy", new ConfirmWorkbenchDecisionRequest
        {
            OperationId = "workbench-ui-confirm-1",
            Outcome = "feature-spawn",
            Actor = "Robert",
            ExpectedFingerprint = fingerprint,
            Task = FeaturePrepare(fingerprint).Task,
            Responses =
            [
                new WorkbenchDecisionResponse
                {
                    DecisionId = "route",
                    Kind = "single",
                    SelectedOptionIds = ["direct"],
                    Comment = "Keep the boundary explicit.",
                },
            ],
            SpawnedTaskKeys = ["AGT-2527"],
            CliType = "codex",
            Model = "gpt-5.6-sol",
            ThinkingLevel = "high",
            Confirmed = true,
        });

        Assert.True(result.Success);
        Assert.Equal("succeeded", result.DecisionStage);

        using var written = JsonDocument.Parse(File.ReadAllText(Descriptor("routing-policy")));
        var descriptor = written.RootElement;
        Assert.Equal("decided", descriptor.GetProperty("lifecycleState").GetString());
        Assert.Equal("Robert", descriptor.GetProperty("editedBy").GetString());
        var history = descriptor.GetProperty("lifecycleHistory").EnumerateArray().ToList();
        Assert.Equal(2, history.Count);
        Assert.Equal("decided", history[^1].GetProperty("state").GetString());
        Assert.Contains("Implement the routing policy", history[^1].GetProperty("note").GetString());
        Assert.Equal("succeeded", descriptor.GetProperty("decision").GetProperty("state").GetString());
        Assert.Equal("route", descriptor.GetProperty("decision").GetProperty("responses")[0]
            .GetProperty("decisionId").GetString());
        Assert.Equal("AGT-2527", descriptor.GetProperty("relatedTaskKeys")[0].GetString());
        Assert.Equal("created-card", descriptor.GetProperty("decision").GetProperty("action").GetString());
        Assert.Equal("codex", descriptor.GetProperty("decision").GetProperty("cliType").GetString());
        Assert.Equal("gpt-5.6-sol", descriptor.GetProperty("decision").GetProperty("model").GetString());
        Assert.Equal("high", descriptor.GetProperty("decision").GetProperty("thinkingLevel").GetString());

        // The descriptor still validates, so the catalogue projects the decision.
        var item = catalogue.List("Project", includeHistory: true)!.Items.Single();
        Assert.Equal("decided", item.Status);
        Assert.Equal("succeeded", item.DecisionStage);
        Assert.Equal("direct", Assert.Single(item.Decision!.Responses).SelectedOptionIds.Single());
        Assert.Contains("AGT-2527", item.SourceTaskKeys);
        Assert.False(File.Exists(Descriptor("routing-policy") + ".meta.json"));
        Assert.True(recorded.HasValue);
        Assert.Equal(new WorkbenchDecisionRecordedEvent(
            "Project", "routing-policy", "active", "decided"), recorded.Value);
    }

    [Fact]
    public void Confirm_ReworkKeepsDecisionOpenAndAppendsRevisionRequestedTimelineEntry()
    {
        WriteSchemaTwo("routing-policy");
        var (catalogue, decisions) = Services();
        var fingerprint = catalogue.Read("Project", "routing-policy")!.Fingerprint;

        var result = decisions.Confirm("Project", "routing-policy", new ConfirmWorkbenchDecisionRequest
        {
            OperationId = "workbench-ui-rework-1",
            Outcome = "rework",
            Actor = "Robert",
            ExpectedFingerprint = fingerprint,
            Responses =
            [
                new WorkbenchDecisionResponse
                {
                    DecisionId = "route",
                    Kind = "single",
                    SelectedOptionIds = [],
                    Comment = "Move the total higher.",
                },
            ],
            CliType = "codex",
            Model = "gpt-5.6-sol",
            ThinkingLevel = "high",
            Confirmed = true,
        });

        Assert.True(result.Success, result.Error);
        Assert.Equal("pending", result.DecisionStage);
        using var written = JsonDocument.Parse(File.ReadAllText(Descriptor("routing-policy")));
        var descriptor = written.RootElement;
        Assert.Equal("review-requested", descriptor.GetProperty("lifecycleState").GetString());
        Assert.Equal("Revision requested", descriptor.GetProperty("lifecycleHistory")
            .EnumerateArray().Last().GetProperty("note").GetString());
        var receipt = descriptor.GetProperty("decision");
        Assert.Equal("rework-requested", receipt.GetProperty("action").GetString());
        Assert.Equal("pending", receipt.GetProperty("state").GetString());
        Assert.Equal("codex", receipt.GetProperty("cliType").GetString());
        Assert.Equal("gpt-5.6-sol", receipt.GetProperty("model").GetString());
        Assert.Equal("high", receipt.GetProperty("thinkingLevel").GetString());
        Assert.Equal(JsonValueKind.Null, receipt.GetProperty("decidedAt").ValueKind);
        Assert.False(descriptor.TryGetProperty("relatedTaskKeys", out _));

        var item = Assert.Single(catalogue.List("Project", includeHistory: true)!.Items);
        Assert.True(item.Valid, item.Error);
        Assert.Equal("decision-pending", item.Status);
        Assert.Equal("rework", item.Decision!.Outcome);
        Assert.Equal("Move the total higher.", Assert.Single(item.Decision.Responses).Comment);
    }

    [Fact]
    public void Confirm_ReworkAppendsRevisionTimelineForSchemaOneDossiers()
    {
        WriteSchemaOne("legacy-experiment");
        var (catalogue, decisions) = Services();

        var result = decisions.Confirm("Project", "legacy-experiment", new ConfirmWorkbenchDecisionRequest
        {
            OperationId = "workbench-ui-rework-legacy",
            Outcome = "rework",
            Actor = "Robert",
            ExpectedFingerprint = catalogue.Read("Project", "legacy-experiment")!.Fingerprint,
            Responses =
            [
                new WorkbenchDecisionResponse
                {
                    DecisionId = "route",
                    Kind = "single",
                    SelectedOptionIds = [],
                    Comment = "Revise the legacy Dossier.",
                },
            ],
            CliType = "codex",
            Model = "gpt-5.6-sol",
            ThinkingLevel = "high",
            Confirmed = true,
        });

        Assert.True(result.Success, result.Error);
        using var written = JsonDocument.Parse(File.ReadAllText(Descriptor("legacy-experiment")));
        var descriptor = written.RootElement;
        Assert.Equal("decision-pending", descriptor.GetProperty("status").GetString());
        var timeline = Assert.Single(descriptor.GetProperty("lifecycleHistory").EnumerateArray());
        Assert.Equal("Revision requested", timeline.GetProperty("note").GetString());
        Assert.Equal("Robert", timeline.GetProperty("editedBy").GetString());
    }

    [Fact]
    public void Prepare_UsesTheFileFingerprintWhenRepositoryRevisionMovedElsewhere()
    {
        WriteSchemaTwo("routing-policy");
        var (catalogue, decisions) = Services();
        var fingerprint = catalogue.Read("Project", "routing-policy")!.Fingerprint;
        var request = FeaturePrepare(fingerprint) with
        {
            // The checkout used by this unit fixture has no HEAD. Supplying a
            // different informational revision therefore exercises the same
            // matrix branch as an unrelated commit moving a real repository.
            ExpectedRevision = new string('f', 40),
        };

        var result = decisions.Prepare("Project", "routing-policy", request);

        Assert.True(result.Success);
        Assert.Equal(fingerprint, result.Fingerprint);
    }

    [Fact]
    public void Confirm_ArchivesALegacyDescriptorInPlaceInsteadOfASidecar()
    {
        WriteSchemaOne("legacy-experiment");
        var (catalogue, decisions) = Services();

        var result = decisions.Confirm("Project", "legacy-experiment", new ConfirmWorkbenchDecisionRequest
        {
            OperationId = "workbench-ui-archive-1",
            Outcome = "archive",
            Actor = "Robert",
            ExpectedFingerprint = catalogue.Read("Project", "legacy-experiment")!.Fingerprint,
            ArchiveReason = "The experiment disproved the direction.",
            Confirmed = true,
        });

        Assert.True(result.Success);
        Assert.Equal("archived", result.DecisionStage);
        using var written = JsonDocument.Parse(File.ReadAllText(Descriptor("legacy-experiment")));
        Assert.Equal("archived", written.RootElement.GetProperty("status").GetString());
        Assert.Equal("archived", catalogue.List("Project", includeHistory: true)!.Items.Single().Status);
        Assert.Empty(catalogue.List("Project")!.Items);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Descriptor("legacy-experiment"))!, "*.meta.json"));
    }

    [Fact]
    public void Confirm_RefusesAFingerprintThatNoLongerMatchesTheFile()
    {
        WriteSchemaTwo("routing-policy");
        var (catalogue, decisions) = Services();
        var stale = catalogue.Read("Project", "routing-policy")!.Fingerprint;
        File.AppendAllText(Path.Combine(_root, "docs", "workbenches", "routing-policy", "index.html"),
            "<p>Someone edited the artifact.</p>");

        var result = decisions.Confirm("Project", "routing-policy", new ConfirmWorkbenchDecisionRequest
        {
            OperationId = "workbench-ui-confirm-2",
            Outcome = "feature-spawn",
            Actor = "Robert",
            ExpectedFingerprint = stale,
            Task = FeaturePrepare(stale).Task,
            Confirmed = true,
        });

        Assert.False(result.Success);
        Assert.Equal("stale-revision", result.ErrorCode);
        Assert.DoesNotContain("\"decision\"", File.ReadAllText(Descriptor("routing-policy")));
    }

    [Fact]
    public void Confirm_RefusesAnArchiveDecisionThatCarriesSpawnedTaskKeys()
    {
        WriteSchemaTwo("routing-policy");
        var (catalogue, decisions) = Services();
        var before = File.ReadAllText(Descriptor("routing-policy"));

        var result = decisions.Confirm("Project", "routing-policy", new ConfirmWorkbenchDecisionRequest
        {
            OperationId = "workbench-ui-archive-keys",
            Outcome = "archive",
            Actor = "Robert",
            ExpectedFingerprint = catalogue.Read("Project", "routing-policy")!.Fingerprint,
            ArchiveReason = "The experiment disproved the direction.",
            SpawnedTaskKeys = ["AGT-2400"],
            Confirmed = true,
        });

        // The read side refuses a settled archive receipt with spawned keys, so
        // writing one would leave the Workbench permanently unreadable.
        Assert.False(result.Success);
        Assert.Equal("validation", result.ErrorCode);
        Assert.Equal(before, File.ReadAllText(Descriptor("routing-policy")));
        Assert.True(catalogue.List("Project", includeHistory: true)!.Items.Single().Valid);
    }

    [Fact]
    public void Confirm_AnswersARetryOfALegacyDescriptorFromTheStoredReceipt()
    {
        WriteSchemaOne("legacy-experiment");
        var (catalogue, decisions) = Services();
        ConfirmWorkbenchDecisionRequest Request(string operationId) => new()
        {
            OperationId = operationId,
            Outcome = "archive",
            Actor = "Robert",
            ExpectedFingerprint = catalogue.Read("Project", "legacy-experiment")!.Fingerprint,
            ArchiveReason = "The experiment disproved the direction.",
            Confirmed = true,
        };

        Assert.True(decisions.Confirm("Project", "legacy-experiment", Request("workbench-ui-archive-1")).Success);

        // The same idempotency key must replay the settled result, not 409.
        var retry = decisions.Confirm("Project", "legacy-experiment", Request("workbench-ui-archive-1"));
        Assert.True(retry.Success);
        Assert.True(retry.Idempotent);
        Assert.Equal("archive", retry.Outcome);
        Assert.Equal("archived", retry.DecisionStage);

        // A different key on a settled Workbench is still a conflict.
        var other = decisions.Confirm("Project", "legacy-experiment", Request("workbench-ui-archive-2"));
        Assert.False(other.Success);
        Assert.Equal("already-settled", other.ErrorCode);
    }

    [Fact]
    public void Confirm_SerializesTwoConcurrentConfirmationsOfTheSameWorkbench()
    {
        WriteSchemaTwo("routing-policy");
        var (catalogue, decisions) = Services();
        var fingerprint = catalogue.Read("Project", "routing-policy")!.Fingerprint;
        ConfirmWorkbenchDecisionRequest Request(string operationId) => new()
        {
            OperationId = operationId,
            Outcome = "archive",
            Actor = "Robert",
            ExpectedFingerprint = fingerprint,
            ArchiveReason = "The experiment disproved the direction.",
            Confirmed = true,
        };

        var results = new WorkbenchDecisionResult[2];
        using var start = new Barrier(2);
        var threads = new[] { "workbench-ui-race-a", "workbench-ui-race-b" }
            .Select((operationId, index) => new Thread(() =>
            {
                start.SignalAndWait();
                results[index] = decisions.Confirm("Project", "routing-policy", Request(operationId));
            }))
            .ToArray();
        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads)
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "a confirmation never finished");

        // Both were taken on the same fingerprint: unserialized, both would pass
        // the check and the second would overwrite the first decision.
        var winner = Assert.Single(results, result => result.Success);
        var refused = Assert.Single(results, result => !result.Success);
        Assert.Equal("already-settled", refused.ErrorCode);

        var item = Assert.Single(catalogue.List("Project", includeHistory: true)!.Items);
        Assert.True(item.Valid, item.Error);
        Assert.Equal("archived", item.Status);
        Assert.Equal(winner.OperationId, item.Decision!.OperationId);
    }

    private static PrepareWorkbenchDecisionRequest FeaturePrepare(string? fingerprint) => new()
    {
        OperationId = "workbench-ui-prepare-2",
        Outcome = "feature-spawn",
        Actor = "Robert",
        ExpectedFingerprint = fingerprint,
        Task = new WorkbenchTaskDraft
        {
            Title = "Implement the routing policy",
            Goal = "Ship the confirmed direction.",
            AcceptanceCriteria = ["The direction is implemented and verified."],
            TargetProject = "Project",
        },
    };

    private string Descriptor(string id) =>
        Path.Combine(_root, "docs", "workbenches", id, "workbench.json");

    private void WriteSchemaTwo(string id)
    {
        var dir = Path.Combine(_root, "docs", "workbenches", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.html"), $"<h1>{id}</h1>");
        File.WriteAllText(Path.Combine(dir, "workbench.json"), $$"""
          {
            "schemaVersion": 2,
            "id": "{{id}}",
            "title": "Routing policy",
            "summary": "Choose the durable routing direction.",
            "entrypoint": "index.html",
            "pageKind": "workbench",
            "lifecycleState": "review-requested",
            "phase": "decision-ready",
            "editedBy": "Robert",
            "editedAt": "2026-07-26T10:00:00Z",
            "lifecycleHistory": [
              { "state": "review-requested", "editedBy": "Robert", "editedAt": "2026-07-26T10:00:00Z" }
            ]
          }
          """);
    }

    private void WriteSchemaOne(string id)
    {
        var dir = Path.Combine(_root, "docs", "workbenches", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.html"), $"<h1>{id}</h1>");
        File.WriteAllText(Path.Combine(dir, "workbench.json"), $$"""
          {"schemaVersion":1,"id":"{{id}}","title":"Legacy experiment","summary":"Question",
           "entrypoint":"index.html","status":"active","phase":"decision-ready","updatedAt":"2026-07-26T10:00:00Z"}
          """);
    }

    private (WorkbenchCatalogueService Catalogue, WorkbenchDecisionService Decisions) Services(
        WorkbenchChangeNotifier? notifier = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "Project",
            ["WatchPaths:0:RootPath"] = _root,
            ["WatchPaths:0:Path"] = Path.Combine(_root, ".orchestrator", "jobs"),
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var catalogue = new WorkbenchCatalogueService(scanner, registry, git);
        return (catalogue, new WorkbenchDecisionService(catalogue, git, notifier: notifier));
    }
}
