using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2824 - HTTP contract of the explicit "Retry integration" operator action.
/// The route exists so a healed gate host can be re-tried without spending a new
/// remote review; it applies the same eligibility guards as the automatic rail,
/// so a merge conflict or a card outside Human Review is refused with the reason
/// rather than silently replayed.
/// </summary>
public sealed class GateEnvironmentRetryEndpointTests : IDisposable
{
    private const string ProjectName = "gate-environment-retry-test";
    private const string TaskKey = "AGT-2824";

    private readonly string _workspace;
    private readonly string _watchPath;
    private readonly string _repository;

    public GateEnvironmentRetryEndpointTests()
    {
        _workspace = Path.Combine(
            Path.GetTempPath(),
            "agt-gate-retry-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", ProjectName);
        _repository = Path.Combine(_workspace, "repo");

        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));

        Directory.CreateDirectory(_repository);
        RunGit("init", "-q", "-b", "develop");
        RunGit("config", "user.email", "test@example.com");
        RunGit("config", "user.name", "test");
        RunGit("config", "commit.gpgsign", "false");
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (Exception ex) { SilentCatch.Note(ex, "best-effort temp cleanup"); }
    }

    [Fact]
    public async Task RetryIntegration_UnknownTask_IsNotFound()
    {
        using var factory = BuildFactory();
        using var client = Client(factory);

        using var response = await client.PostAsync(Url("does-not-exist"), null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RetryIntegration_MergeConflict_IsRefusedWithTheReason()
    {
        var sha = CommitFile("work.txt", "task work", $"feat: {TaskKey}");
        WriteTask(TaskStates.HumanReview, sha);
        WriteFailedMergeStep(AcceptedIntegrationFailureCodes.MergeConflict, "conflict");

        using var factory = BuildFactory();
        using var client = Client(factory);

        using var response = await client.PostAsync(Url(TaskKey), null);

        var raw = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var body = JsonDocument.Parse(raw);
        Assert.Contains(
            "not a gate environment failure",
            body.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task RetryIntegration_OutsideHumanReview_IsRefused()
    {
        var sha = CommitFile("work.txt", "task work", $"feat: {TaskKey}");
        WriteTask(TaskStates.Archive, sha);
        WriteFailedMergeStep(
            AcceptedIntegrationFailureCodes.GateEnvironmentFailure,
            "gate-environment-failure",
            TaskStates.Archive);

        using var factory = BuildFactory();
        using var client = Client(factory);

        using var response = await client.PostAsync(Url(TaskKey), null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(TaskStates.Archive, body.RootElement.GetProperty("state").GetString());
    }

    private string Url(string jobId)
        => $"/api/tasks/{jobId}/integration/retry?watchPath={Uri.EscapeDataString(_watchPath)}";

    private static HttpClient Client(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        return client;
    }

    private WebApplicationFactory<Program> BuildFactory() =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["TaskRepository"] = _workspace,
                        ["WatchPaths:0:Name"] = ProjectName,
                        ["WatchPaths:0:Path"] = _watchPath,
                        ["WatchPaths:0:RootPath"] = _repository,
                        ["WatchPaths:0:RepositoryPath"] = _repository,
                    });
                });
                // The boot-time backfills and sweeps would move a verdict-less
                // Human Review fixture out of its lane mid-request; this test
                // is about the endpoint contract, not about them.
                builder.ConfigureTestServices(services => services.RemoveAll<IHostedService>());
            });

    private string CommitFile(string relativePath, string content, string message)
    {
        File.WriteAllText(Path.Combine(_repository, relativePath), content);
        RunGit("add", "--", relativePath);
        RunGit("commit", "-q", "-m", message);
        return RunGitCapture("rev-parse", "HEAD").Trim();
    }

    private void WriteTask(string lane, string sha)
    {
        var folder = Path.Combine(_watchPath, lane, TaskKey);
        Directory.CreateDirectory(folder);
        var task = new
        {
            id = TaskKey,
            key = TaskKey,
            title = "Reviewed delivery blocked by a gate environment failure",
            state = lane,
            order = 1,
            agent = "codex",
            cliType = "codex",
            createdAt = "2026-09-15T00:00:00Z",
            projectName = ProjectName,
            commits = new[]
            {
                new
                {
                    sha,
                    shortSha = sha[..8],
                    message = $"feat: {TaskKey}",
                    filesChanged = 1,
                    files = new[] { "work.txt" },
                    at = "2026-09-15T00:01:00Z",
                    attribution = CommitAttributionKinds.Automatic,
                    confidence = 1.0,
                },
            },
        };
        File.WriteAllText(
            Path.Combine(folder, "task.json"),
            JsonSerializer.Serialize(task, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "Deliver the change.");
    }

    private void WriteFailedMergeStep(
        string failureCode,
        string verdict,
        string lane = TaskStates.HumanReview)
    {
        var folder = Path.Combine(_watchPath, lane, TaskKey);
        var pipeline = new PipelineExecutionLog(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PipelineExecutionLog>.Instance);
        pipeline.EnsureRun(folder, PipelineCatalogue.Standard, ProjectName, TaskKey);
        pipeline.RecordStep(folder, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopStepId,
            Kind = StepKind.Tool,
            Status = PipelineStepStatus.Failed,
            StartedAt = new DateTime(2026, 9, 15, 10, 0, 0, DateTimeKind.Utc),
            CompletedAt = new DateTime(2026, 9, 15, 10, 5, 0, DateTimeKind.Utc),
            Verdict = verdict,
            Reason = "The build gate blocked the merge into develop.",
            FailureCode = failureCode,
        });
    }

    private void RunGit(params string[] args)
    {
        var (stdout, stderr, exitCode) = RunProcess(args);
        Assert.True(
            exitCode == 0,
            $"git {string.Join(' ', args)} failed ({exitCode}): {stderr}\n{stdout}");
    }

    private string RunGitCapture(params string[] args)
    {
        var (stdout, stderr, exitCode) = RunProcess(args);
        Assert.True(
            exitCode == 0,
            $"git {string.Join(' ', args)} failed ({exitCode}): {stderr}\n{stdout}");
        return stdout;
    }

    private (string Stdout, string Stderr, int ExitCode) RunProcess(string[] args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = _repository,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);
        return (stdout, stderr, process.ExitCode);
    }
}
