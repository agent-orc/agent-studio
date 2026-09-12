using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class WorkbenchCatalogueTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "workbench-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "workbench-outside-" + Guid.NewGuid().ToString("N"));

    public WorkbenchCatalogueTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        var linkedWorkbench = Path.Combine(_root, "docs", "workbenches", "linked");
        try
        {
            if (Directory.Exists(linkedWorkbench)
                && (File.GetAttributes(linkedWorkbench) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(linkedWorkbench);
        }
        catch { }
        try { Directory.Delete(_root, true); } catch { }
        try { Directory.Delete(_outside, true); } catch { }
    }

    [Fact]
    public void List_ValidatesSortsAndKeepsInvalidEntriesVisible()
    {
        WriteWorkbench("older", "Older", "active", "2026-07-10T10:00:00Z");
        WriteWorkbench("newer", "Newer", "decision-pending", "2026-07-12T10:00:00Z");
        var invalid = Path.Combine(_root, "docs", "workbenches", "broken");
        Directory.CreateDirectory(invalid);
        File.WriteAllText(Path.Combine(invalid, "workbench.json"), "{not-json");

        var catalogue = Service().List("Project")!;

        Assert.Equal(3, catalogue.Count);
        Assert.Equal(new[] { "newer", "older" }, catalogue.Items.Where(x => x.Valid).Select(x => x.Id));
        Assert.Equal(1, catalogue.Items.Single(x => x.Id == "newer").OpenDecisionCount);
        Assert.Contains(catalogue.Items, x => x.Id == "broken" && !x.Valid && x.Error != null);
    }

    [Fact]
    public void List_AcceptsInformationalSnapshotDossiers()
    {
        WriteWorkbench("lagebild", "Lagebild", "active", "2026-08-03T04:00:00Z");
        var descriptorPath = Path.Combine(
            _root, "docs", "workbenches", "lagebild", "workbench.json");
        var descriptor = JsonNode.Parse(File.ReadAllText(descriptorPath))!.AsObject();
        descriptor["phase"] = "informational";
        File.WriteAllText(descriptorPath, descriptor.ToJsonString());

        var item = Assert.Single(Service().List("Project")!.Items);

        Assert.True(item.Valid, item.Error);
        Assert.Equal("informational", item.Phase);
        Assert.Equal("in-progress", item.LifecycleState);
    }

    [Fact]
    public void List_ValidatesAndProjectsOptionalRelevanceReview()
    {
        WriteWorkbench("reviewed", "Reviewed", "active", "2026-09-12T10:00:00Z");
        var path = Path.Combine(_root, "docs", "workbenches", "reviewed", "workbench.json");
        var descriptor = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        descriptor["review"] = JsonNode.Parse("""
          {"verdict":"partially-superseded","supersededBy":["AGT-W51"],"reviewedAt":"2026-09-12T14:00:00Z","reviewedBy":"AGT-2784","note":"The storage contract remains current."}
          """);
        File.WriteAllText(path, descriptor.ToJsonString());

        var item = Assert.Single(Service().List("Project")!.Items);

        Assert.True(item.Valid, item.Error);
        Assert.Equal("partially-superseded", item.Review!.Verdict);
        Assert.Equal("AGT-W51", Assert.Single(item.Review.SupersededBy));
        Assert.Equal("AGT-2784", item.Review.ReviewedBy);
        Assert.False(item.ReviewDue);
    }

    [Fact]
    public void List_RejectsMalformedRelevanceReview()
    {
        WriteWorkbench("reviewed", "Reviewed", "active", "2026-09-12T10:00:00Z");
        var path = Path.Combine(_root, "docs", "workbenches", "reviewed", "workbench.json");
        var descriptor = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        descriptor["review"] = JsonNode.Parse("""
          {"verdict":"obsolete","supersededBy":[],"reviewedAt":"yesterday","reviewedBy":"unknown","note":""}
          """);
        File.WriteAllText(path, descriptor.ToJsonString());

        var item = Assert.Single(Service().List("Project", includeHistory: true)!.Items);

        Assert.False(item.Valid);
        Assert.Contains("review verdict", item.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Overview_UsesOneProjectionForWorkspaceAndProjectScopes()
    {
        WriteWorkbench("current", "Current", "active", "2026-07-12T10:00:00Z");
        WriteWorkbench("tracking", "Tracking", "decided", "2026-07-11T12:00:00Z");
        WriteWorkbench("discarded", "Discarded", "archived", "2026-07-11T10:00:00Z");
        WriteWorkbench("documented", "Documented", "documented", "2026-07-11T09:00:00Z");
        var service = Service();

        var overview = service.ListOverview(["Project"], "Project");

        Assert.Equal("Project", overview.ProjectName);
        Assert.Equal(4, overview.Count);
        Assert.Equal(2, overview.CurrentCount);
        Assert.Equal(2, overview.HistoryCount);
        Assert.All(overview.Items, item => Assert.Equal("Project", item.ProjectName));
    }

    [Fact]
    public void List_CountsValidInlineDecisionPointsAndIgnoresMalformedDuplicates()
    {
        WriteWorkbench("decision-count", "Decision count", "decision-pending", "2026-07-12T10:00:00Z");
        var entrypoint = Path.Combine(_root, "docs", "workbenches", "decision-count", "index.html");
        File.WriteAllText(entrypoint, """
          <section data-decision-id="route" data-decision-kind="single">
            <span data-option-id="direct">Direct</span>
          </section>
          <section data-decision-id="checks" data-decision-kind="multi">
            <span data-option-id="build">Build</span>
            <span data-option-id="e2e">E2E</span>
          </section>
          <section data-decision-id="route" data-decision-kind="confirm">
            <span data-option-id="duplicate">Duplicate</span>
          </section>
          <section data-decision-id="invalid id" data-decision-kind="single">
            <span data-option-id="ignored">Ignored</span>
          </section>
          """);

        var item = Assert.Single(Service().List("Project")!.Items);

        Assert.Equal(2, item.OpenDecisionCount);
    }

    [Fact]
    public void List_HidesSettledItemsUnlessHistoryRequested()
    {
        WriteWorkbench("current", "Current", "active", "2026-07-12T10:00:00Z");
        WriteWorkbench("tracking", "Tracking", "decided", "2026-07-11T11:00:00Z");
        WriteWorkbench("documented", "Documented", "documented", "2026-07-11T10:30:00Z");
        WriteWorkbench("done", "Done", "archived", "2026-07-11T10:00:00Z");
        Assert.Equal(new[] { "current", "tracking" }, Service().List("Project")!.Items.Select(item => item.Id));
        Assert.Equal(4, Service().List("Project", includeHistory: true)!.Items.Count);
    }

    [Fact]
    public void List_ProjectsUiPatternAndTolerantlyDefaultsMissingOrUnknownValues()
    {
        WriteWorkbench("visual", "Visual", "active", "2026-07-12T10:00:00Z", "ui");
        WriteWorkbench("reasoning", "Reasoning", "active", "2026-07-11T10:00:00Z", "concept");
        WriteWorkbench("legacy", "Legacy", "active", "2026-07-10T10:00:00Z");
        WriteWorkbench("future", "Future", "active", "2026-07-09T10:00:00Z", "spatial");

        var items = Service().List("Project")!.Items;

        Assert.All(items, item => Assert.True(item.Valid, item.Error));
        Assert.Equal("ui", items.Single(item => item.Id == "visual").Pattern);
        Assert.Equal("concept", items.Single(item => item.Id == "reasoning").Pattern);
        Assert.Equal("concept", items.Single(item => item.Id == "legacy").Pattern);
        Assert.Equal("concept", items.Single(item => item.Id == "future").Pattern);
    }

    [Fact]
    public void List_SchemaTwoUsesSharedLifecycleAsItsOnlyStoredState()
    {
        var dir = Path.Combine(_root, "docs", "workbenches", "shared-lifecycle");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.html"), "<h1>Shared lifecycle</h1>");
        File.WriteAllText(Path.Combine(dir, "workbench.json"), """
          {
            "schemaVersion": 2,
            "id": "shared-lifecycle",
            "title": "Shared lifecycle",
            "summary": "Question",
            "entrypoint": "index.html",
            "pageKind": "workbench",
            "lifecycleState": "review-requested",
            "phase": "decision-ready",
            "editedBy": "Robert",
            "editedAt": "2026-07-21T05:46:33Z",
            "lifecycleHistory": [
              { "state": "review-requested", "editedBy": "Robert", "editedAt": "2026-07-21T05:46:33Z" }
            ]
          }
          """);

        var item = Assert.Single(Service().List("Project")!.Items);

        Assert.Equal("review-requested", item.LifecycleState);
        Assert.Equal("Robert", item.EditedBy);
        Assert.Single(item.LifecycleHistory!);
        Assert.Equal("active", item.Status); // compatibility projection, not stored metadata
    }

    [Fact]
    public void List_ProjectsDurableDecisionReceipt()
    {
        var dir = Path.Combine(_root, "docs", "workbenches", "settled-decision");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.html"), "<h1>Settled decision</h1>");
        File.WriteAllText(Path.Combine(dir, "workbench.json"), """
          {
            "schemaVersion": 2,
            "id": "settled-decision",
            "title": "Settled decision",
            "summary": "Durable receipt",
            "entrypoint": "index.html",
            "pageKind": "workbench",
            "lifecycleState": "decided",
            "phase": "decision-ready",
            "editedBy": "Robert",
            "editedAt": "2026-07-26T10:02:00Z",
            "lifecycleHistory": [
              { "state": "decided", "editedBy": "Robert", "editedAt": "2026-07-26T10:02:00Z" }
            ],
            "decision": {
              "outcome": "feature-spawn",
              "state": "succeeded",
              "operationId": "workbench-ui-settled",
              "sourceRevision": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "sourceFingerprint": null,
              "preparedAt": "2026-07-26T10:00:00Z",
              "preparedBy": "Robert",
              "confirmedAt": "2026-07-26T10:01:00Z",
              "confirmedBy": "Robert",
              "decidedAt": "2026-07-26T10:02:00Z",
              "spawnedTaskKeys": ["AGT-2400"],
              "taskDraft": {
                "title": "Implement the decision",
                "goal": "Ship the confirmed Dossier direction.",
                "acceptanceCriteria": ["The direction is implemented and verified."],
                "evidenceLinks": [],
                "relatedTaskKeys": [],
                "initialLane": "1-preparation",
                "mode": "coding",
                "taskType": "feature"
              }
            }
          }
          """);

        var item = Assert.Single(Service().List("Project", includeHistory: true)!.Items);

        Assert.Equal("decided", item.Status);
        Assert.Equal("succeeded", item.DecisionStage);
        Assert.Equal("feature-spawn", item.Decision!.Outcome);
        Assert.Equal(new[] { "AGT-2400" }, item.Decision.SpawnedTaskKeys);
    }

    [Fact]
    public void List_SchemaTwoRejectsDuplicateLegacyStateAndMismatchedHistory()
    {
        var dir = Path.Combine(_root, "docs", "workbenches", "duplicate-state");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.html"), "<h1>Duplicate state</h1>");
        File.WriteAllText(Path.Combine(dir, "workbench.json"), """
          {
            "schemaVersion": 2,
            "id": "duplicate-state",
            "title": "Duplicate state",
            "summary": "Invalid duplicate truth",
            "entrypoint": "index.html",
            "pageKind": "workbench",
            "lifecycleState": "decided",
            "status": "active",
            "editedBy": "Robert",
            "editedAt": "2026-07-21T05:46:33Z",
            "lifecycleHistory": [
              { "state": "decided", "editedBy": "Robert", "editedAt": "2026-07-21T05:46:33Z" }
            ]
          }
          """);
        var mismatchDir = Path.Combine(_root, "docs", "workbenches", "mismatched-history");
        Directory.CreateDirectory(mismatchDir);
        File.WriteAllText(Path.Combine(mismatchDir, "index.html"), "<h1>Mismatched history</h1>");
        File.WriteAllText(Path.Combine(mismatchDir, "workbench.json"), """
          {
            "schemaVersion": 2,
            "id": "mismatched-history",
            "title": "Mismatched history",
            "summary": "Invalid current projection",
            "entrypoint": "index.html",
            "pageKind": "workbench",
            "lifecycleState": "decided",
            "editedBy": "Robert",
            "editedAt": "2026-07-21T05:46:33Z",
            "lifecycleHistory": [
              { "state": "review-requested", "editedBy": "Robert", "editedAt": "2026-07-21T05:46:33Z" }
            ]
          }
          """);

        var items = Service().List("Project", includeHistory: true)!.Items;

        Assert.Equal(2, items.Count);
        Assert.Contains(items, item => item.Id == "duplicate-state" && !item.Valid
            && item.Error!.Contains("legacy status", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(items, item => item.Id == "mismatched-history" && !item.Valid
            && item.Error!.Contains("latest lifecycleHistory", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void List_ProjectsALegacyDecisionReceiptWithoutCouplingItToALifecycleState()
    {
        // Schema v1 has no lifecycleState, but it stores the receipt under the
        // same key. Without this projection the decision service cannot see the
        // operationId it wrote and answers a retry with 409 instead of the
        // settled result (AGT-2375).
        var dir = Path.Combine(_root, "docs", "workbenches", "legacy-settled");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.html"), "<h1>Legacy settled</h1>");
        File.WriteAllText(Path.Combine(dir, "workbench.json"), $$"""
          {
            "schemaVersion": 1,
            "id": "legacy-settled",
            "title": "Legacy settled",
            "summary": "Question",
            "entrypoint": "index.html",
            "status": "archived",
            "updatedAt": "2026-07-26T10:02:00Z",
            "decision": {
              "outcome": "archive",
              "state": "succeeded",
              "operationId": "workbench-ui-legacy",
              "sourceFingerprint": "{{new string('a', 64)}}",
              "preparedAt": "2026-07-26T10:00:00Z",
              "preparedBy": "Robert",
              "confirmedAt": "2026-07-26T10:01:00Z",
              "confirmedBy": "Robert",
              "decidedAt": "2026-07-26T10:02:00Z",
              "reason": "The experiment disproved the direction.",
              "spawnedTaskKeys": []
            }
          }
          """);

        var item = Assert.Single(Service().List("Project", includeHistory: true)!.Items);

        Assert.True(item.Valid, item.Error);
        Assert.Equal("archived", item.Status); // still the flat v1 field, not a lifecycle projection
        Assert.Equal("archived", item.DecisionStage);
        Assert.Equal("workbench-ui-legacy", item.Decision!.OperationId);
    }

    [Fact]
    public void OwnsCanonicalPath_CoversBothDescriptorSchemas()
    {
        // The Wiki-classification archive bug is a property of workbench.json,
        // not of its version: the repository's Workbenches are overwhelmingly
        // schema v1, so a v2-only guard protected almost none of them.
        WriteWorkbench("legacy", "Legacy", "active", "2026-07-12T10:00:00Z");
        WriteSchemaTwoWorkbench("modern");
        Directory.CreateDirectory(Path.Combine(_root, "docs", "concepts"));
        File.WriteAllText(Path.Combine(_root, "docs", "concepts", "plain.md"), "# Plain\n");

        var service = Service();

        Assert.True(service.OwnsCanonicalPath("Project", "docs/workbenches/legacy/index.html"));
        Assert.True(service.OwnsCanonicalPath("Project", "workbenches/legacy/notes.md"));
        Assert.True(service.OwnsCanonicalPath("Project", "docs/workbenches/modern/index.html"));
        Assert.False(service.OwnsCanonicalPath("Project", "docs/concepts/plain.md"));
    }

    [Fact]
    public void LifecycleMergeKeepsSettledWorkbenchesVisibleForPulse()
    {
        WriteWorkbench("decision", "Decision", "decided", "2026-07-12T10:00:00Z");
        WriteWorkbench("documented", "Documented", "documented", "2026-07-11T11:00:00Z");
        WriteWorkbench("complete", "Complete", "archived", "2026-07-11T10:00:00Z");
        var catalogue = Service().List("Project", includeHistory: true)!;

        var merged = ProjectDocsService.MergeWorkbenchLifecycle(
            new WikiPulseLifecycle(true, null, 0, []), catalogue);

        Assert.Equal(3, merged.Count);
        Assert.Contains(merged.Items, item => item.WorkbenchId == "decision" && item.State == "decided");
        Assert.Contains(merged.Items, item => item.WorkbenchId == "documented" && item.State == "documented");
        Assert.Contains(merged.Items, item => item.WorkbenchId == "complete" && item.State == "done");
    }

    [Fact]
    public void Read_RejectsEscapingEntrypointAndDiscoversNamedLegacyPilot()
    {
        var dir = Path.Combine(_root, "docs", "workbenches", "escape");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "workbench.json"), """
          {"schemaVersion":1,"id":"escape","title":"Escape","summary":"Bad", "entrypoint":"../../../secret.html","status":"active","updatedAt":"2026-07-12T10:00:00Z"}
          """);
        Directory.CreateDirectory(Path.Combine(_root, "docs", "quality", "design"));
        File.WriteAllText(Path.Combine(_root, "docs", "quality", "design", "app-survey-2026-07-11.html"), "<h1>Survey</h1>");

        var service = Service();
        var catalogue = service.List("Project")!;
        Assert.Contains(catalogue.Items, x => x.Id == "escape" && !x.Valid);
        Assert.Contains(catalogue.Items, x => x.Id == "app-survey" && x.Valid);
        Assert.Null(service.Read("Project", "escape"));
        Assert.Equal("<h1>Survey</h1>", service.Read("Project", "app-survey")!.Html);
    }

    [SkippableFact]
    public void List_RejectsWorkbenchDirectorySymlinkThatEscapesRepository()
    {
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "index.html"), "<h1>Outside</h1>");
        File.WriteAllText(Path.Combine(_outside, "workbench.json"), """
          {"schemaVersion":1,"id":"linked","title":"Linked","summary":"Bad", "entrypoint":"index.html","status":"active","updatedAt":"2026-07-12T10:00:00Z"}
          """);
        var catalogueRoot = Path.Combine(_root, "docs", "workbenches");
        Directory.CreateDirectory(catalogueRoot);
        var link = Path.Combine(catalogueRoot, "linked");
        Skip.IfNot(TryCreateDirectoryLink(link, _outside),
            "Symbolic links and directory junctions are unavailable on this host.");

        var service = Service();
        var item = Assert.Single(service.List("Project")!.Items, candidate => candidate.Id == "linked");

        Assert.False(item.Valid);
        Assert.Contains("symbolic link", item.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(service.Read("Project", "linked"));
    }

    [Fact]
    public void List_RejectsHtmlOverTwentyMiBWithoutReadingIt()
    {
        var dir = Path.Combine(_root, "docs", "workbenches", "oversized");
        Directory.CreateDirectory(dir);
        using (var html = new FileStream(Path.Combine(dir, "index.html"), FileMode.Create, FileAccess.Write))
            html.SetLength(21L * 1024 * 1024);
        File.WriteAllText(Path.Combine(dir, "workbench.json"), """
          {"schemaVersion":1,"id":"oversized","title":"Oversized","summary":"Too large", "entrypoint":"index.html","status":"active","updatedAt":"2026-07-12T10:00:00Z"}
          """);

        var service = Service();
        var item = Assert.Single(service.List("Project")!.Items, candidate => candidate.Id == "oversized");

        Assert.False(item.Valid);
        Assert.Contains("20 MiB", item.Error);
        Assert.Null(service.Read("Project", "oversized"));
    }

    [Fact]
    public void Read_DoesNotLabelDirtyWorkingTreeBytesAsHeadRevision()
    {
        WriteWorkbench("provenance", "Provenance", "active", "2026-07-12T10:00:00Z");
        var service = Service();
        service.List("Project"); // persist the discovery key before establishing the clean revision
        RunGit("init");
        RunGit("config", "user.email", "tests@example.invalid");
        RunGit("config", "user.name", "Workbench Tests");
        RunGit("add", ".");
        RunGit("commit", "-m", "seed workbench");
        var head = RunGit("rev-parse", "HEAD").Trim();
        // Rebuild the Git boundary after the external test commit so this test
        // does not observe the short-lived pre-commit status cache.
        service = Service();

        var clean = service.Read("Project", "provenance")!;
        Assert.Equal(head, clean.Revision);
        Assert.False(clean.WorkingTreeModified);
        Assert.NotNull(clean.Fingerprint);

        File.AppendAllText(Path.Combine(_root, "docs", "workbenches", "provenance", "index.html"),
            "<p>Uncommitted bytes</p>");
        var dirty = service.Read("Project", "provenance")!;

        Assert.Null(dirty.Revision);
        Assert.True(dirty.WorkingTreeModified);
        Assert.NotEqual(clean.Fingerprint, dirty.Fingerprint);
        Assert.Contains("Uncommitted bytes", dirty.Html);
    }

    [Fact]
    public void ConfiguredWikiBranch_DrivesCatalogueAndDocumentRead_InsteadOfCheckout()
    {
        RunGit("init");
        RunGit("config", "user.email", "tests@example.invalid");
        RunGit("config", "user.name", "Workbench Tests");
        WriteWorkbench("checkout-only", "Checkout only", "active", "2026-07-12T10:00:00Z");
        SetWorkbenchKey("checkout-only", "PRO-W1");
        RunGit("add", ".");
        RunGit("commit", "-m", "seed checkout workbench");
        var checkoutBranch = RunGit("branch", "--show-current").Trim();

        RunGit("checkout", "-b", "wiki-source");
        Directory.Delete(Path.Combine(_root, "docs", "workbenches", "checkout-only"), recursive: true);
        WriteWorkbench("branch-only", "Branch only", "active", "2026-07-13T10:00:00Z");
        SetWorkbenchKey("branch-only", "PRO-W2");
        RunGit("add", ".");
        RunGit("commit", "-m", "seed branch workbench");
        var branchRevision = RunGit("rev-parse", "HEAD").Trim();
        RunGit("checkout", checkoutBranch);

        var (service, registry) = ServiceWithRegistry();
        var project = registry.FindByIdOrDisplayName("Project")!;
        registry.SetWikiSourceBranch(project.Id, "wiki-source");

        var catalogue = service.List("Project", includeHistory: true)!;
        var item = Assert.Single(catalogue.Items);
        var document = service.Read("Project", item.Id)!;

        Assert.Equal("branch-only", item.Id);
        Assert.DoesNotContain(catalogue.Items, candidate => candidate.Id == "checkout-only");
        Assert.Contains("Branch only", document.Html);
        Assert.Equal("wiki-source", document.Branch);
        Assert.Equal(branchRevision, document.Revision);
        Assert.False(document.WorkingTreeModified);
    }

    [Fact]
    public void List_AssignsStableProjectKeys_AboveExistingFloor_AndKeepsThemAcrossRename()
    {
        WriteWorkbench("alpha", "Alpha", "active", "2026-07-12T10:00:00Z");
        WriteWorkbench("beta", "Beta", "active", "2026-07-13T10:00:00Z");
        var betaPath = Path.Combine(_root, "docs", "workbenches", "beta", "workbench.json");
        var betaDescriptor = JsonNode.Parse(File.ReadAllText(betaPath))!.AsObject();
        betaDescriptor["key"] = "PRO-W7";
        betaDescriptor["relatedTaskKeys"] = new JsonArray("PRO-41");
        File.WriteAllText(betaPath, betaDescriptor.ToJsonString());

        var service = Service();
        var first = service.List("Project", includeHistory: true)!;
        var alpha = Assert.Single(first.Items, item => item.Id == "alpha");
        var beta = Assert.Single(first.Items, item => item.Id == "beta");
        Assert.Equal("PRO-W8", alpha.Key);
        Assert.Equal("PRO-W7", beta.Key);
        Assert.Equal(new[] { "PRO-41" }, beta.RelatedTaskKeys);

        var alphaPath = Path.Combine(_root, "docs", "workbenches", "alpha", "workbench.json");
        using (var assigned = JsonDocument.Parse(File.ReadAllText(alphaPath)))
            Assert.Equal("PRO-W8", assigned.RootElement.GetProperty("key").GetString());

        var renamedDir = Path.Combine(_root, "docs", "workbenches", "renamed-alpha");
        Directory.Move(Path.GetDirectoryName(alphaPath)!, renamedDir);
        var renamedPath = Path.Combine(renamedDir, "workbench.json");
        var renamedDescriptor = JsonNode.Parse(File.ReadAllText(renamedPath))!.AsObject();
        renamedDescriptor["id"] = "renamed-alpha";
        File.WriteAllText(renamedPath, renamedDescriptor.ToJsonString());

        var afterRename = service.List("Project", includeHistory: true)!;
        Assert.Equal("PRO-W8", Assert.Single(afterRename.Items,
            item => item.Id == "renamed-alpha").Key);

        WriteWorkbench("gamma", "Gamma", "active", "2026-07-14T10:00:00Z");
        var afterNewDiscovery = service.List("Project", includeHistory: true)!;
        Assert.Equal("PRO-W9", Assert.Single(afterNewDiscovery.Items,
            item => item.Id == "gamma").Key);
    }

    [Fact]
    public void List_AssignsMissingKeys_CommitsThemAndLeavesTheRepositoryClean()
    {
        WriteWorkbench("discovered", "Discovered", "active", "2026-08-10T08:00:00Z");
        RunGit("init", "-b", "develop");
        RunGit("config", "user.email", "tests@example.invalid");
        RunGit("config", "user.name", "Workbench Tests");
        RunGit("add", ".");
        RunGit("commit", "-m", "test: seed workbench without a key");
        var before = RunGit("rev-parse", "HEAD").Trim();
        var pushQueue = new WorkspaceArtifactPushQueue();

        var item = Assert.Single(Service(pushQueue).List("Project", includeHistory: true)!.Items);

        var after = RunGit("rev-parse", "HEAD").Trim();
        Assert.Equal("PRO-W1", item.Key);
        Assert.NotEqual(before, after);
        Assert.Equal("chore(workbench): assign document keys", RunGit("log", "-1", "--format=%s").Trim());
        Assert.Equal(string.Empty, RunGit("status", "--porcelain"));
        Assert.True(pushQueue.Reader.TryRead(out var push));
        Assert.Equal(after, push!.Sha);
        Assert.Equal("develop", push.TargetBranch);
        Assert.Equal("Project", push.Project);
    }

    [Fact]
    public void List_WhenTheManagedCommitFails_RestoresTheDescriptorAndLeavesTheRepositoryClean()
    {
        WriteWorkbench("discovered", "Discovered", "active", "2026-08-10T08:00:00Z");
        RunGit("init", "-b", "develop");
        RunGit("config", "user.email", "tests@example.invalid");
        RunGit("config", "user.name", "Workbench Tests");
        RunGit("add", ".");
        RunGit("commit", "-m", "test: seed workbench without a key");
        var before = RunGit("rev-parse", "HEAD").Trim();
        var hook = Path.Combine(_root, ".git", "hooks", "pre-commit");
        File.WriteAllText(hook, "#!/bin/sh\nexit 1\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                hook,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var item = Assert.Single(Service().List("Project", includeHistory: true)!.Items);

        Assert.False(item.Valid);
        Assert.Contains("key is required", item.Error);
        Assert.Equal(before, RunGit("rev-parse", "HEAD").Trim());
        Assert.Equal(string.Empty, RunGit("status", "--porcelain"));
        using var descriptor = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(_root, "docs", "workbenches", "discovered", "workbench.json")));
        Assert.False(descriptor.RootElement.TryGetProperty("key", out _));
    }

    private void WriteWorkbench(
        string id,
        string title,
        string status,
        string updatedAt,
        string? pattern = null)
    {
        var dir = Path.Combine(_root, "docs", "workbenches", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.html"), $"<h1>{title}</h1>");
        var patternProperty = pattern == null ? "" : $",\"pattern\":\"{pattern}\"";
        File.WriteAllText(Path.Combine(dir, "workbench.json"), $$"""
          {"schemaVersion":1,"id":"{{id}}","title":"{{title}}","summary":"Question", "entrypoint":"index.html","status":"{{status}}","phase":"testing","updatedAt":"{{updatedAt}}"{{patternProperty}}}
          """);
    }

    private void WriteSchemaTwoWorkbench(string id)
    {
        var dir = Path.Combine(_root, "docs", "workbenches", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.html"), $"<h1>{id}</h1>");
        File.WriteAllText(Path.Combine(dir, "workbench.json"), $$"""
          {
            "schemaVersion": 2,
            "id": "{{id}}",
            "title": "Modern",
            "summary": "Question",
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

    private WorkbenchCatalogueService Service(WorkspaceArtifactPushQueue? pushQueue = null)
        => ServiceWithRegistry(pushQueue).Service;

    private (WorkbenchCatalogueService Service, ProjectRegistry Registry) ServiceWithRegistry(
        WorkspaceArtifactPushQueue? pushQueue = null)
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
        registry.EnsureProjectForStorage(
            Path.Combine(_root, ".orchestrator", "jobs"),
            "Project",
            DefaultWorkspace.Id);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var mutations = new ManagedRepositoryMutationService(
            git,
            pushQueue: pushQueue,
            logger: NullLogger<ManagedRepositoryMutationService>.Instance);
        return (
            new WorkbenchCatalogueService(scanner, registry, git, repositoryMutations: mutations),
            registry);
    }

    private void SetWorkbenchKey(string id, string key)
    {
        var path = Path.Combine(_root, "docs", "workbenches", id, "workbench.json");
        var descriptor = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        descriptor["key"] = key;
        File.WriteAllText(path, descriptor.ToJsonString());
    }

    private string RunGit(params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error}");
        return output;
    }

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            if (!OperatingSystem.IsWindows()) return false;
        }

        var start = new ProcessStartInfo("cmd.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "/c", "mklink", "/J", link, target })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(link);
    }
}
