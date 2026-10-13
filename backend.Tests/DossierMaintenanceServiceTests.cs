using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class DossierMaintenanceServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "dossier-maintenance-" + Guid.NewGuid().ToString("N"));
    private readonly string _taskStore;

    public DossierMaintenanceServiceTests()
    {
        _taskStore = Path.Combine(_root, ".orchestrator", "jobs");
        Directory.CreateDirectory(_root);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_taskStore, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void ResolveTargets_AcceptsTypedReferenceAndSourceTaskBackEdge()
    {
        WriteWorkbench("context-chat", "ASW-W8", ["AGT-42"]);
        var service = Service();

        var byReference = service.ResolveTargets("Project", new TaskInfo
        {
            Id = "typed-reference",
            Key = "AGT-99",
            References = new TaskReferences { Workbenches = ["ASW-W8"] },
        });
        var byBackEdge = service.ResolveTargets("Project", new TaskInfo
        {
            Id = "source-task",
            Key = "AGT-42",
        });

        Assert.Equal("docs/context-chat/index.html", Assert.Single(byReference).EntryPath);
        Assert.Equal("docs/context-chat/index.html", Assert.Single(byBackEdge).EntryPath);
    }

    [Fact]
    public void ResolveTargets_DoesNotTreatLegacyRelatedTaskAsAMaintenanceOwner()
    {
        WriteWorkbench("context-chat", "ASW-W8", []);
        var descriptorPath = Path.Combine(_root, "docs", "context-chat", "workbench.json");
        var descriptor = System.Text.Json.Nodes.JsonNode.Parse(
            File.ReadAllText(descriptorPath))!.AsObject();
        descriptor["relatedTaskKeys"] = new System.Text.Json.Nodes.JsonArray("AGT-42");
        File.WriteAllText(descriptorPath, descriptor.ToJsonString());

        var targets = Service().ResolveTargets("Project", new TaskInfo
        {
            Id = "legacy-related",
            Key = "AGT-42",
        });

        Assert.Empty(targets);
    }

    [Fact]
    public void RemoteRunFraming_CarriesTheResolvedDossierContract()
    {
        WriteWorkbench("context-chat", "ASW-W8", ["AGT-42"]);
        var task = new TaskInfo
        {
            Id = "source-task",
            Key = "AGT-42",
            ProjectName = "Project",
            Mode = TaskModes.Coding,
        };

        var framing = LeaseEndpoints.BuildModeFraming(task, Prompts(), Service());

        Assert.NotNull(framing);
        Assert.Contains("Dossier implementation update", framing);
        Assert.Contains("docs/context-chat/index.html", framing);
        Assert.Contains("ASW-W8", framing);
        Assert.Contains("AGT-42", framing);
    }

    [Fact]
    public void Review_GatesMissingEntryAndAcceptsAnExistingIdempotentEntry()
    {
        WriteWorkbench("context-chat", "ASW-W8", ["AGT-42"]);
        var seed = InitRepoAndCommit("seed dossier");
        var service = Service();
        var task = new TaskInfo
        {
            Id = "source-task",
            Key = "AGT-42",
            IntegrationBranch = "refs/heads/develop",
        };

        RunGit("update-ref", "refs/heads/develop", seed);
        var missing = service.Review("Project", _root, task);
        WriteDossier(Dossier(Entry("AGT-42")));
        var withEntry = CommitDossier("docs: entry landed elsewhere");
        RunGit("update-ref", "refs/heads/develop", withEntry);
        var complete = service.Review("Project", _root, task);

        Assert.True(missing.Required);
        Assert.False(missing.IsComplete);
        Assert.Contains(missing.Findings, finding => finding.Contains("AGT-42", StringComparison.Ordinal));
        Assert.True(complete.Required);
        Assert.True(complete.IsComplete, complete.Summary);
    }

    [Fact]
    public void Review_UsesTheAttributedCommitParentAsTheAppendOnlyBaseline()
    {
        WriteWorkbench("context-chat", "ASW-W8", ["AGT-42"]);
        InitRepoAndCommit("seed dossier");
        File.WriteAllText(
            Path.Combine(_root, "docs", "context-chat", "index.html"),
            Dossier(
                "<li data-implementation-entry=\"\" data-task-key=\"AGT-42\" "
                + "data-delivered-at=\"2026-08-10\" data-slice=\"Context API\">"
                + "Delivered the bounded context API.</li>"));
        RunGit("add", "docs/context-chat/index.html");
        RunGit("commit", "-m", "feat: append dossier delivery");
        var sha = RunGit("rev-parse", "HEAD").Trim();
        var task = new TaskInfo
        {
            Id = "source-task",
            Key = "AGT-42",
            Commits =
            [
                new TaskCommitInfo
                {
                    Sha = sha,
                    Files = ["docs/context-chat/index.html"],
                    At = new DateTime(2026, 8, 10, 10, 0, 0, DateTimeKind.Utc),
                },
            ],
        };

        var review = Service().Review("Project", _root, task);

        Assert.True(review.IsComplete, review.Summary);
    }

    [Fact]
    public void Review_AcceptsAValidEntryOnTheIntegrationTipWhenTheAttributedRevisionIsStale()
    {
        var (a, b) = StaleAttributedThenIntegrated(Dossier(Entry("AGT-42")));
        RunGit("update-ref", "refs/heads/develop", b);

        var review = Service().Review("Project", _root, AttributedTask(a));

        Assert.True(review.IsComplete, review.Summary);
        Assert.Empty(review.Findings);
        Assert.Contains("refs/heads/develop@", review.Summary);
        Assert.Contains(b[..8], review.Summary);
        Assert.Contains("already present", review.Summary);
    }

    [Fact]
    public void Review_PrefersTheFetchedOriginTipOverTheLocalBranch()
    {
        var (a, b) = StaleAttributedThenIntegrated(Dossier(Entry("AGT-42")));
        RunGit("update-ref", "refs/remotes/origin/develop", b);
        RunGit("update-ref", "refs/heads/develop", a);
        var service = Service();
        var task = AttributedTask(a);

        var candidates = service.ResolveRevisionCandidates(_root, task, subjectResultSha: null);
        var review = service.Review("Project", _root, task, candidates);

        Assert.Equal(
            ["refs/remotes/origin/develop", "refs/heads/develop"],
            candidates.Select(candidate => candidate.Source).ToArray());
        Assert.True(review.IsComplete, review.Summary);
        Assert.Contains("origin/develop", review.Summary);
        Assert.Contains(b[..8], review.Summary);
    }

    [Fact]
    public void Review_ChecksTheRunResultBeforeTheIntegrationBranch()
    {
        var (a, b) = StaleAttributedThenIntegrated(Dossier(Entry("AGT-42")));
        RunGit("update-ref", "refs/heads/develop", a);
        var service = Service();
        var task = AttributedTask(a);

        var candidates = service.ResolveRevisionCandidates(_root, task, subjectResultSha: b);
        var review = service.Review("Project", _root, task, candidates);

        Assert.Equal(DossierRevision.RunResultSource, candidates[0].Source);
        Assert.True(review.IsComplete, review.Summary);
        Assert.Contains("run result", review.Summary);
        Assert.Contains(b[..8], review.Summary);
    }

    [Fact]
    public void Review_ReportsBothRevisionsWhenTheIntegrationTipHasNoEntry()
    {
        var (a, b) = StaleAttributedThenIntegrated(Dossier(string.Empty));
        RunGit("update-ref", "refs/heads/develop", b);

        var review = Service().Review("Project", _root, AttributedTask(a));

        Assert.False(review.IsComplete);
        Assert.Equal(2, review.Findings.Count);
        Assert.Contains(review.Findings, finding =>
            finding.Contains("attributed commit " + a[..8], StringComparison.Ordinal)
            && finding.Contains(DossierImplementationContract.SectionStartMarker, StringComparison.Ordinal)
            && finding.Contains(DossierImplementationContract.LogStartMarker, StringComparison.Ordinal)
            && finding.Contains("</main>", StringComparison.Ordinal));
        Assert.Contains(review.Findings, finding =>
            finding.Contains("refs/heads/develop@" + b[..8], StringComparison.Ordinal)
            && finding.Contains("no entry for AGT-42", StringComparison.Ordinal));
    }

    [Fact]
    public void Review_DoesNotAcceptADuplicateEntryOnTheIntegrationTip()
    {
        var (a, b) = StaleAttributedThenIntegrated(Dossier(Entry("AGT-42") + Entry("AGT-42")));
        RunGit("update-ref", "refs/heads/develop", b);

        var review = Service().Review("Project", _root, AttributedTask(a));

        Assert.False(review.IsComplete);
        Assert.Contains(review.Findings, finding =>
            finding.Contains("duplicate entries for AGT-42", StringComparison.Ordinal)
            && finding.Contains(b[..8], StringComparison.Ordinal));
        Assert.DoesNotContain("already present", review.Summary);
    }

    [Fact]
    public void Review_NeverClearsAnAppendOnlyViolationFromANewerRevision()
    {
        WriteWorkbench("context-chat", "ASW-W8", ["AGT-42"]);
        InitRepoAndCommit("seed dossier");
        WriteDossier(Dossier(Entry("AGT-42")).Replace(
            "Keep this decision.", "Reworded decision.", StringComparison.Ordinal));
        var a = CommitDossier("feat: append entry and reword decision");
        WriteDossier(Dossier(Entry("AGT-42")));
        var b = CommitDossier("fix: restore decision");
        RunGit("update-ref", "refs/heads/develop", b);

        var review = Service().Review("Project", _root, AttributedTask(a));

        Assert.False(review.IsComplete);
        var finding = Assert.Single(review.Findings);
        Assert.Contains("changed outside the append-only implementation log", finding);
        Assert.Contains("attributed commit " + a[..8], finding);
    }

    [Theory]
    [InlineData(false, true, PipelineStepStatus.NotApplicable, "not-referenced")]
    [InlineData(true, true, PipelineStepStatus.Passed, "appended")]
    [InlineData(true, false, PipelineStepStatus.Failed, "missing-update")]
    public void StepPolicy_RecordsTheVisibleTimelineOutcome(
        bool required,
        bool complete,
        PipelineStepStatus expectedStatus,
        string expectedVerdict)
    {
        var started = new DateTime(2026, 8, 10, 10, 0, 0, DateTimeKind.Utc);
        var review = new DossierMaintenanceReview(
            required,
            complete,
            required
                ? [new DossierMaintenanceTarget("context", "ASW-W8", "Context chat", "docs/context/index.html")]
                : [],
            complete ? [] : ["Entry missing."]);

        var execution = DossierMaintenanceStepPolicy.ToExecution(
            review,
            started,
            started.AddMilliseconds(12));

        Assert.Equal(PipelineCatalogue.DossierMaintenanceStepId, execution.StepId);
        Assert.Equal(expectedStatus, execution.Status);
        Assert.Equal(expectedVerdict, execution.Verdict);
        Assert.Equal(12, execution.DurationMs);
    }

    private DossierMaintenanceService Service()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "Project",
            ["WatchPaths:0:RootPath"] = _root,
            ["WatchPaths:0:Path"] = _taskStore,
        }).Build();
        var summaries = new SummaryGenerationService(
            NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(
            config, NullLogger<TaskScannerService>.Instance, summaries);
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var catalogue = new WorkbenchCatalogueService(scanner, registry, git);
        return new DossierMaintenanceService(catalogue, git);
    }

    private RuntimePromptService Prompts()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PromptTemplates:RuntimePath"] = FindPromptRoot(),
        }).Build();
        return new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
    }

    private static string FindPromptRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "prompts", "runtime");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate prompts/runtime from test base directory.");
    }

    private void WriteWorkbench(string id, string key, string[] sourceTaskKeys)
    {
        var directory = Path.Combine(_root, "docs", id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "index.html"), Dossier(string.Empty));
        File.WriteAllText(Path.Combine(directory, "workbench.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            id,
            key,
            title = "Context chat",
            summary = "Track delivery of the context-chat slices.",
            entrypoint = "index.html",
            status = "decided",
            phase = "decision-ready",
            updatedAt = "2026-08-10T10:00:00Z",
            sourceTaskKeys,
            relatedTaskKeys = Array.Empty<string>(),
        }));
    }

    private static string Dossier(string log) =>
        "<main><section id=\"decision\">Keep this decision.</section>"
        + DossierImplementationContract.SectionStartMarker
        + "<section id=\"implementation\"><ol>"
        + DossierImplementationContract.LogStartMarker
        + log
        + DossierImplementationContract.LogEndMarker
        + "</ol></section>"
        + DossierImplementationContract.SectionEndMarker
        + "</main>";

    private const string NoSectionDossier =
        "<main><section id=\"decision\">Keep this decision.</section></main>";

    /// <summary>
    /// Seeds a Dossier without the implementation section, then commits A
    /// (attributed: edits the Dossier, still no section) and B (unattributed:
    /// <paramref name="integrated"/>). Returns both full SHAs.
    /// </summary>
    private (string A, string B) StaleAttributedThenIntegrated(string integrated)
    {
        WriteWorkbench("context-chat", "ASW-W8", ["AGT-42"]);
        WriteDossier(NoSectionDossier);
        InitRepoAndCommit("seed dossier without implementation section");
        WriteDossier(NoSectionDossier.Replace(
            "</section>", "</section><p>First round notes.</p>", StringComparison.Ordinal));
        var a = CommitDossier("feat: first round");
        WriteDossier(integrated);
        var b = CommitDossier("feat: fix round");
        return (a, b);
    }

    private static TaskInfo AttributedTask(string sha) => new()
    {
        Id = "source-task",
        Key = "AGT-42",
        IntegrationBranch = "refs/heads/develop",
        Commits =
        [
            new TaskCommitInfo
            {
                Sha = sha,
                ResultSha = sha,
                Files = ["docs/context-chat/index.html"],
                At = new DateTime(2026, 8, 10, 10, 0, 0, DateTimeKind.Utc),
            },
        ],
    };

    private static string Entry(string taskKey) =>
        $"<li data-implementation-entry=\"\" data-task-key=\"{taskKey}\" "
        + "data-delivered-at=\"2026-08-10\" data-slice=\"Context API\">"
        + "Delivered the bounded context API.</li>";

    private void WriteDossier(string html) =>
        File.WriteAllText(Path.Combine(_root, "docs", "context-chat", "index.html"), html);

    private string InitRepoAndCommit(string message)
    {
        RunGit("init");
        RunGit("config", "user.email", "dossier-tests@example.invalid");
        RunGit("config", "user.name", "Dossier Tests");
        RunGit("add", ".");
        RunGit("commit", "-m", message);
        return RunGit("rev-parse", "HEAD").Trim();
    }

    private string CommitDossier(string message)
    {
        RunGit("add", "docs/context-chat/index.html");
        RunGit("commit", "-m", message);
        return RunGit("rev-parse", "HEAD").Trim();
    }

    private string RunGit(params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);
        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {error}");
        return output;
    }
}
