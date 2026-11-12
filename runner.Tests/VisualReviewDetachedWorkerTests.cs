using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-3718: the review daemon prepares the attempt and captures the delivered
/// screenshots, then a detached worker builds a new workspace, adopts the
/// prepared directory, and runs the plan. The worker must give the
/// visual-review aspect exactly the screenshots the daemon prepared.
/// </summary>
public sealed class VisualReviewDetachedWorkerTests : IDisposable
{
    private const string SubjectRef = "refs/heads/task/visual";
    private const string SourceRunId = "run-coding-1";
    private static readonly DateTime Now = new(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "visual-review-worker-" + Guid.NewGuid().ToString("N"));
    private readonly string _origin;

    public VisualReviewDetachedWorkerTests()
    {
        _origin = Path.Combine(_root, "origin.git");
    }

    [Fact]
    public async Task Worker_that_adopts_a_prepared_attempt_lists_the_daemon_screenshots_for_the_visual_aspect()
    {
        var after = VisualScreenshotFixture.Png(1920, 1080);
        var before = VisualScreenshotFixture.Png(1920, 1080);
        var artifacts = new ServedArtifacts(
            (VisualScreenshotFixture.Artifact("after", SourceRunId, "bars/after-1920-light.png", "image/png", after), after),
            (VisualScreenshotFixture.Artifact("before", SourceRunId, "bars/before-1920-dark.png", "image/png", before), before));
        var (options, subject, lease) = await SubjectAsync("attempt-png");

        var daemon = new RemoteReviewWorkspace(options, subject, lease, _ => { }, new StubVisualAspect());
        using (var http = new HttpClient(artifacts) { BaseAddress = new Uri("http://localhost") })
        using (var client = new TaskServerClient(http, options.RunnerId))
            await daemon.PrepareAsync(client, default);

        var aspect = new StubVisualAspect();
        var worker = new RemoteReviewWorkspace(options, subject, lease, _ => { }, aspect);
        await worker.AdoptPreparedAsync(default);
        var evidence = await worker.ExecutePlanAsync(default);

        var prompt = Assert.Single(aspect.Prompts);
        var evidenceDirectory = Path.Combine(worker.AttemptRoot, VisualReviewScreenshotBundle.DirectoryName);
        Assert.DoesNotContain(VisualReviewScreenshotBundle.NoScreenshots, prompt);
        Assert.Contains("\"bars/after-1920-light.png\": "
            + JsonSerializer.Serialize(Path.Combine(evidenceDirectory, "after-image-01.png"))
            + " (light theme)", prompt);
        Assert.Contains("\"bars/before-1920-dark.png\": "
            + JsonSerializer.Serialize(Path.Combine(evidenceDirectory, "before-image-02.png"))
            + " (dark theme)", prompt);
        Assert.Equal(after, File.ReadAllBytes(Path.Combine(evidenceDirectory, "after-image-01.png")));
        var verdict = Assert.Single(evidence.Verdicts);
        Assert.Equal("visual-review", verdict.Aspect);
        Assert.Equal("pass", verdict.Status);
        Assert.Contains("bars/after-1920-light.png", verdict.EvidenceChecked);
        Assert.Equal(2, artifacts.ContentReads);
    }

    [Fact]
    public async Task Webp_after_screenshot_sent_as_octet_stream_counts_as_evidence_in_the_worker()
    {
        var webp = VisualScreenshotFixture.Webp(1920, 1080);
        var artifacts = new ServedArtifacts(
            (VisualScreenshotFixture.Artifact("after", SourceRunId, "bars/after-1920-light.webp",
                "application/octet-stream", webp), webp));
        var (options, subject, lease) = await SubjectAsync("attempt-webp");

        var daemon = new RemoteReviewWorkspace(options, subject, lease, _ => { }, new StubVisualAspect());
        using (var http = new HttpClient(artifacts) { BaseAddress = new Uri("http://localhost") })
        using (var client = new TaskServerClient(http, options.RunnerId))
            await daemon.PrepareAsync(client, default);

        var aspect = new StubVisualAspect();
        var worker = new RemoteReviewWorkspace(options, subject, lease, _ => { }, aspect);
        await worker.AdoptPreparedAsync(default);
        var evidence = await worker.ExecutePlanAsync(default);

        Assert.Contains("\"bars/after-1920-light.webp\": ", Assert.Single(aspect.Prompts));
        var index = File.ReadAllText(Path.Combine(worker.AttemptRoot,
            VisualReviewScreenshotBundle.DirectoryName, VisualReviewScreenshotBundle.IndexFileName));
        Assert.Contains("\"mediaType\": \"image/webp\"", index);
        var verdict = Assert.Single(evidence.Verdicts);
        Assert.Equal("pass", verdict.Status);
        Assert.NotEqual("missing-visual-evidence", verdict.Classification);
    }

