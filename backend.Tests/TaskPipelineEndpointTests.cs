using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Xunit;

namespace AgentStudio.Tests;

public sealed class TaskPipelineEndpointTests : IDisposable
{
    private readonly string _watchPath = Path.Combine(
        Path.GetTempPath(),
        "task-pipeline-endpoint-" + Guid.NewGuid().ToString("N"));
    private readonly string _repositoryPath;

    public TaskPipelineEndpointTests()
    {
        _repositoryPath = _watchPath + "-repository";
        Directory.CreateDirectory(_repositoryPath);
        RunGit(_repositoryPath, "init", "-q", "-b", "main");
        RunGit(_repositoryPath, "config", "user.name", "test");
        RunGit(_repositoryPath, "config", "user.email", "test@example.com");
        File.WriteAllText(Path.Combine(_repositoryPath, "README.md"), "seed\n");
        RunGit(_repositoryPath, "add", "README.md");
        RunGit(_repositoryPath, "commit", "-q", "-m", "seed");

        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));

        var job = Path.Combine(_watchPath, TaskStates.Backlog, "pipeline-capabilities");
        Directory.CreateDirectory(job);
        File.WriteAllText(Path.Combine(job, "task.json"), JsonSerializer.Serialize(new
        {
            id = "pipeline-capabilities",
            title = "Pipeline capabilities",
            state = TaskStates.Backlog,
            order = 1,
            agent = "codex",
            mode = "coding",
        }));
        File.WriteAllText(Path.Combine(job, "status.md"), "# Result");
        File.WriteAllText(Path.Combine(job, "aspect-code-quality.md"), "# Code quality");
        File.WriteAllText(Path.Combine(job, "aspect-not-in-pipeline.md"), "# Unrelated");
        File.WriteAllText(
            Path.Combine(job, "remote-review-review_01-candidate_aspect-code-quality_stdout_log"),
            "raw aspect output");
        File.WriteAllText(Path.Combine(job, "remote-review-grade-review_01.md"), "# Grade");
        File.WriteAllText(Path.Combine(job, "pipeline-execution.json"), JsonSerializer.Serialize(
            new PipelineExecutionRecord
            {
                PipelineId = PipelineCatalogue.Standard.Id,
                PipelineVersion = PipelineCatalogue.Standard.Version,
                JobId = "pipeline-capabilities",
                StartedAt = DateTime.UtcNow,
                Steps =
                [
                    new PipelineStepExecution
                    {
                        StepId = "aspect-code-quality",
                        Kind = StepKind.Aspect,
                        Status = PipelineStepStatus.Passed,
                        ExecutionLocation = "remote",
                        ExecutionAttemptId = "review_01",
                    },
                ],
            }));

        File.WriteAllText(Path.Combine(_watchPath, "project-settings.json"), JsonSerializer.Serialize(
            new Dictionary<string, ProjectSettings>
            {
                ["pipeline-capabilities"] = new()
                {
                    PipelineSteps = new Dictionary<string, PipelineStepSetting>
                    {
                        ["aspect-code-quality"] = new()
                        {
                            Enabled = true,
                            Prompt = "Keep this custom prompt",
                            Condition = new PipelineStepCondition
                            {
                                When = PipelineStepConditions.Tag,
                                Value = "security",
                            },
                        },
                    },
                },
            }));
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchPath, recursive: true); }
        catch (Exception ex) { SilentCatch.Note(ex, "TaskPipelineEndpointTests: clean task fixture"); }
        try { Directory.Delete(_repositoryPath, recursive: true); }
        catch (Exception ex) { SilentCatch.Note(ex, "TaskPipelineEndpointTests: clean repository fixture"); }
    }

    [Fact]
    public async Task GetPipeline_ConfigExposesCatalogueDisableCapabilities()
    {
        await using var factory = new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["WatchPaths:0:Name"] = "pipeline-capabilities",
                    ["WatchPaths:0:Path"] = _watchPath,
                    ["WatchPaths:0:RootPath"] = _repositoryPath,
                    ["WatchPaths:0:RepositoryPath"] = _repositoryPath,
                    ["TaskRepository"] = _watchPath,
                }));
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/api/tasks/pipeline-capabilities/pipeline?watchPath={Uri.EscapeDataString(_watchPath)}");
        response.EnsureSuccessStatusCode();
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var config = body.RootElement.GetProperty("config");

        Assert.False(config.GetProperty(PipelineCatalogue.CoreAgentRunStepId).GetProperty("canDisable").GetBoolean());
        Assert.False(config.GetProperty(PipelineCatalogue.LoopGuardStepId).GetProperty("canDisable").GetBoolean());
        Assert.True(config.GetProperty(PipelineCatalogue.LintScssStepId).GetProperty("canDisable").GetBoolean());

        var qualityConfig = config.GetProperty("aspect-code-quality");
        Assert.Equal("Keep this custom prompt", qualityConfig.GetProperty("prompt").GetString());
        Assert.Equal(PipelineStepConditions.Tag,
            qualityConfig.GetProperty("condition").GetProperty("when").GetString());
        Assert.Equal("security",
            qualityConfig.GetProperty("condition").GetProperty("value").GetString());
        var qualityActivation = qualityConfig.GetProperty("activation");
        Assert.Equal(PostStepActivationProjection.Skipped,
            qualityActivation.GetProperty("state").GetString());
        Assert.Equal(PostStepActivationProjection.ConditionSource,
            qualityActivation.GetProperty("source").GetString());
        Assert.Contains("task has tag 'security'",
            qualityActivation.GetProperty("reason").GetString());

        var wikiActivation = config.GetProperty(PipelineCatalogue.AgentsWikiSyncStepId)
            .GetProperty("activation");
        Assert.Equal(PostStepActivationProjection.Inactive,
            wikiActivation.GetProperty("state").GetString());
        Assert.Equal(PostStepActivationProjection.GlobalSource,
            wikiActivation.GetProperty("source").GetString());
        Assert.Equal("Disabled by the global catalogue default.",
            wikiActivation.GetProperty("reason").GetString());

        var resultFiles = body.RootElement.GetProperty("resultFiles");
        Assert.Equal("status.md",
            resultFiles.GetProperty(PipelineCatalogue.CoreAgentRunStepId).GetString());
        Assert.Equal("aspect-code-quality.md",
            resultFiles.GetProperty("aspect-code-quality").GetString());
        Assert.False(resultFiles.TryGetProperty("aspect-requirement-fit", out _));
        Assert.False(resultFiles.TryGetProperty("aspect-not-in-pipeline", out _));
        var qualityEvidence = body.RootElement.GetProperty("aspectEvidence")
            .GetProperty("aspect-code-quality")[0];
        Assert.Equal("aspect-code-quality.md", qualityEvidence.GetProperty("reportFile").GetString());
        Assert.Equal(
            "remote-review-review_01-candidate_aspect-code-quality_stdout_log",
            qualityEvidence.GetProperty("rawLogFile").GetString());
        Assert.Equal(
            "remote-review-grade-review_01.md",
            qualityEvidence.GetProperty("reviewGradeFile").GetString());
    }

    [Fact]
    public async Task RunPostStep_UsesCanonicalProjectIdentityAndLeavesManagedRepoClean()
    {
        await using var factory = new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["WatchPaths:0:Name"] = "mutable display name",
                    ["WatchPaths:0:Path"] = _watchPath,
                    ["WatchPaths:0:RootPath"] = _repositoryPath,
                    ["WatchPaths:0:RepositoryPath"] = _repositoryPath,
                    ["TaskRepository"] = _watchPath,
                }));
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);

        var response = await client.PostAsync(
            $"/api/tasks/pipeline-capabilities/pipeline/steps/{PipelineCatalogue.AgentsWikiSyncStepId}/run" +
            $"?watchPath={Uri.EscapeDataString(_watchPath)}",
            JsonContent.Create(new { addToCard = true }));

        response.EnsureSuccessStatusCode();
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var row = body.RootElement;
        var projectId = row.GetProperty("projectId").GetString();
        Assert.Matches("^PROJ-[0-9]{3,}$", projectId!);
        Assert.NotEqual("mutable display name", projectId);
        Assert.Equal($"{projectId}::pipeline-capabilities", row.GetProperty("jobKey").GetString());
        Assert.Matches("^[a-f0-9]{64}$", row.GetProperty("id").GetString()!);
        Assert.Equal(1, row.GetProperty("attempt").GetInt32());

        Assert.Equal(string.Empty, RunGitCapture(_repositoryPath, "status", "--porcelain=v1"));
        Assert.Contains(
            $"docs(pipeline): run {PipelineCatalogue.AgentsWikiSyncStepId}",
            RunGitCapture(_repositoryPath, "log", "-1", "--format=%s"));
        Assert.True(File.Exists(Path.Combine(
            _watchPath,
            TaskStates.Backlog,
            "pipeline-capabilities",
            "results",
            "post-steps",
            $"{PipelineCatalogue.AgentsWikiSyncStepId}-attempt-001.md")));
    }

    /// <summary>
    /// AGT-3565: a research card whose run finished without a report review
    /// lists the missing requirement-fit step, and Review results runs only
    /// that step: the card stays in human review and the step is no longer
    /// offered once the verdict exists.
    /// </summary>
    [Fact]
    public async Task ReviewResults_RunsOnlyTheMissingReportReview()
    {
        var job = WriteResearchCardWithoutReview(withReport: true);
        var aspectCalls = new List<string>();
        await using var factory = ReviewResultsFactory(aspectCalls);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        var query = $"?watchPath={Uri.EscapeDataString(_watchPath)}";

        Assert.Equal([PipelineCatalogue.RequirementFitStepId], await RunnableSteps(client, query));
        var refused = await client.PostAsync(
            $"/api/tasks/pipeline-capabilities/review-results{query}", JsonContent.Create(new { }));
        Assert.Equal(System.Net.HttpStatusCode.Conflict, refused.StatusCode);

        var response = await client.PostAsync(
            $"/api/tasks/research-without-review/review-results{query}", JsonContent.Create(new { }));

        response.EnsureSuccessStatusCode();
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.True(body.RootElement.GetProperty("ran").GetBoolean());
        Assert.Equal("pass", body.RootElement.GetProperty("status").GetString());
        Assert.Equal(["requirement-fit"], aspectCalls);
        Assert.True(Directory.Exists(job));
        Assert.Empty(await RunnableSteps(client, query));
        var again = await client.PostAsync(
            $"/api/tasks/research-without-review/review-results{query}", JsonContent.Create(new { }));
        Assert.Equal(System.Net.HttpStatusCode.Conflict, again.StatusCode);
        Assert.Single(aspectCalls);
    }

    /// <summary>
    /// AGT-3565 review: a research card that delivered other files but not its
    /// report is not offered Review results, and the route refuses it, so a
    /// late pass verdict can never back the completion of a card without its
    /// report.
    /// </summary>
    [Fact]
    public async Task ReviewResults_RefusesAResearchCardWithoutItsReport()
    {
        WriteResearchCardWithoutReview(withReport: false);
        var aspectCalls = new List<string>();
        await using var factory = ReviewResultsFactory(aspectCalls);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        var query = $"?watchPath={Uri.EscapeDataString(_watchPath)}";

        Assert.Empty(await RunnableSteps(client, query));
        var refused = await client.PostAsync(
            $"/api/tasks/research-without-review/review-results{query}", JsonContent.Create(new { }));

        Assert.Equal(System.Net.HttpStatusCode.Conflict, refused.StatusCode);
        using var body = await JsonDocument.ParseAsync(await refused.Content.ReadAsStreamAsync());
        Assert.Contains("a valid HTML document at results/report.html",
            body.RootElement.GetProperty("error").GetString());
        Assert.Empty(aspectCalls);
    }

    private string WriteResearchCardWithoutReview(bool withReport)
    {
        var job = Path.Combine(_watchPath, TaskStates.HumanReview, "research-without-review");
        Directory.CreateDirectory(Path.Combine(job, "results"));
        File.WriteAllText(Path.Combine(job, "task.json"), JsonSerializer.Serialize(new
        {
            id = "research-without-review",
            title = "Research without review",
            state = TaskStates.HumanReview,
            order = 2,
            agent = "codex",
            mode = TaskModes.Research,
        }));
        File.WriteAllText(Path.Combine(job, "prompt.md"), "# Research\n\nName the renewal interval.\n");
        if (withReport)
            File.WriteAllText(Path.Combine(job, "results", "report.html"),
                "<html><body><p>The renewal interval is 30 seconds.</p></body></html>");
        else
            File.WriteAllText(Path.Combine(job, "results", "notes.md"), "Draft notes without the report.\n");
        BriefVersionStore.ReadOrCreate(job);
        CompletionContentEvidence.StampLocalRun(job);
        return job;
    }

    private WebApplicationFactory<Program> ReviewResultsFactory(List<string> aspectCalls)
        => new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["WatchPaths:0:Name"] = "pipeline-capabilities",
                    ["WatchPaths:0:Path"] = _watchPath,
                    ["WatchPaths:0:RootPath"] = _repositoryPath,
                    ["WatchPaths:0:RepositoryPath"] = _repositoryPath,
                    ["TaskRepository"] = _watchPath,
                }));
            builder.ConfigureServices(services => services.AddSingleton(provider =>
            {
                // No one-shot registry: the stub below answers every aspect call.
                var aspects = new AspectRunnerService(
                    provider.GetRequiredService<AgentStudio.Prompts.RuntimePromptService>(),
                    provider.GetRequiredService<ILogger<AspectRunnerService>>(),
                    pipelineLog: provider.GetService<PipelineExecutionLog>());
                aspects.CliRunner = (aspect, _, _, _, _, _) =>
                {
                    lock (aspectCalls) aspectCalls.Add(aspect);
                    return Task.FromResult("[[ASPECT_VERDICT: status=pass; summary=The report names the interval; "
                        + "evidence_checked=results/report.html; missing=none]]\n[[TASK_DONE]]");
                };
                return aspects;
            }));
        });

    private static async Task<string[]> RunnableSteps(HttpClient client, string query)
    {
        var response = await client.GetAsync($"/api/tasks/research-without-review/pipeline{query}");
        response.EnsureSuccessStatusCode();
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return body.RootElement.GetProperty("onDemand").GetProperty("runnableStepIds")
            .EnumerateArray().Select(item => item.GetString()!).ToArray();
    }

    private static void RunGit(string cwd, params string[] args)
    {
        var result = RunGitRaw(cwd, args);
        Assert.True(result.Code == 0, $"git {string.Join(' ', args)} failed: {result.Stdout} {result.Stderr}");
    }

    private static string RunGitCapture(string cwd, params string[] args)
    {
        var result = RunGitRaw(cwd, args);
        Assert.True(result.Code == 0, $"git {string.Join(' ', args)} failed: {result.Stdout} {result.Stderr}");
        return result.Stdout.Trim();
    }

    private static (string Stdout, string Stderr, int Code) RunGitRaw(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);
        return (stdout, stderr, process.ExitCode);
    }
}
