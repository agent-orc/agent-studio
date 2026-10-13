using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the DRIFT-Nachtrag post-step coordinator: drift dimensions default
/// OFF (no enabled step means no work, no CLI call, no report, no telemetry);
/// an enabled LLM dimension routes its per-step model to the CLI seam, persists
/// a Scheduled-trigger report through the existing <see cref="DriftReportStore"/>,
/// and records a <see cref="StepKind.Drift"/> step with the CLI's token usage;
/// and the deterministic code-pattern report maps onto a schema-valid
/// <see cref="DriftReport"/>.
/// </summary>
public sealed class DriftPostStepRunnerTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _promptDir;
    private readonly string _jobFolder;
    private const string Project = "agent-taskboard";

    public DriftPostStepRunnerTests()
    {
        var stem = Path.Combine(Path.GetTempPath(), "drift-poststep-tests-" + Guid.NewGuid().ToString("N"));
        _workspace = Path.Combine(stem, "workspace");
        _promptDir = Path.Combine(stem, "prompts");
        _jobFolder = Path.Combine(stem, "job");
        Directory.CreateDirectory(Path.Combine(_workspace, "projects", Project));
        Directory.CreateDirectory(_promptDir);
        Directory.CreateDirectory(_jobFolder);

        // Hermetic templates so RuntimePromptService.Render never depends on
        // the output-copied prompts tree. The bodies are irrelevant to the
        // behaviour under test (the CLI call is stubbed).
        foreach (var name in new[]
                 {
                     "adr-code-drift.md", "software-architecture-drift.md",
                     "docs-marketing-drift.md", "spec-task-job-drift.md",
                 })
        {
            File.WriteAllText(Path.Combine(_promptDir, name), "# drift template\n");
        }
    }

    public void Dispose()
    {
        try
        {
            var parent = Directory.GetParent(_workspace)?.FullName;
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                Directory.Delete(parent, recursive: true);
        }
        catch { /* best-effort temp cleanup */ }
    }

    private IConfiguration BuildConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["PromptTemplates:RuntimePath"] = _promptDir,
            })
            .Build();

    private DriftPostStepRunner BuildRunner(DriftReportStore driftStore, PipelineExecutionLog pipelineLog) =>
        new(
            prompts: new RuntimePromptService(BuildConfig(), NullLogger<RuntimePromptService>.Instance),
            driftStore: driftStore,
            analysisStore: new AnalysisReportStore(),
            adrCode: new AdrCodeDriftAnalysisService(),
            softwareArch: new SoftwareArchitectureDriftAnalysisService(),
            docsMarketing: new DocsMarketingDriftAnalysisService(),
            specTask: new SpecTaskDriftAnalysisService(),
            codePattern: new CodePatternDriftAnalysisService(NullLogger<CodePatternDriftAnalysisService>.Instance),
            pipelineLog: pipelineLog,
            config: BuildConfig(),
            logger: NullLogger<DriftPostStepRunner>.Instance);

    private static ProjectSettings SettingsEnabling(string stepId, string? model, string? cliType = null)
    {
        return new ProjectSettings
        {
            PipelineSteps = new Dictionary<string, PipelineStepSetting>(StringComparer.OrdinalIgnoreCase)
            {
                [stepId] = new PipelineStepSetting { Enabled = true, Model = model, CliType = cliType },
            },
        };
    }

    // ------------------------------------------------------------------
    // Default-OFF gating
    // ------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_NoEnabledDriftSteps_DoesNoWork()
    {
        var driftStore = new DriftReportStore();
        var pipelineLog = new PipelineExecutionLog(TokenTestServices.Pricing(), NullLogger<PipelineExecutionLog>.Instance);
        var runner = BuildRunner(driftStore, pipelineLog);

        var cliCalls = 0;
        runner.CliRunner = (_, _, _, _, _, _, _) =>
        {
            Interlocked.Increment(ref cliCalls);
            return Task.FromResult(new DriftCliResult(true, string.Empty, null));
        };

        // settings == null: every drift step falls back to DefaultEnabled=false.
        await runner.RunAsync(Project, "job-1", _jobFolder, settings: null);

        Assert.Equal(0, cliCalls);
        Assert.Equal(0, driftStore.Count(_workspace, Project));
        // No pipeline-execution record should have been started for drift alone.
        Assert.Null(pipelineLog.Read(_jobFolder));
    }

    // ------------------------------------------------------------------
    // Per-step model routing + persistence + telemetry (one LLM dimension)
    // ------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_EnabledLlmDimension_RoutesPerStepModel_PersistsScheduledReport_RecordsTelemetry()
    {
        var driftStore = new DriftReportStore();
        var pipelineLog = new PipelineExecutionLog(TokenTestServices.Pricing(), NullLogger<PipelineExecutionLog>.Instance);
        var runner = BuildRunner(driftStore, pipelineLog);

        string? capturedCli = null;
        string? capturedModel = null;
        var usage = new OrchestratorTokenUsage
        {
            Model = ModelIds.Gpt55,
            InputTokens = 1234,
            OutputTokens = 567,
            CacheReadTokens = 89,
        };
        runner.CliRunner = (cliType, model, _, _, _, _, _) =>
        {
            capturedCli = cliType;
            capturedModel = model;
            // A short non-JSON narrative parses as Unstructured, which the
            // drift services still turn into a schema-valid evidence-only report.
            return Task.FromResult(new DriftCliResult(true, "No material drift observed.", usage));
        };

        var settings = SettingsEnabling(PipelineCatalogue.DriftAdrCodeStepId, ModelIds.Gpt55, CliTypes.Codex);
        await runner.RunAsync(Project, "job-42", _jobFolder, settings);

        // 1. Per-step model routed to the CLI seam (the acceptance "assert the
        //    configured Codex/OpenAI pair).
        Assert.Equal(CliTypes.Codex, capturedCli);
        Assert.Equal(ModelIds.Gpt55, capturedModel);

        // 2. The report landed in the existing store with the Scheduled trigger
        //    (no manual button).
        var reports = driftStore.Snapshot(_workspace, Project);
        var report = Assert.Single(reports);
        Assert.Equal(DriftReportTrigger.Scheduled, report.Trigger);

        // 3. Drift step telemetry recorded with the CLI token usage.
        var record = pipelineLog.Read(_jobFolder);
        Assert.NotNull(record);
        var step = Assert.Single(record!.Steps, s => s.StepId == PipelineCatalogue.DriftAdrCodeStepId);
        Assert.Equal(StepKind.Drift, step.Kind);
        Assert.Equal(PipelineStepStatus.Passed, step.Status);
        Assert.Equal(ModelIds.Gpt55, step.Model);
        Assert.Equal(1234, step.InputTokens);
        Assert.Equal(567, step.OutputTokens);
        Assert.Equal(89, step.CacheReadTokens);
    }

    // ------------------------------------------------------------------
    // Code-pattern report mapping (pure, no repo scan)
    // ------------------------------------------------------------------

    [Fact]
    public void MapCodePatternReport_WithDriftFindings_ProducesSchemaValidScheduledReport()
    {
        var cp = new CodePatternDriftReport(
            CapturedAt: DateTime.UtcNow,
            RepoRoot: "/repo",
            Findings: new[]
            {
                new CodePatternFinding(
                    RuleId: "no-direct-job-folder-io",
                    Title: "Direct job-folder IO",
                    CanonicalDescription: "Go through the API.",
                    TotalSites: 5,
                    CanonicalSites: 3,
                    DriftSites: 2,
                    Hits: new[]
                    {
                        new CodePatternHit("backend/A.cs", 10, "File.Move(...)", true, "writes job folder directly"),
                        new CodePatternHit("backend/B.cs", 22, "File.Delete(...)", true, "deletes job folder directly"),
                        new CodePatternHit("backend/C.cs", 33, "api.Move(...)", false, "canonical"),
                    },
                    OverallSeverity: DriftSeverity.High),
            },
            TotalDriftSites: 2);

        var report = DriftPostStepRunner.MapCodePatternReport(
            cp, Project, "0120260602120000000abcdef12", DateTime.UtcNow, DriftReportTrigger.Scheduled);

        Assert.True(DriftReportValidator.TryValidate(report, out var error), error);
        Assert.Equal(DriftReportTrigger.Scheduled, report.Trigger);
        Assert.NotNull(report.Producer);
        Assert.Equal(DriftReportProducerKind.Scheduled, report.Producer!.Kind);

        var dim = Assert.Single(report.Dimensions);
        Assert.Equal(DriftDimensionType.Process, dim.Type);
        // 2 drift sites -> score 100 - 20 = 80; band derived from High severity.
        Assert.Equal(80, report.OverallScore);
        Assert.Equal(DriftScoreBand.Warn, report.ScoreBand);

        var finding = Assert.Single(dim.Findings!);
        Assert.Equal("no-direct-job-folder-io", finding.FindingId);
        // Only the drifted hits become evidence refs.
        Assert.Equal(2, finding.EvidenceRefs!.Count);
        Assert.Contains("backend/A.cs:10", finding.EvidenceRefs!);
    }

    [Fact]
    public void MapCodePatternReport_NoDrift_IsHealthyAndValid()
    {
        var cp = new CodePatternDriftReport(
            CapturedAt: DateTime.UtcNow,
            RepoRoot: "/repo",
            Findings: Array.Empty<CodePatternFinding>(),
            TotalDriftSites: 0);

        var report = DriftPostStepRunner.MapCodePatternReport(
            cp, Project, "0120260602120000000abcdef99", DateTime.UtcNow, DriftReportTrigger.Scheduled);

        Assert.True(DriftReportValidator.TryValidate(report, out var error), error);
        Assert.Equal(100, report.OverallScore);
        Assert.Equal(DriftScoreBand.Healthy, report.ScoreBand);
        var dim = Assert.Single(report.Dimensions);
        Assert.Null(dim.Findings); // no drifted rules -> no findings
        Assert.Empty(dim.RecommendedActions);
    }
}