    [Fact]
    public async Task Worker_without_delivered_screenshots_still_reports_the_missing_evidence_concern()
    {
        var (options, subject, lease) = await SubjectAsync("attempt-none");

        var daemon = new RemoteReviewWorkspace(options, subject, lease, _ => { }, new StubVisualAspect());
        using (var http = new HttpClient(new ServedArtifacts()) { BaseAddress = new Uri("http://localhost") })
        using (var client = new TaskServerClient(http, options.RunnerId))
            await daemon.PrepareAsync(client, default);

        var aspect = new StubVisualAspect();
        var worker = new RemoteReviewWorkspace(options, subject, lease, _ => { }, aspect);
        await worker.AdoptPreparedAsync(default);
        var evidence = await worker.ExecutePlanAsync(default);

        Assert.Contains(VisualReviewScreenshotBundle.NoScreenshots, Assert.Single(aspect.Prompts));
        var verdict = Assert.Single(evidence.Verdicts);
        Assert.Equal("concerns", verdict.Status);
        Assert.Equal("missing-visual-evidence", verdict.Classification);
    }

    private async Task<(RunnerOptions Options, ReviewSubjectDto Subject, ReviewLeaseDto Lease)> SubjectAsync(
        string attemptId)
    {
        var sha = await SeedOriginAsync();
        var now = Now;
        ReviewCommandDto[] plan =
        [
            new(
                "visual-review",
                "visual-review",
                "codex",
                [],
                TimeoutSeconds: 30,
                ExecutionKind: ReviewCommandKinds.AgentAspect,
                Prompt: "Review the delivered screenshots.",
                CliType: CliSelection.CodexCli,
                Model: "gpt-5.4-mini",
                ThinkingLevel: "high"),
        ];
        var subject = new ReviewSubjectDto(
            "subject-" + attemptId,
            "task-" + attemptId,
            SourceRunId,
            TaskServerClient.RepositoryIdentity(_origin)!,
            _origin,
            sha,
            SubjectRef,
            null,
            null,
            "coding-host",
            "policy",
            new ReviewPlanDto(plan, ["visual-review"], IntegrationRef: "refs/heads/main"),
            now);
        var lease = new ReviewLeaseDto(
            "lease-" + attemptId,
            attemptId,
            subject.SubjectId,
            "review-executor",
            "review-instance",
            "review-host",
            1,
            now,
            now.AddMinutes(2),
            "active",
            $"review-{attemptId}-f1",
            25400);
        var options = new RunnerOptions
        {
            ServerUrl = "http://localhost",
            RunnerId = "review-executor",
            RunnerName = "review-executor",
            Hostname = "review-host",
            BackendName = "remote-review",
            Role = "review",
            WorkDir = Path.Combine(_root, "coding"),
            ReviewWorkDir = Path.Combine(_root, "review"),
            BaseBranch = "main",
        };
        return (options, subject, lease);
    }

