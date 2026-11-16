using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The documented transition shares AGT-2375's descriptor ownership rule:
/// lifecycle state is written to workbench.json, never to a Wiki sidecar.
/// </summary>
public sealed class WorkbenchLifecycleServiceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "workbench-lifecycle-" + Guid.NewGuid().ToString("N"));
    private readonly string _tasks;

    public WorkbenchLifecycleServiceTests()
    {
        _tasks = Path.Combine(_root, ".orchestrator", "jobs");
        Directory.CreateDirectory(_root);
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(_tasks, state));
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Theory]
    [InlineData(true, null, StatusCodes.Status200OK)]
    [InlineData(false, "not-canonical", StatusCodes.Status404NotFound)]
    [InlineData(false, "references-not-terminal", StatusCodes.Status409Conflict)]
    [InlineData(false, "validation", StatusCodes.Status400BadRequest)]
    [InlineData(false, "write-failed", StatusCodes.Status500InternalServerError)]
    public void HttpResult_MapsLifecycleOutcomesToTheEndpointContract(
        bool success,
        string? errorCode,
        int expectedStatus)
    {
        var result = ProjectDocsEndpoints.WorkbenchLifecycleHttpResult(new DocumentWorkbenchResult
        {
            Success = success,
            ErrorCode = errorCode,
            WorkbenchId = "delivery",
        });

        Assert.Equal(expectedStatus, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    [Fact]
    public void Document_WritesStatusToTheDescriptorWhenEveryReferenceIsTerminal()
    {
        WriteWorkbench("delivery", "decided", ["AGT-1", "AGT-2"]);
        WriteTask("AGT-1", TaskStates.Completed);
        WriteTask("AGT-2", TaskStates.Archive);
        var (catalogue, lifecycle) = Services();
        var document = catalogue.Read("Project", "delivery")!;

        var result = lifecycle.Document("Project", "delivery", Request(document));

        Assert.True(result.Success, result.Error);
        Assert.Equal("documented", result.Status);
        using var json = JsonDocument.Parse(File.ReadAllText(Descriptor("delivery")));
        Assert.Equal("documented", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("Operator", json.RootElement.GetProperty("editedBy").GetString());
        Assert.Equal("documented", catalogue.List("Project", includeHistory: true)!.Items.Single().Status);
        Assert.Empty(catalogue.List("Project")!.Items);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Descriptor("delivery"))!, "*.meta.json"));
    }

    [Fact]
    public void Document_UsesCardsThatReferenceTheWorkbenchKey()
    {
        WriteWorkbench("delivery", "decided", []);
        var descriptor = JsonNode.Parse(File.ReadAllText(Descriptor("delivery")))!.AsObject();
        descriptor["key"] = "AGT-W7";
        File.WriteAllText(Descriptor("delivery"), descriptor.ToJsonString());
        WriteTask("AGT-1", TaskStates.Completed, ["AGT-W7"]);
        var (catalogue, lifecycle) = Services();
        var document = catalogue.Read("Project", "delivery")!;

        Assert.True(document.Workbench.Documentation!.Eligible);
        Assert.Equal("AGT-1", Assert.Single(document.Workbench.Documentation.References).Key);

        var result = lifecycle.Document("Project", "delivery", Request(document));

        Assert.True(result.Success, result.Error);
        Assert.Equal("documented", result.Status);
    }

    [Fact]
    public void DocumentationProposal_UsesDescriptorSourceTaskBackEdges()
    {
        WriteWorkbench("delivery", "decided", []);
        var descriptor = JsonNode.Parse(File.ReadAllText(Descriptor("delivery")))!.AsObject();
        descriptor["sourceTaskKeys"] = new JsonArray("AGT-1");
        File.WriteAllText(Descriptor("delivery"), descriptor.ToJsonString());
        WriteTask("AGT-1", TaskStates.Completed);
        var (catalogue, _) = Services();

        var document = catalogue.Read("Project", "delivery")!;

        Assert.True(document.Workbench.Documentation!.Eligible);
        var reference = Assert.Single(document.Workbench.Documentation.References);
        Assert.Equal("AGT-1", reference.Key);
        Assert.True(reference.Terminal);
    }

    [Fact]
    public void Document_RefusesOpenOrMissingReferencesWithoutChangingTheDescriptor()
    {
        WriteWorkbench("delivery", "decided", ["AGT-1", "AGT-404"]);
        WriteTask("AGT-1", TaskStates.Ready);
        var (catalogue, lifecycle) = Services();
        var document = catalogue.Read("Project", "delivery")!;
        var before = File.ReadAllText(Descriptor("delivery"));

        var result = lifecycle.Document("Project", "delivery", Request(document));

        Assert.False(result.Success);
        Assert.Equal("references-not-terminal", result.ErrorCode);
        Assert.Equal(before, File.ReadAllText(Descriptor("delivery")));
    }

    [Fact]
    public void Catalogue_AcceptedConceptWithAnOpenSlice_KeepsMarkDocumentedClosedAndNamesTheSlice()
    {
        // AGT-3837: the concept card is accepted, one promoted slice is done and
        // one is still open. The slices are linked only through task.json dossier.
        WriteConceptWorkbench("delivery", sliceCount: 2);
        var entry = "docs/workbenches/delivery/index.html";
        WriteTask("AGT-1", TaskStates.Completed, dossier: new { path = entry });
        WriteTask("AGT-2", TaskStates.Completed, dossier: new { path = entry, implementationIndex = 0 });
        WriteTask("AGT-3", TaskStates.Ready, dossier: new { path = entry, implementationIndex = 1 });
        var (catalogue, lifecycle, cards) = ServicesWithCards();

        var document = catalogue.Read("Project", "delivery")!;
        var documentation = document.Workbench.Documentation!;
        var list = cards.List("Project", document.Workbench);

        Assert.False(documentation.Eligible);
        Assert.Equal(1, documentation.OpenCount);
        Assert.Contains($"AGT-3 ({TaskStates.Ready})", documentation.BlockedReason);
        Assert.DoesNotContain("AGT-2", documentation.BlockedReason);
        Assert.Equal(
            list.Cards.Select(card => card.Key).Order(),
            documentation.References.Select(reference => reference.Key).Order());
        Assert.Equal((2, 1), (list.ImplementationSlices, list.CompletedSlices));

        var before = File.ReadAllText(Descriptor("delivery"));
        var result = lifecycle.Document("Project", "delivery", Request(document));

        Assert.False(result.Success);
        Assert.Equal("references-not-terminal", result.ErrorCode);
        Assert.Contains("AGT-3", result.Error);
        Assert.Equal(before, File.ReadAllText(Descriptor("delivery")));
    }

    [Fact]
    public void Catalogue_CountsASliceThatOnlyThePromotionLedgerNames()
    {
        WriteConceptWorkbench("delivery", sliceCount: 2);
        var entry = "docs/workbenches/delivery/index.html";
        var concept = WriteTask("AGT-1", TaskStates.Completed, dossier: new { path = entry });
        WriteTask("AGT-2", TaskStates.Completed, dossier: new { path = entry, implementationIndex = 0 });
        WriteTask("AGT-3", TaskStates.Ready);
        Directory.CreateDirectory(Path.Combine(concept, ".metadata"));
        File.WriteAllText(Path.Combine(concept, ".metadata", "spawned-tasks.jsonl"),
            JsonSerializer.Serialize(new { reason = $"concept-promotion:{entry}:1", targetKey = "AGT-3" }) + "\n");
        var (catalogue, _, cards) = ServicesWithCards();

        var document = catalogue.Read("Project", "delivery")!;
        var list = cards.List("Project", document.Workbench);

        Assert.False(document.Workbench.Documentation!.Eligible);
        Assert.Contains("AGT-3", document.Workbench.Documentation.BlockedReason);
        Assert.Equal(1, list.Cards.Single(card => card.Key == "AGT-3").ImplementationIndex);
        Assert.Equal((2, 1), (list.ImplementationSlices, list.CompletedSlices));
    }

    [Fact]
    public void Catalogue_OffersMarkDocumentedOnceEverySliceIsTerminal()
    {
        WriteConceptWorkbench("delivery", sliceCount: 2);
        var entry = "docs/workbenches/delivery/index.html";
        WriteTask("AGT-1", TaskStates.Completed, dossier: new { path = entry });
        WriteTask("AGT-2", TaskStates.Completed, dossier: new { path = entry, implementationIndex = 0 });
        WriteTask("AGT-3", TaskStates.Archive, dossier: new { path = entry, implementationIndex = 1 });
        var (catalogue, _, _) = ServicesWithCards();

        var documentation = catalogue.Read("Project", "delivery")!.Workbench.Documentation!;

        Assert.True(documentation.Eligible);
        Assert.Null(documentation.BlockedReason);
        Assert.Equal(3, documentation.TotalCount);
    }

    [Fact]
    public void Document_LeavesTheOriginalDescriptorWhenTheAtomicWriteFails()
    {
        WriteWorkbench("delivery", "decided", ["AGT-1"]);
        WriteTask("AGT-1", TaskStates.Completed);
        var (catalogue, lifecycle) = Services(new FailingWriter());
        var document = catalogue.Read("Project", "delivery")!;
        var before = File.ReadAllText(Descriptor("delivery"));

        var result = lifecycle.Document("Project", "delivery", Request(document));

        Assert.False(result.Success);
        Assert.Equal("write-failed", result.ErrorCode);
        Assert.Equal(before, File.ReadAllText(Descriptor("delivery")));
    }

    [Fact]
    public void Document_AppendsDocumentedToSchemaTwoLifecycleHistory()
    {
        WriteSchemaTwoWorkbench("delivery", ["AGT-1"]);
        WriteTask("AGT-1", TaskStates.Completed);
        var (catalogue, lifecycle) = Services();
        var document = catalogue.Read("Project", "delivery")!;

        var result = lifecycle.Document("Project", "delivery", Request(document));

        Assert.True(result.Success, result.Error);
        using var json = JsonDocument.Parse(File.ReadAllText(Descriptor("delivery")));
        Assert.Equal("documented", json.RootElement.GetProperty("lifecycleState").GetString());
        Assert.False(json.RootElement.TryGetProperty("status", out _));
        var history = json.RootElement.GetProperty("lifecycleHistory").EnumerateArray().ToList();
        Assert.Equal("documented", history[^1].GetProperty("state").GetString());
        Assert.Equal("documented", catalogue.List("Project", includeHistory: true)!.Items.Single().Status);
    }

    private DocumentWorkbenchRequest Request(WorkbenchDocument document) => new()
    {
        Actor = "Operator",
        ExpectedFingerprint = document.Fingerprint,
    };

    private void WriteWorkbench(string id, string status, string[] references)
    {
        var dir = Path.Combine(_root, "docs", "workbenches", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.html"), $"<h1>{id}</h1>");
        File.WriteAllText(Descriptor(id), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            id,
            title = "Delivery notes",
            summary = "Track implementation to completion.",
            entrypoint = "index.html",
            status,
            phase = "decision-ready",
            updatedAt = "2026-08-09T10:00:00Z",
            relatedTaskKeys = references,
        }));
    }

    private void WriteSchemaTwoWorkbench(string id, string[] references)
    {
        var dir = Path.Combine(_root, "docs", "workbenches", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.html"), $"<h1>{id}</h1>");
        File.WriteAllText(Descriptor(id), $$"""
          {
            "schemaVersion": 2,
            "id": "{{id}}",
            "title": "Delivery notes",
            "summary": "Track implementation to completion.",
            "entrypoint": "index.html",
            "pageKind": "workbench",
            "lifecycleState": "decided",
            "phase": "decision-ready",
            "editedBy": "Operator",
            "editedAt": "2026-08-09T10:00:00Z",
            "lifecycleHistory": [
              { "state": "decided", "editedBy": "Operator", "editedAt": "2026-08-09T10:00:00Z" }
            ],
            "relatedTaskKeys": {{JsonSerializer.Serialize(references)}},
            "decision": {
              "outcome": "feature-spawn",
              "state": "succeeded",
              "operationId": "workbench-ui-delivery",
              "sourceFingerprint": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "preparedAt": "2026-08-09T09:58:00Z",
              "preparedBy": "Operator",
              "confirmedAt": "2026-08-09T09:59:00Z",
              "confirmedBy": "Operator",
              "decidedAt": "2026-08-09T10:00:00Z",
              "spawnedTaskKeys": [],
              "taskDraft": {
                "title": "Implement delivery",
                "goal": "Complete the delivery.",
                "acceptanceCriteria": ["The delivery is verified."],
                "evidenceLinks": [],
                "relatedTaskKeys": [],
                "initialLane": "1-preparation",
                "mode": "coding",
                "taskType": "feature"
              }
            }
          }
          """);
    }

    private string WriteTask(string key, string state, string[]? workbenchKeys = null, object? dossier = null)
    {
        var id = key.ToLowerInvariant();
        var dir = Path.Combine(_tasks, state, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"), JsonSerializer.Serialize(new
        {
            id,
            key,
            title = $"Task {key}",
            state,
            order = 1,
            agent = "codex",
            references = workbenchKeys == null ? null : new { workbenches = workbenchKeys },
            dossier,
        }));
        return dir;
    }

    /// <summary>A decided concept Dossier whose source concept card is AGT-1.</summary>
    private void WriteConceptWorkbench(string id, int sliceCount)
    {
        WriteWorkbench(id, "decided", []);
        var descriptor = JsonNode.Parse(File.ReadAllText(Descriptor(id)))!.AsObject();
        descriptor["sourceTaskKeys"] = new JsonArray("AGT-1");
        descriptor["implementationTasks"] = new JsonArray(Enumerable.Range(1, sliceCount)
            .Select(index => (JsonNode)new JsonObject { ["title"] = $"Slice {index}" })
            .ToArray());
        File.WriteAllText(Descriptor(id), descriptor.ToJsonString());
    }

    private string Descriptor(string id) =>
        Path.Combine(_root, "docs", "workbenches", id, "workbench.json");

    private (WorkbenchCatalogueService Catalogue, WorkbenchLifecycleService Lifecycle) Services(
        IAtomicJsonFileWriter? writer = null)
    {
        var (catalogue, lifecycle, _) = ServicesWithCards(writer);
        return (catalogue, lifecycle);
    }

    private (WorkbenchCatalogueService Catalogue, WorkbenchLifecycleService Lifecycle, DossierCardsService Cards)
        ServicesWithCards(IAtomicJsonFileWriter? writer = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "Project",
            ["WatchPaths:0:RootPath"] = _root,
            ["WatchPaths:0:Path"] = _tasks,
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var catalogue = new WorkbenchCatalogueService(scanner, registry, git);
        return (catalogue, new WorkbenchLifecycleService(catalogue, git, writer), new DossierCardsService(scanner));
    }

    private sealed class FailingWriter : IAtomicJsonFileWriter
    {
        public void Write(string path, string content) => throw new IOException("forced write failure");
    }
}
