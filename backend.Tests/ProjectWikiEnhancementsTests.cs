using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Covers the wiki-enhancement backend surface: frontmatter provenance
/// parsing, the user-organisation manifest round-trip + sanitisation, the
/// model-trailer extraction, and per-file git history. The pure parsers are
/// asserted directly; the manifest + git lookups use a temp project / repo.
/// </summary>
public class ProjectWikiEnhancementsTests : IDisposable
{
    private readonly string _tempDir;

    public ProjectWikiEnhancementsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "wiki-enh-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* best-effort */ }
    }

    // ---- ParseWikiMetadata (pure) ----

    [Fact]
    public void ParseWikiMetadata_ExtractsProvenanceFields()
    {
        var md = "---\n"
            + "model: Claude Opus 4.8\n"
            + "last-distilled: 2026-06-09T10:00:00Z\n"
            + "status: accept\n"
            + "task-key: ASS-1709\n"
            + "run-count: 3\n"
            + "why: \"distilled after review\"\n"
            + "---\n\n# Title\nbody";

        var meta = ProjectDocsService.ParseWikiMetadata(md);

        Assert.True(meta.HasFrontmatter);
        Assert.Equal("Claude Opus 4.8", meta.Model);
        Assert.Equal("2026-06-09T10:00:00Z", meta.UpdatedAt);
        Assert.Equal("accept", meta.Status);
        Assert.Equal("ASS-1709", meta.TaskKey);
        Assert.Equal("3", meta.RunCount);
        Assert.Equal("distilled after review", meta.Reason);
    }

    [Fact]
    public void ParseWikiMetadata_NoFrontmatter_ReturnsEmpty()
    {
        var meta = ProjectDocsService.ParseWikiMetadata("# Just a heading\n\nNo frontmatter here.");

        Assert.False(meta.HasFrontmatter);
        Assert.Null(meta.Model);
        Assert.Null(meta.UpdatedAt);
    }

    [Fact]
    public void ParseWikiMetadata_FallsBackToReasonSynonyms()
    {
        var meta = ProjectDocsService.ParseWikiMetadata("---\nsummary: keeps the rationale\n---\n# t");
        Assert.Equal("keeps the rationale", meta.Reason);
    }

    // ---- GetWikiTree (physical docs/ hierarchy) ----

    [Fact]
    public void GetWikiTree_ReflectsPhysicalStructure_FoldersFirst_IncludesHtml_StripsOrderPrefix()
    {
        var projectRoot = Path.Combine(_tempDir, "tree-proj");
        var docsDir = Path.Combine(projectRoot, "docs");
        Directory.CreateDirectory(Path.Combine(docsDir, "01-concepts"));
        File.WriteAllText(Path.Combine(docsDir, "README.md"), "# Index\n");
        // Wiki home configuration lives in the docs/app/ code-contract area, not a
        // page: the whole app/ subtree must never surface as a tree node.
        Directory.CreateDirectory(Path.Combine(docsDir, "app", "config"));
        File.WriteAllText(Path.Combine(docsDir, "app", "config", "home.json"), "{ \"sections\": [] }");
        File.WriteAllText(Path.Combine(docsDir, "01-concepts", "10-overview.md"), "# Overview\n");
        File.WriteAllText(Path.Combine(docsDir, "01-concepts", "page.html"), "<h1>HTML page</h1>");
        File.WriteAllText(Path.Combine(docsDir, "01-concepts", "page.metadata.json"),
            "{ \"title\": \"Page metadata\", \"drift\": { \"grade\": \"B\" } }");
        File.WriteAllText(Path.Combine(docsDir, "01-concepts", "10-overview.md.report.html"),
            "<h1>Overview report</h1>");
        File.WriteAllText(Path.Combine(docsDir, "01-concepts", "10-overview.md.meta.json"),
            """
            {
              "$schema": "https://agent-taskboard.local/schemas/wiki-document-companion.schema.json",
              "schemaVersion": "wiki-document-companion/v1",
              "title": "Concept overview metadata",
              "source": {
                "path": "docs/01-concepts/10-overview.md",
                "type": "markdown",
                "fingerprint": {
                  "algorithm": "sha256",
                  "hash": "0000000000000000000000000000000000000000000000000000000000000000",
                  "sizeBytes": 1,
                  "lineCount": 1,
                  "capturedAt": "2026-06-12T08:30:00Z"
                }
              },
              "report": {
                "path": "docs/01-concepts/10-overview.md.report.html",
                "generatedAt": "2026-06-12T08:30:00Z",
                "generator": "scripts/wiki/generate-companion-metadata.mjs",
                "template": "wiki-document-companion-report/v1"
              },
              "classification": {
                "owner": "architecture",
                "documentMode": "documentation",
                "temporalState": "present",
                "implementationState": "implemented"
              },
              "review": {
                "date": "2026-06-12",
                "method": "unit test",
                "model": "codex",
                "sourceFingerprint": {
                  "algorithm": "sha256",
                  "hash": "0000000000000000000000000000000000000000000000000000000000000000",
                  "sizeBytes": 1,
                  "lineCount": 1,
                  "capturedAt": "2026-06-12T08:30:00Z"
                },
                "sourceChangedSinceReview": false
              },
              "drift": { "grade": "B", "hasDrift": true, "score": 0.24, "summary": "Light sample drift." },
              "axes": {
                "architectureAlignment": "high",
                "implementationAlignment": "medium",
                "freshness": "medium",
                "operatorUsefulness": "high"
              },
              "duplicates": { "suspected": false, "groupSize": 1, "similarTo": [] },
              "findings": [
                { "id": "drift-summary", "severity": "warn", "axis": "drift", "summary": "Light sample drift." }
              ],
              "nextAction": "Refresh if source changes."
            }
            """);

        var docs = BuildDocsService(("Tree", projectRoot));
        var tree = docs.GetWikiTree("Tree");

        Assert.NotNull(tree);
        Assert.True(tree!.Exists);
        // Folders sort before the loose README file; home.json (config) is hidden.
        Assert.Equal(2, tree.Root.Count);
        Assert.Equal("folder", tree.Root[0].Type);
        Assert.Equal("concepts", tree.Root[0].Title); // NN- prefix stripped
        Assert.Equal("01-concepts", tree.Root[0].Name);
        Assert.Equal("README.md", tree.Root[1].Name);
        Assert.DoesNotContain(tree.Root, n => n.Name == "home.json");
        Assert.DoesNotContain(tree.Root, n => n.Name == "app"); // docs/app/ subtree stays hidden

        var folder = tree.Root[0];
        Assert.Equal(3, folder.Children.Count);
        // Markdown, HTML, and JSON surface; md '10-overview' sorts by its numeric prefix.
        var types = folder.Children.Select(c => c.Type).ToHashSet();
        Assert.Contains("md", types);
        Assert.Contains("html", types);
        Assert.Contains("json", types);
        Assert.Equal("01-concepts/page.html", folder.Children.Single(c => c.Type == "html").RelPath);
        Assert.Equal("Page metadata", folder.Children.Single(c => c.Type == "json").Title);
        var overview = folder.Children.Single(c => c.Name == "10-overview.md");
        Assert.NotNull(overview.Metadata);
        Assert.Equal("implemented", overview.Metadata!.ImplementationState);
        Assert.True(overview.Metadata.HasDrift);
        Assert.Equal("B", overview.Metadata.DriftGrade);
        Assert.Equal("medium", overview.Metadata.Quality);
        Assert.False(overview.Metadata.DuplicateSuspected);
        Assert.Equal("01-concepts/10-overview.md.report.html", overview.Metadata.ReportPath);
        Assert.Equal("01-concepts/10-overview.md.meta.json", overview.Metadata.CompanionPath);
        Assert.True(overview.Metadata.SourceChangedSinceReview);
        Assert.Equal(1, overview.Metadata.FindingsCount);
    }

    [Fact]
    public void GetWikiTree_NoDocsFolder_ReturnsEmptyButValid()
    {
        var projectRoot = Path.Combine(_tempDir, "no-docs-proj");
        Directory.CreateDirectory(projectRoot);
        var docs = BuildDocsService(("NoDocs", projectRoot));

        var tree = docs.GetWikiTree("NoDocs");

        Assert.NotNull(tree);
        Assert.False(tree!.Exists);
        Assert.Empty(tree.Root);
    }

    [Fact]
    public void GetWikiTree_ProjectsRuntimeAgentReadsWithoutTrackedCompanion()
    {
        var projectRoot = Path.Combine(_tempDir, "runtime-reads-tree");
        var docsDir = Path.Combine(projectRoot, "docs");
        Directory.CreateDirectory(docsDir);
        File.WriteAllText(Path.Combine(docsDir, "page.md"), "# Page\n");
        new WikiAgentReadStore().Increment(
            docsDir,
            "page.md",
            new DateTime(2026, 8, 8, 10, 0, 0, DateTimeKind.Utc),
            "AGT-2507");

        var page = Assert.Single(BuildDocsService(("RuntimeReads", projectRoot)).GetWikiTree("RuntimeReads")!.Root);

        Assert.Equal(1, page.Metadata?.AgentReads?.Total);
        Assert.Equal("AGT-2507", Assert.Single(page.Metadata!.AgentReads!.Recent).TaskKey);
        Assert.False(File.Exists(Path.Combine(docsDir, "page.md.meta.json")));
    }

    [Fact]
    public void GetWikiTree_UnknownProject_ReturnsNull()
    {
        var docs = BuildDocsService(("Known", Path.Combine(_tempDir, "known")));
        Assert.Null(docs.GetWikiTree("Nope"));
    }

    // ---- Classification (sidecar `classification` block + folder defaults) ----

    [Fact]
    public void GetWikiTree_ClassificationComesFromSidecar_WithFolderDefaultTypeFallback()
    {
        var projectRoot = Path.Combine(_tempDir, "class-proj");
        var docsDir = Path.Combine(projectRoot, "docs");
        Directory.CreateDirectory(Path.Combine(docsDir, "concepts"));
        Directory.CreateDirectory(Path.Combine(docsDir, "system", "domains"));
        File.WriteAllText(Path.Combine(docsDir, "concepts", "old.md"), "# Old concept\n");
        File.WriteAllText(Path.Combine(docsDir, "concepts", "old.md.meta.json"),
            """
            {
              "$schema": "https://agent-taskboard.local/schemas/wiki-document-companion.schema.json",
              "schemaVersion": "wiki-document-companion/v1",
              "source": { "path": "docs/concepts/old.md" },
              "classification": {
                "owner": "concepts",
                "status": "ueberholt",
                "supersededBy": "concepts/new.md",
                "type": "konzept",
                "analyzedAt": "2026-07-18"
              }
            }
            """);
        // Sidecar without a `type`: the folder default (system/domains -> domain-map) fills it.
        File.WriteAllText(Path.Combine(docsDir, "system", "domains", "aging.md"), "# Aging analysis\n");
        File.WriteAllText(Path.Combine(docsDir, "system", "domains", "aging.md.meta.json"),
            """
            {
              "source": { "path": "docs/system/domains/aging.md" },
              "classification": { "status": "veraltet", "analyzedAt": "2026-07-18" }
            }
            """);

        var tree = BuildDocsService(("Class", projectRoot)).GetWikiTree("Class");

        var concept = tree!.Root.Single(n => n.Name == "concepts").Children.Single(n => n.Name == "old.md");
        Assert.NotNull(concept.Classification);
        Assert.Equal("ueberholt", concept.Classification!.Status);
        Assert.Equal("concepts/new.md", concept.Classification.SupersededBy);
        Assert.Equal("konzept", concept.Classification.Type);
        Assert.Equal("2026-07-18", concept.Classification.AnalyzedAt);

        var aging = tree.Root.Single(n => n.Name == "system")
            .Children.Single(n => n.Name == "domains")
            .Children.Single(n => n.Name == "aging.md");
        Assert.NotNull(aging.Classification);
        Assert.Equal("veraltet", aging.Classification!.Status);
        Assert.Null(aging.Classification.SupersededBy);
        Assert.Equal("domain-map", aging.Classification.Type);
    }

    [Fact]
    public void GetWikiTree_ClassificationFolderDefaults_ApplyWithoutSidecar()
    {
        var projectRoot = Path.Combine(_tempDir, "class-default-proj");
        var docsDir = Path.Combine(projectRoot, "docs");
        Directory.CreateDirectory(Path.Combine(docsDir, "proposals", "2026-07-11"));
        Directory.CreateDirectory(Path.Combine(docsDir, "architecture", "decisions", "proposed"));
        Directory.CreateDirectory(Path.Combine(docsDir, "operations"));
        File.WriteAllText(Path.Combine(docsDir, "proposals", "2026-07-11", "finding.md"), "# Finding\n");
        File.WriteAllText(
            Path.Combine(docsDir, "architecture", "decisions", "proposed", "adr-0099-example.md"), "# ADR\n");
        File.WriteAllText(Path.Combine(docsDir, "operations", "runbook.md"), "# Runbook\n");

        var tree = BuildDocsService(("ClassDefault", projectRoot)).GetWikiTree("ClassDefault");

        var proposal = tree!.Root.Single(n => n.Name == "proposals")
            .Children.Single(n => n.Name == "2026-07-11").Children.Single(n => n.Name == "finding.md");
        Assert.NotNull(proposal.Classification);
        Assert.Equal("proposal", proposal.Classification!.Type);
        Assert.Null(proposal.Classification.Status);

        var adr = tree.Root.Single(n => n.Name == "architecture")
            .Children.Single(n => n.Name == "decisions")
            .Children.Single(n => n.Name == "proposed")
            .Children.Single(n => n.Name == "adr-0099-example.md");
        Assert.Equal("adr", adr.Classification!.Type);

        // No sidecar + no curated folder default still receives the canonical
        // interactive page kind so every page can render a type icon.
        var runbook = tree.Root.Single(n => n.Name == "operations").Children.Single(n => n.Name == "runbook.md");
        Assert.NotNull(runbook.Classification);
        Assert.Equal("doc", runbook.Classification!.PageType);
        // Folder nodes never carry a classification.
        Assert.Null(tree.Root.Single(n => n.Name == "proposals").Classification);
    }

    [Theory]
    [InlineData("operations/common-problems/x/main.md", "generiert")]
    [InlineData("concepts/proposals/2026-07-11/a.md", "proposal")]
    [InlineData("system/domains/tasks.md", "domain-map")]
    [InlineData("system/contracts/filesystem.md", "contract")]
    [InlineData("system/architecture/decisions/adr-archive.md", "adr")]
    [InlineData("concepts/mockups/decoupled-lifecycles.html", "mockup")]
    // Promoted mockup families keep the mockup type despite losing the /mockups/ segment.
    [InlineData("concepts/project-urls/ui.html", "mockup")]
    [InlineData("concepts/project-overview-dashboard/README.md", "mockup")]
    [InlineData("concepts/task-processing-pipeline/task-timeline.md", "mockup")]
    [InlineData("concepts/task-detail-header-state-actions/README.md", "mockup")]
    [InlineData("system/architecture/model.md", null)]
    [InlineData("concepts/idea.md", null)]
    // Workbench pages are no longer folder-typed (discovered via workbench.json,
    // theme-distributed); research folder is dissolved - both fall through to null.
    [InlineData("operations/haertung-verteilte-ausfuehrung/index.html", null)]
    [InlineData("quality/agent-eval-set-split-measurement-2026-06.md", null)]
    public void DefaultClassificationType_MapsAgreedFolders(string relPath, string? expected)
    {
        Assert.Equal(expected, ProjectDocsService.DefaultClassificationType(relPath));
    }

    [Theory]
    [InlineData("README.md", null, "doc")]
    [InlineData("concepts/action-bar.md", "konzept", "concept")]
    [InlineData("operations/incidents/history.md", null, "incident")]
    [InlineData("reports/quality.md", null, "report")]
    [InlineData("quality/action-bar/index.html", "workbench", "workbench")]
    public void CanonicalPageType_MapsMetaAndPathFamilies(
        string relPath, string? curatedType, string expected)
    {
        Assert.Equal(expected, ProjectDocsService.CanonicalPageType(relPath, curatedType));
    }

    [Fact]
    public void GetWikiTree_WorkbenchRegistrationTypesEntryPage()
    {
        var projectRoot = Path.Combine(_tempDir, "registered-workbench");
        var workbenchDir = Path.Combine(projectRoot, "docs", "quality", "action-bar");
        Directory.CreateDirectory(workbenchDir);
        File.WriteAllText(Path.Combine(workbenchDir, "index.html"), "<h1>Action bar</h1>");
        File.WriteAllText(Path.Combine(workbenchDir, "workbench.json"),
            """{ "entrypoint": "index.html" }""");

        var tree = BuildDocsService(("Workbench", projectRoot)).GetWikiTree("Workbench");
        var entry = tree!.Root.Single(node => node.Name == "quality")
            .Children.Single(node => node.Name == "action-bar")
            .Children.Single(node => node.Name == "index.html");

        Assert.Equal("workbench", entry.Classification!.PageType);
    }

    [Fact]
    public void SetWikiClassificationStatus_PreservesPageAndWritesArchivedCompanion()
    {
        var projectRoot = Path.Combine(_tempDir, "archive-page");
        var pagePath = Path.Combine(projectRoot, "docs", "guide.md");
        Directory.CreateDirectory(Path.GetDirectoryName(pagePath)!);
        File.WriteAllText(pagePath, "# Guide\n\nRetained content.\n");
        var docs = BuildDocsService(("Archive", projectRoot));

        var result = docs.SetWikiClassificationStatus(
            "Archive", "guide.md", "archived", new WikiCompanionStore());

        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(pagePath));
        var companion = File.ReadAllText(pagePath + ".meta.json");
        Assert.Contains("\"status\": \"archived\"", companion);
        Assert.Equal("archived", docs.GetWikiTree("Archive")!.Root.Single().Classification!.Status);
    }

    [Fact]
    public void SetWikiHomePin_AddsMovesAndRemovesSharedCuratedEntry()
    {
        var projectRoot = Path.Combine(_tempDir, "pin-home");
        var docsRoot = Path.Combine(projectRoot, "docs");
        var configDir = Path.Combine(docsRoot, "app", "config");
        Directory.CreateDirectory(configDir);
        Directory.CreateDirectory(Path.Combine(docsRoot, "concepts"));
        File.WriteAllText(
            Path.Combine(docsRoot, "concepts", "action-bar.md"),
            "# Action bar\n\nA bidirectional page interface.\n");
        File.WriteAllText(
            Path.Combine(configDir, "home.json"),
            """
            {
              "sections": [
                { "title": "Start", "links": [] },
                { "title": "Concepts", "links": [] }
              ]
            }
            """);
        var docs = BuildDocsService(("Pin", projectRoot));

        var added = docs.SetWikiHomePin(
            "Pin", "concepts/action-bar.md", true, "Start", "Action bar", "Shared entry.");
        Assert.True(added.Success, added.Error);
        var first = docs.GetWikiHome("Pin")!;
        Assert.Equal("concepts/action-bar.md", first.Sections[0].Links.Single().RelPath);
        Assert.Equal("Shared entry.", first.Sections[0].Links.Single().Note);

        var moved = docs.SetWikiHomePin(
            "Pin", "concepts/action-bar.md", true, "Concepts", "Page actions", null);
        Assert.True(moved.Success, moved.Error);
        var second = docs.GetWikiHome("Pin")!;
        Assert.Empty(second.Sections[0].Links);
        Assert.Equal("Page actions", second.Sections[1].Links.Single().Label);

        var removed = docs.SetWikiHomePin(
            "Pin", "concepts/action-bar.md", false, null, null, null);
        Assert.True(removed.Success, removed.Error);
        Assert.All(docs.GetWikiHome("Pin")!.Sections, section => Assert.Empty(section.Links));
        Assert.True(File.Exists(Path.Combine(docsRoot, "concepts", "action-bar.md")));
    }

    [Fact]
    public void SetWikiHomePin_RejectsUnknownSectionWithoutChangingConfig()
    {
        var projectRoot = Path.Combine(_tempDir, "pin-home-invalid");
        var docsRoot = Path.Combine(projectRoot, "docs");
        var configDir = Path.Combine(docsRoot, "app", "config");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(docsRoot, "guide.md"), "# Guide\n");
        var homePath = Path.Combine(configDir, "home.json");
        File.WriteAllText(homePath, """{ "sections": [{ "title": "Start", "links": [] }] }""");
        var before = File.ReadAllText(homePath);
        var docs = BuildDocsService(("Pin", projectRoot));

        var result = docs.SetWikiHomePin("Pin", "guide.md", true, "Missing", "Guide", null);

        Assert.False(result.Success);
        Assert.Equal(before, File.ReadAllText(homePath));
    }

    // ---- CreateWikiPage / CreateWikiFolder + commit ----

    [Fact]
    public void CreateWikiPage_WritesFile_AndCommitPathsRecordsIt()
    {
        var repoRoot = Path.Combine(_tempDir, "create-repo");
        Directory.CreateDirectory(repoRoot);
        RunGit(repoRoot, "init -q -b main");
        RunGit(repoRoot, "config user.email test@example.com");
        RunGit(repoRoot, "config user.name test");

        var docs = BuildDocsService(("Create", repoRoot));
        var git = BuildGitService(("Create", repoRoot));

        var result = docs.CreateWikiPage("Create", "guide.md", null);
        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(repoRoot, "docs", "guide.md")));

        var commit = git.CommitPaths(repoRoot, "wiki: create docs/guide.md", new[] { "docs/guide.md" });
        Assert.True(commit.Success, commit.Error);

        var history = git.GetFileHistory(repoRoot, "docs/guide.md");
        Assert.Single(history);
        Assert.Equal("wiki: create docs/guide.md", history[0].Subject);
    }

    [Fact]
    public void CreateWikiPage_RejectsExistingFile_AndNonDocExtension()
    {
        var projectRoot = Path.Combine(_tempDir, "reject-proj");
        Directory.CreateDirectory(Path.Combine(projectRoot, "docs"));
        File.WriteAllText(Path.Combine(projectRoot, "docs", "exists.md"), "# x\n");
        var docs = BuildDocsService(("Reject", projectRoot));

        Assert.False(docs.CreateWikiPage("Reject", "exists.md", null).Success);
        Assert.False(docs.CreateWikiPage("Reject", "notes.txt", null).Success);
        Assert.True(docs.CreateWikiPage("Reject", "meta.json", null).Success);
    }

    [Fact]
    public void WriteWikiFile_UpdatesExistingDoc_AndReportsNoop()
    {
        var projectRoot = Path.Combine(_tempDir, "write-proj");
        var docPath = Path.Combine(projectRoot, "docs", "guide.md");
        Directory.CreateDirectory(Path.GetDirectoryName(docPath)!);
        File.WriteAllText(docPath, "# Guide\n");
        var docs = BuildDocsService(("Write", projectRoot));

        var changed = docs.WriteWikiFile("Write", "guide.md", "# Guide\n\nUpdated.\n");
        Assert.True(changed.Success);
        Assert.True(changed.Changed);
        Assert.Equal("# Guide\n\nUpdated.\n", File.ReadAllText(docPath));

        var unchanged = docs.WriteWikiFile("Write", "guide.md", "# Guide\n\nUpdated.\n");
        Assert.True(unchanged.Success);
        Assert.False(unchanged.Changed);
    }

    [Fact]
    public void CreateWikiFolder_SeedsGitkeep()
    {
        var projectRoot = Path.Combine(_tempDir, "folder-proj");
        Directory.CreateDirectory(projectRoot);
        var docs = BuildDocsService(("Folder", projectRoot));

        var result = docs.CreateWikiFolder("Folder", "concepts");
        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(projectRoot, "docs", "concepts", ".gitkeep")));
    }

    // ---- MoveAndCommit / RemoveAndCommit / GetFileAtCommit (real git) ----

    [Fact]
    public void MoveAndCommit_RenamesTrackedDoc_AndCommits()
    {
        var repoRoot = Path.Combine(_tempDir, "move-repo");
        var docPath = Path.Combine(repoRoot, "docs", "old.md");
        Directory.CreateDirectory(Path.GetDirectoryName(docPath)!);
        RunGit(repoRoot, "init -q -b main");
        RunGit(repoRoot, "config user.email test@example.com");
        RunGit(repoRoot, "config user.name test");
        File.WriteAllText(docPath, "# Doc\n");
        RunGit(repoRoot, "add -A");
        RunGitArgs(repoRoot, "commit", "-q", "-m", "seed");

        var git = BuildGitService(("Move", repoRoot));
        var commit = git.MoveAndCommit(repoRoot, "docs/old.md", "docs/new.md", "wiki: move docs/old.md -> docs/new.md");

        Assert.True(commit.Success, commit.Error);
        Assert.False(File.Exists(docPath));
        Assert.True(File.Exists(Path.Combine(repoRoot, "docs", "new.md")));
    }

    [Fact]
    public void RemoveAndCommit_DeletesTrackedDoc_AndCommits()
    {
        var repoRoot = Path.Combine(_tempDir, "remove-repo");
        var docPath = Path.Combine(repoRoot, "docs", "gone.md");
        Directory.CreateDirectory(Path.GetDirectoryName(docPath)!);
        RunGit(repoRoot, "init -q -b main");
        RunGit(repoRoot, "config user.email test@example.com");
        RunGit(repoRoot, "config user.name test");
        File.WriteAllText(docPath, "# Doc\n");
        RunGit(repoRoot, "add -A");
        RunGitArgs(repoRoot, "commit", "-q", "-m", "seed");

        var git = BuildGitService(("Remove", repoRoot));
        var commit = git.RemoveAndCommit(repoRoot, "docs/gone.md", "wiki: delete docs/gone.md");

        Assert.True(commit.Success, commit.Error);
        Assert.False(File.Exists(docPath));
    }

    [Fact]
    public void GetFileAtCommit_ReturnsHistoricContent()
    {
        var repoRoot = Path.Combine(_tempDir, "revision-repo");
        var docPath = Path.Combine(repoRoot, "docs", "note.md");
        Directory.CreateDirectory(Path.GetDirectoryName(docPath)!);
        RunGit(repoRoot, "init -q -b main");
        RunGit(repoRoot, "config user.email test@example.com");
        RunGit(repoRoot, "config user.name test");
        File.WriteAllText(docPath, "# Note\nfirst");
        RunGit(repoRoot, "add -A");
        RunGitArgs(repoRoot, "commit", "-q", "-m", "create note");

        var git = BuildGitService(("Revision", repoRoot));
        var history = git.GetFileHistory(repoRoot, "docs/note.md");
        Assert.Single(history);

        File.WriteAllText(docPath, "# Note\nsecond");
        RunGit(repoRoot, "add -A");
        RunGitArgs(repoRoot, "commit", "-q", "-m", "update note");

        var old = git.GetFileAtCommit(repoRoot, history[0].Sha, "docs/note.md");
        Assert.NotNull(old);
        Assert.Contains("first", old);
        Assert.DoesNotContain("second", old);
    }

    // ---- ParseModelTrailer (pure) ----

    [Theory]
    [InlineData("Claude Opus 4.8 <noreply@anthropic.com>", "Claude Opus 4.8")]
    [InlineData("Codex <x@y.z>\nClaude <a@b.c>", "Codex")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void ParseModelTrailer_StripsAddress_TakesFirst(string input, string? expected)
    {
        Assert.Equal(expected, GitService.ParseModelTrailer(input));
    }

    // ---- GetFileHistory + GetLatestModelForPath (real git) ----

    [Fact]
    public void GetFileHistory_ReturnsCommitsNewestFirst_AndModelFromTrailer()
    {
        var repoRoot = Path.Combine(_tempDir, "repo");
        var docPath = Path.Combine(repoRoot, "docs", "note.md");
        Directory.CreateDirectory(Path.GetDirectoryName(docPath)!);

        RunGit(repoRoot, "init -q -b main");
        RunGit(repoRoot, "config user.email test@example.com");
        RunGit(repoRoot, "config user.name test");

        File.WriteAllText(docPath, "# Note\nfirst");
        RunGit(repoRoot, "add -A");
        RunGitArgs(repoRoot, "commit", "-q", "-m", "create note");

        File.WriteAllText(docPath, "# Note\nsecond");
        RunGit(repoRoot, "add -A");
        RunGitArgs(repoRoot, "commit", "-q", "-m", "update note",
            "-m", "Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>");

        var git = BuildGitService(("Repo", repoRoot));

        var history = git.GetFileHistory(repoRoot, "docs/note.md");
        Assert.Equal(2, history.Count);
        Assert.Equal("update note", history[0].Subject); // newest first
        Assert.Equal("create note", history[1].Subject);

        var model = git.GetLatestModelForPath(repoRoot, "docs/note.md");
        Assert.Equal("Claude Opus 4.8", model);
    }

    [Fact]
    public void GetFileHistory_UnknownPath_ReturnsEmpty()
    {
        var repoRoot = Path.Combine(_tempDir, "repo2");
        Directory.CreateDirectory(repoRoot);
        RunGit(repoRoot, "init -q -b main");
        RunGit(repoRoot, "config user.email test@example.com");
        RunGit(repoRoot, "config user.name test");
        File.WriteAllText(Path.Combine(repoRoot, "README.md"), "seed");
        RunGit(repoRoot, "add -A");
        RunGitArgs(repoRoot, "commit", "-q", "-m", "seed");

        var git = BuildGitService(("Repo2", repoRoot));
        Assert.Empty(git.GetFileHistory(repoRoot, "docs/does-not-exist.md"));
    }

    // ---- ParseRecentEdits (pure) ----

    [Fact]
    public void ParseRecentEdits_DedupesByPath_NewestCommitWins_RespectsLimit()
    {
        const char RS = '\x1e';
        const char US = '\x1f';
        // Two records (newest first). docs/a.md appears in both; it must be
        // attributed to the newer commit only. docs/b.md only in the older one.
        var output =
            $"{RS}sha2{US}s2{US}2026-06-20T10:00:00Z{US}Alice{US}update a\ndocs/a.md\n" +
            $"{RS}sha1{US}s1{US}2026-06-10T09:00:00Z{US}Bob{US}seed\ndocs/a.md\ndocs/b.md\n";

        var edits = GitService.ParseRecentEdits(output, limit: 10);

        Assert.Equal(2, edits.Count);
        Assert.Equal("docs/a.md", edits[0].RepoRelPath);
        Assert.Equal("sha2", edits[0].Sha);
        Assert.Equal("Alice", edits[0].Author);
        Assert.Equal("docs/b.md", edits[1].RepoRelPath);
        Assert.Equal("Bob", edits[1].Author);

        var capped = GitService.ParseRecentEdits(output, limit: 1);
        Assert.Single(capped);
        Assert.Equal("docs/a.md", capped[0].RepoRelPath);
    }

    // ---- GetWikiRecentEdits (real git) ----

    [Fact]
    public void GetWikiRecentEdits_ReturnsNewestFirst_WithAuthorAndTitle_FiltersNonDocsAndDeletions()
    {
        var repoRoot = Path.Combine(_tempDir, "recent");
        var docsDir = Path.Combine(repoRoot, "docs");
        Directory.CreateDirectory(docsDir);

        RunGit(repoRoot, "init -q -b main");
        RunGit(repoRoot, "config user.email test@example.com");
        RunGit(repoRoot, "config user.name test");

        // Commit 1 (oldest): two docs + a companion sidecar + a non-doc file.
        File.WriteAllText(Path.Combine(docsDir, "alpha.md"), "# Alpha Title\nbody");
        File.WriteAllText(Path.Combine(docsDir, "beta.md"), "# Beta Title\nbody");
        File.WriteAllText(Path.Combine(docsDir, "alpha.md.meta.json"), "{}");
        File.WriteAllText(Path.Combine(repoRoot, "notes.txt"), "ignore me");
        RunGit(repoRoot, "add -A");
        RunGitArgs(repoRoot, "commit", "-q", "-m", "seed docs");

        // Commit 2: add a doc that we then delete (must not appear).
        File.WriteAllText(Path.Combine(docsDir, "ghost.md"), "# Ghost\n");
        RunGit(repoRoot, "add -A");
        RunGitArgs(repoRoot, "commit", "-q", "-m", "add ghost");
        File.Delete(Path.Combine(docsDir, "ghost.md"));
        RunGit(repoRoot, "add -A");
        RunGitArgs(repoRoot, "commit", "-q", "-m", "remove ghost");

        // Commit 3 (newest): touch beta.md so it sorts first.
        File.WriteAllText(Path.Combine(docsDir, "beta.md"), "# Beta Title\nupdated");
        RunGit(repoRoot, "add -A");
        RunGitArgs(repoRoot, "commit", "-q", "-m", "update beta");

        var docs = BuildDocsService(("Recent", repoRoot));
        var git = BuildGitService(("Recent", repoRoot));

        var recent = docs.GetWikiRecentEdits("Recent", git, 10);

        Assert.NotNull(recent);
        Assert.True(recent!.Exists);
        Assert.Equal(2, recent.Edits.Count); // alpha + beta; no companion, txt, ghost
        Assert.Equal("beta.md", recent.Edits[0].RelPath); // newest first
        Assert.Equal("Beta Title", recent.Edits[0].Title);
        Assert.Equal("test", recent.Edits[0].Author);
        Assert.Equal("alpha.md", recent.Edits[1].RelPath);
        Assert.Equal("Alpha Title", recent.Edits[1].Title);
    }

    [Fact]
    public void WriteWikiFile_ConfiguredBranchSource_IsRejectedWithoutTouchingCheckout()
    {
        var repoRoot = Path.Combine(_tempDir, "readonly-branch");
        var docsDir = Path.Combine(repoRoot, "docs");
        Directory.CreateDirectory(docsDir);
        var file = Path.Combine(docsDir, "page.md");
        File.WriteAllText(file, "# Checkout\n");
        var entries = new[] { (Name: "Readonly", RootPath: repoRoot) };
        var config = BuildConfig(entries);
        var scanner = BuildScanner(entries);
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var project = registry.EnsureProjectForStorage(
            Path.Combine(repoRoot, ".orchestrator", "jobs"), "Readonly", DefaultWorkspace.Id);
        registry.SetWikiSourceBranch(project.Id, "origin/develop");
        var docs = new ProjectDocsService(scanner, registry, NullLogger<ProjectDocsService>.Instance);

        var result = docs.WriteWikiFile("Readonly", "page.md", "# Diverged\n");

        Assert.False(result.Success);
        Assert.Contains("disabled to prevent silent divergence", result.Error);
        Assert.Equal("# Checkout\n", File.ReadAllText(file));
    }

    [Fact]
    public void Wiki_and_security_documents_reject_links_at_every_path_segment()
    {
        var repoRoot = Path.Combine(_tempDir, "linked-repo");
        var docsRoot = Path.Combine(repoRoot, "docs");
        var securityRoot = Path.Combine(docsRoot, "operations", "security");
        var outside = Path.Combine(_tempDir, "outside");
        Directory.CreateDirectory(securityRoot);
        Directory.CreateDirectory(outside);
        var secret = Path.Combine(outside, "secret.md");
        File.WriteAllText(secret, "outside content");
        Directory.CreateSymbolicLink(Path.Combine(docsRoot, "linked-folder"), outside);
        File.CreateSymbolicLink(Path.Combine(docsRoot, "linked-file.md"), secret);
        Directory.CreateSymbolicLink(Path.Combine(securityRoot, "linked-folder"), outside);
        File.CreateSymbolicLink(Path.Combine(securityRoot, "linked-file.md"), secret);
        var docs = BuildDocsService(("Linked", repoRoot));

        Assert.Null(docs.ReadWikiFile("Linked", "linked-folder/secret.md"));
        Assert.Null(docs.ReadWikiFile("Linked", "linked-file.md"));
        Assert.False(docs.WriteWikiFile("Linked", "linked-folder/secret.md", "changed").Success);
        Assert.False(docs.WriteWikiFile("Linked", "linked-file.md", "changed").Success);
        Assert.Null(docs.ReadSecurityFile("Linked", "linked-folder/secret.md"));
        Assert.Null(docs.ReadSecurityFile("Linked", "linked-file.md"));
        Assert.False(docs.WriteSecurityFile("Linked", "linked-folder/secret.md", "changed"));
        Assert.False(docs.WriteSecurityFile("Linked", "linked-file.md", "changed"));
        Assert.Equal("outside content", File.ReadAllText(secret));
    }

    // ---- helpers ----

    private ProjectDocsService BuildDocsService(params (string Name, string RootPath)[] entries)
        => new(BuildScanner(entries),
               new ProjectRegistry(BuildConfig(entries), NullLogger<ProjectRegistry>.Instance),
               NullLogger<ProjectDocsService>.Instance);

    private GitService BuildGitService(params (string Name, string RootPath)[] entries)
        => new(NullLogger<GitService>.Instance, BuildScanner(entries), BuildConfig(entries));

    private static IConfiguration BuildConfig((string Name, string RootPath)[] entries)
    {
        var dict = new Dictionary<string, string?>();
        for (var i = 0; i < entries.Length; i++)
        {
            dict[$"WatchPaths:{i}:Name"] = entries[i].Name;
            dict[$"WatchPaths:{i}:RootPath"] = entries[i].RootPath;
            dict[$"WatchPaths:{i}:Path"] = Path.Combine(entries[i].RootPath, ".orchestrator", "jobs");
        }
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static TaskScannerService BuildScanner((string Name, string RootPath)[] entries)
    {
        var config = BuildConfig(entries);
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        return new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
    }

    private static void RunGit(string cwd, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit(15_000);
    }

    private static void RunGitArgs(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit(15_000);
    }
}