    /// <summary>A main branch and a subject branch that changes a frontend template.</summary>
    private async Task<string> SeedOriginAsync()
    {
        if (Directory.Exists(_origin))
            return (await GitAsync(_origin, "rev-parse", SubjectRef)).StdOut.Trim();
        Directory.CreateDirectory(_root);
        var seed = Path.Combine(_root, "seed");
        var template = Path.Combine("frontend", "src", "app", "bars", "bars.component.html");
        await GitAsync(_root, "init", "--bare", _origin);
        await GitAsync(_root, "init", "-b", "main", seed);
        Directory.CreateDirectory(Path.Combine(seed, Path.GetDirectoryName(template)!));
        await File.WriteAllTextAsync(Path.Combine(seed, template), "<div class=\"bars\"></div>\n");
        await GitAsync(seed, "add", ".");
        await GitAsync(seed, "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
            "commit", "-m", "base");
        await GitAsync(seed, "remote", "add", "origin", _origin);
        await GitAsync(seed, "push", "origin", "main");
        await GitAsync(seed, "checkout", "-b", "task/visual");
        await File.WriteAllTextAsync(Path.Combine(seed, template), "<div class=\"bars bars--compact\"></div>\n");
        await GitAsync(seed, "add", ".");
        await GitAsync(seed, "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
            "commit", "-m", "subject");
        await GitAsync(seed, "push", "origin", "task/visual");
        return (await GitAsync(seed, "rev-parse", "HEAD")).StdOut.Trim();
    }

    private static async Task<ProcessResult> GitAsync(string workingDirectory, params string[] args)
    {
        var result = await ProcessRunner.RunAsync("git", args, workingDirectory);
        Assert.True(result.Success, result.StdErr);
        return result;
    }

    public void Dispose()
    {
        ResilientDirectory.TryDelete(_root);
    }

    /// <summary>Serves the source run's artifact list and contents like the Task Server.</summary>
    private sealed class ServedArtifacts(params (ArtifactDto Artifact, byte[] Bytes)[] artifacts) : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private int _contentReads;

        public int ContentReads => Volatile.Read(ref _contentReads);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var list = $"/api/v1/runs/{SourceRunId}/artifacts";
            if (path == list)
                return Json200(artifacts.Select(item => item.Artifact).ToArray());
            var match = artifacts.FirstOrDefault(item => path == $"{list}/{item.Artifact.ArtifactId}/content");
            if (match.Artifact is null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            Interlocked.Increment(ref _contentReads);
            return Json200(VisualScreenshotFixture.Content(match.Artifact, match.Bytes));
        }

        private static Task<HttpResponseMessage> Json200<T>(T body) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body, Json),
                    System.Text.Encoding.UTF8, "application/json"),
            });
    }

    /// <summary>
    /// Stands in for the visual-review model: it passes only when its prompt
    /// lists an after screenshot that exists on disk, citing its name, and
    /// otherwise returns the concern the aspect prompt asks for.
    /// </summary>
    private sealed class StubVisualAspect : IReviewAgentCommandRunner
    {
        public ConcurrentQueue<string> Prompts { get; } = new();

        public string PlannedExecutable(ReviewCommandDto command) => command.FileName;

        public Task<CommandExecution> RunAsync(ReviewCommandDto command, CancellationToken ct)
        {
            var prompt = command.Prompt ?? string.Empty;
            Prompts.Enqueue(prompt);
            var cited = prompt.Split('\n')
                .Where(line => line.StartsWith("- \"", StringComparison.Ordinal) && line.Contains("after-image-"))
                .Select(line => (Name: JsonSerializer.Deserialize<string>(line[2..(line.IndexOf("\": ", StringComparison.Ordinal) + 1)])!,
                    Path: JsonSerializer.Deserialize<string>(line[(line.IndexOf("\": ", StringComparison.Ordinal) + 3)..]
                        .Split(" (", 2)[0])!))
                .FirstOrDefault(item => File.Exists(item.Path));
            var reply = cited.Name is null
                ? "[[ASPECT_VERDICT: status=concerns; summary=No delivered screenshots to inspect.; "
                  + "evidence_checked=frontend/src/app/bars/bars.component.html; missing=after screenshots]]"
                : $"[[ASPECT_VERDICT: status=pass; summary=Bars render cleanly.; "
                  + $"evidence_checked={cited.Name}: bars; missing=none]]";
            return Task.FromResult(new CommandExecution(new ProcessResult(0, reply, string.Empty), Now, Now, null, null));
        }
    }
}
