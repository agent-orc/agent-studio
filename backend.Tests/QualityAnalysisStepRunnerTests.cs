using AgentStudio.Pipeline;

using AgentStudio.Runner;
using AgentStudio.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using Xunit;

namespace AgentStudio.Tests;

public sealed class QualityAnalysisStepRunnerTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "quality-analysis-step-" + Guid.NewGuid().ToString("N"));
    private string Repository => Path.Combine(root, "repo");
    private string TaskFolder => Path.Combine(root, "task");

    public QualityAnalysisStepRunnerTests()
    {
        Directory.CreateDirectory(Path.Combine(Repository, "frontend", "src", "app"));
        File.WriteAllText(Path.Combine(Repository, "frontend", "angular.json"), "{}");
        Directory.CreateDirectory(TaskFolder);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch { }
    }

    [Fact]
    public void Policy_selects_conventional_axes_from_changed_files()
    {
        var frontend = QualityAnalysisPolicy.Resolve(Repository,
            ["frontend/src/app/example.component.scss"]);
        Assert.Contains(PipelineCatalogue.QualityAngularRulesStepId, frontend.EnabledSteps);
        Assert.Contains(PipelineCatalogue.QualityVisualStepId, frontend.EnabledSteps);
        Assert.DoesNotContain(PipelineCatalogue.QualitySecurityStepId, frontend.EnabledSteps);

        var backend = QualityAnalysisPolicy.Resolve(Repository,
            ["backend/Features/Tasks/TaskService.cs"]);
        Assert.Contains(PipelineCatalogue.QualityDotNetRulesStepId, backend.EnabledSteps);
        Assert.Contains(PipelineCatalogue.QualitySecurityStepId, backend.EnabledSteps);
        Assert.DoesNotContain(PipelineCatalogue.QualityVisualStepId, backend.EnabledSteps);
    }

    [Fact]
    public void Policy_reads_only_the_versioned_repository_override()
    {
        var quality = Path.Combine(Repository, ".quality");
        Directory.CreateDirectory(quality);
        File.WriteAllText(Path.Combine(quality, "agent-studio.json"), $$"""
        {
          "$schema": "{{QualityAnalysisPolicyFiles.SchemaId}}",
          "schemaVersion": 1,
          "steps": {
            "{{PipelineCatalogue.QualityAngularRulesStepId}}": { "enabled": false },
            "{{PipelineCatalogue.QualityConsistencyStepId}}": { "enabled": true }
          }
        }
        """);

        var result = QualityAnalysisPolicy.Resolve(Repository,
            ["frontend/src/app/example.component.scss"]);

        Assert.DoesNotContain(PipelineCatalogue.QualityAngularRulesStepId, result.EnabledSteps);
        Assert.Contains(PipelineCatalogue.QualityVisualStepId, result.EnabledSteps);
        Assert.Contains(PipelineCatalogue.QualityConsistencyStepId, result.EnabledSteps);
        Assert.Equal(QualityAnalysisPolicyFiles.RelativePath, result.ConfigurationPath);
    }

    [Fact]
    public async Task Angular_slice_runs_named_QS_analysis_and_writes_rule_evidence()
    {
        var source = "frontend/src/app/example.component.scss";
        File.WriteAllText(Path.Combine(Repository, source.Replace('/', Path.DirectorySeparatorChar)),
            ".sample { margin: 13px; }");
        var finding = new QualityStudioFinding(
            "qs-ng-002-sample",
            "QS-NG-002",
            "maintainability",
            "medium",
            "Angular quality rule matched",
            "The named Quality Studio rule matched this source location.",
            "Apply the linked Quality Studio rule guidance.",
            "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "Deterministic rule pre-check matched source text.",
            [new QualityStudioLocation(source, 1, 19)]);
        var core = new RecordingCore(new QualityStudioCoreResult(
            true, null, "AgentOrchestrator.CodeQuality", "0.1.0", [finding]));
        var runner = new QualityAnalysisStepRunner(
            core, NullLogger<QualityAnalysisStepRunner>.Instance);

        var result = await runner.RunAngularRulesAsync(
            Repository, TaskFolder, [source], 3, CancellationToken.None);

        Assert.Equal(QualityAnalysisStepVerdict.Findings, result.Verdict);
        Assert.Empty(result.BlockingFindings);
        Assert.Equal(QualityStudioAnalysisCoreAdapter.RulesAnalysisName, core.AnalysisName);
        Assert.Equal("code", core.Configuration!["reviewKind"]);
        Assert.Equal([source], core.Paths);
        Assert.True(File.Exists(Path.Combine(TaskFolder,
            "results", "quality-analysis", PipelineCatalogue.QualityAngularRulesStepId + ".json")));
        var evidence = Assert.Single(ReviewEvidenceLog.ReadLatestPerId(TaskFolder));
        Assert.Equal("QS-NG-002", evidence.RuleId);
        Assert.Equal(3, evidence.RunIndex);
        Assert.Contains("frontend/src/app/example.component.scss:1", evidence.FileRefs);
        Assert.Contains(result.EvidencePath!, evidence.Artifacts);
    }

    [Fact]
    public async Task Http_adapter_reads_current_checkout_and_maps_named_rule_findings()
    {
        var calls = new List<string>();
        var handler = new StubHandler(request =>
        {
            calls.Add(request.RequestUri!.PathAndQuery);
            return Json(request.RequestUri.AbsolutePath == "/api/repos"
                ? $$"""{"repositories":[{"id":"subject","rootPath":"{{Repository.Replace("\\", "\\\\")}}"}]}"""
                : """
                  {"available":true,"provenance":{"sensorVersion":"1.2.3"},"findings":[
                    {"id":"one","ruleId":"QS-NG-002","aspect":"maintainability","severity":"medium","title":"Rule matched","description":"A detail","recommendation":"A fix","fingerprint":"sha256:one","locations":[{"path":"frontend/src/app/example.component.scss","range":{"start":{"line":7,"column":4}}}]},
                    {"id":"other","ruleId":"QS-NG-003","aspect":"maintainability","severity":"low","title":"Other file","description":"A detail","recommendation":"A fix","fingerprint":"sha256:other","locations":[{"path":"frontend/src/app/other.component.scss"}]}
                  ]}
                  """);
        });
        var adapter = new QualityStudioAnalysisCoreAdapter(
            new HttpClient(handler), Settings("http://127.0.0.1:5127"));

        var result = await adapter.RunAsync(Repository,
            QualityStudioAnalysisCoreAdapter.RulesAnalysisName,
            new Dictionary<string, string>(),
            ["frontend/src/app/example.component.scss"], CancellationToken.None);

        Assert.True(result.Available);
        Assert.Equal("quality-studio:eslint", result.Producer);
        Assert.Equal("1.2.3", result.ProducerVersion);
        Assert.Equal("QS-NG-002", Assert.Single(result.Findings).RuleId);
        Assert.Equal(7, result.Findings[0].Locations[0].StartLine);
        Assert.Equal(["/api/repos", "/api/repos/subject/sensors/eslint/scan?path=frontend"], calls);
    }

    [Fact]
    public async Task Http_adapter_rejects_a_registration_for_a_different_checkout()
    {
        var calls = 0;
        var handler = new StubHandler(_ =>
        {
            calls++;
            return Json("""{"repositories":[{"id":"other","rootPath":"/other/checkout"}]}""");
        });
        var adapter = new QualityStudioAnalysisCoreAdapter(
            new HttpClient(handler), Settings("http://127.0.0.1:5127"));

        var result = await adapter.RunAsync(Repository,
            QualityStudioAnalysisCoreAdapter.RulesAnalysisName,
            new Dictionary<string, string>(), ["frontend/src/app/example.component.scss"], CancellationToken.None);

        Assert.False(result.Available);
        Assert.Contains("exact checkout", result.UnavailableReason);
        Assert.Equal(1, calls);
    }

    private static IConfiguration Settings(string url) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["QualityStudio:BaseUrl"] = url })
        .Build();

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(reply(request));
    }

    private sealed class RecordingCore(QualityStudioCoreResult result) : IQualityStudioAnalysisCore
    {
        public string? AnalysisName { get; private set; }
        public IReadOnlyDictionary<string, string>? Configuration { get; private set; }
        public IReadOnlyList<string>? Paths { get; private set; }

        public Task<QualityStudioCoreResult> RunAsync(
            string repositoryPath,
            string analysisName,
            IReadOnlyDictionary<string, string> configuration,
            IReadOnlyList<string> relativePaths,
            CancellationToken cancellationToken)
        {
            AnalysisName = analysisName;
            Configuration = configuration;
            Paths = relativePaths;
            return Task.FromResult(result);
        }
    }
}
