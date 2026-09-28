using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

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
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
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
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
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
