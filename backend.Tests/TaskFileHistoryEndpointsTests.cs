using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

using Xunit;

namespace AgentStudio.Tests;

public sealed class TaskFileHistoryEndpointsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _workspaceRoot;
    private readonly string _workspaceProjectRoot;
    private readonly string _codeRoot;

    public TaskFileHistoryEndpointsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "atp-file-history-" + Guid.NewGuid().ToString("N"));
        _workspaceRoot = Path.Combine(_tempDir, "workspace");
        _workspaceProjectRoot = Path.Combine(_workspaceRoot, "projects", "agent-taskboard");
        _codeRoot = Path.Combine(_tempDir, "code");

        Directory.CreateDirectory(_workspaceProjectRoot);
        Directory.CreateDirectory(_codeRoot);

        InitRepo(_workspaceRoot);
        InitRepo(_codeRoot);
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

    [Fact]
    public async Task WorkspaceArtifactHistory_ReturnsTrailersAndVersionContent()
    {
        var job = WriteJob("ASS-853");
        var review = Path.Combine(job, "code-review.md");

        File.WriteAllText(review, "run 1 review\n", Encoding.UTF8);
        WriteGenerationIndex(job, runIndex: 1);
        RunGit(_workspaceRoot, "add", "-A");
        RunGit(_workspaceRoot, "commit", "-q", "-m", "chore(workspace): record run artifacts for ASS-853", "-m",
            "Run-Index: 1\nVerdict: reissue\nSteps: aspect-code-quality=warn");
        var firstSha = RunGitCapture(_workspaceRoot, "rev-parse", "HEAD").Trim();

        File.WriteAllText(review, "run 2 review\n", Encoding.UTF8);
        WriteGenerationIndex(job, runIndex: 2);
        RunGit(_workspaceRoot, "add", "-A");
        RunGit(_workspaceRoot, "commit", "-q", "-m", "chore(workspace): record run artifacts for ASS-853", "-m",
            "Run-Index: 2\nVerdict: accept\nSteps: aspect-code-quality=pass");
        var secondSha = RunGitCapture(_workspaceRoot, "rev-parse", "HEAD").Trim();

        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var watchPath = Uri.EscapeDataString(_workspaceProjectRoot);
        using var historyResponse = await client.GetAsync($"/api/tasks/ASS-853/files/code-review.md/history?watchPath={watchPath}");
        historyResponse.EnsureSuccessStatusCode();

        using var historyDoc = JsonDocument.Parse(await historyResponse.Content.ReadAsStringAsync());
        var entries = historyDoc.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, entries.Count);
        Assert.Equal(secondSha, entries[0].GetProperty("sha").GetString());
        Assert.Equal(2, entries[0].GetProperty("runIndex").GetInt32());
        Assert.Equal("accept", entries[0].GetProperty("verdict").GetString());
        Assert.Equal("workspace", entries[0].GetProperty("provenance").GetProperty("source").GetString());
        Assert.Equal("aspect-code-quality=pass", entries[0].GetProperty("provenance").GetProperty("steps").GetString());
        Assert.Equal(2, entries[0].GetProperty("provenance").GetProperty("generation").GetProperty("runIndex").GetInt32());
        Assert.Equal("aspect", entries[0].GetProperty("provenance").GetProperty("generation").GetProperty("kind").GetString());

        using var contentResponse = await client.GetAsync($"/api/tasks/ASS-853/files/code-review.md?watchPath={watchPath}&at={firstSha}");
        contentResponse.EnsureSuccessStatusCode();
        Assert.Equal("text/markdown", contentResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal("run 1 review\n", await contentResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task HtmlArtifact_ReturnsHtmlContentType()
    {
        var job = WriteJob("ASS-HTML");
        File.WriteAllText(Path.Combine(job, "exploration.html"), "<button>switch</button>", Encoding.UTF8);

        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var watchPath = Uri.EscapeDataString(_workspaceProjectRoot);
        using var response = await client.GetAsync($"/api/tasks/ASS-HTML/files/exploration.html?watchPath={watchPath}");

        response.EnsureSuccessStatusCode();
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("<button>switch</button>", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ResultArtifactRoute_ServesNestedHtmlInline_AndKeepsUnknownFilesAsDownloads()
    {
        var job = WriteJob("ASS-RESULT-HTML");
        var nested = Path.Combine(job, "results", "reports");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "concept report.html"), "<h1>Concept report</h1>", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(nested, "evidence.bin"), [0, 1, 2, 255]);

        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var watchPath = Uri.EscapeDataString(_workspaceProjectRoot);
        using var html = await client.GetAsync(
            $"/api/tasks/ASS-RESULT-HTML/results/reports/concept%20report.html?watchPath={watchPath}");

        html.EnsureSuccessStatusCode();
        Assert.Equal("text/html", html.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", html.Content.Headers.ContentType?.CharSet?.ToLowerInvariant());
        Assert.Equal("inline", html.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains("sandbox allow-scripts", html.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("<h1>Concept report</h1>", await html.Content.ReadAsStringAsync());

        using var binary = await client.GetAsync(
            $"/api/tasks/ASS-RESULT-HTML/results/reports/evidence.bin?watchPath={watchPath}");
        binary.EnsureSuccessStatusCode();
        Assert.Equal("application/octet-stream", binary.Content.Headers.ContentType?.MediaType);
        Assert.Equal(new byte[] { 0, 1, 2, 255 }, await binary.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task ThumbnailRoute_ServesBoundedWebpWithoutChangingTheFullResult()
    {
        var job = WriteJob("ASS-THUMBNAIL");
        var results = Path.Combine(job, "results", "gallery");
        Directory.CreateDirectory(results);
        var source = Path.Combine(results, "evidence.png");
        using (var image = new Image<Rgba32>(1200, 800, new Rgba32(32, 80, 140)))
            image.SaveAsPng(source);

        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var watchPath = Uri.EscapeDataString(_workspaceProjectRoot);
        using var response = await client.GetAsync(
            $"/api/tasks/ASS-THUMBNAIL/thumbnail?path=gallery%2Fevidence.png&width=320&watchPath={watchPath}");

        response.EnsureSuccessStatusCode();
        Assert.Equal("image/webp", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.Private == true);
        using var thumbnail = Image.Load(await response.Content.ReadAsByteArrayAsync());
        Assert.True(thumbnail.Width <= 320);
        Assert.True(thumbnail.Height <= 320);

        using var full = await client.GetAsync(
            $"/api/tasks/ASS-THUMBNAIL/results/gallery/evidence.png?watchPath={watchPath}");
        full.EnsureSuccessStatusCode();
        Assert.Equal("image/png", full.Content.Headers.ContentType?.MediaType);
        using var fullImage = Image.Load(await full.Content.ReadAsByteArrayAsync());
        Assert.Equal(1200, fullImage.Width);
        Assert.Equal(800, fullImage.Height);
    }

    [Fact]
    public async Task CodeFileHistory_UsesProjectRepositoryWhenScopedToCode()
    {
        WriteJob("ASS-900");
        WriteFile(_codeRoot, "src/app.cs", "class App { }\n");
        RunGit(_codeRoot, "add", "-A");
        RunGit(_codeRoot, "commit", "-q", "-m", "feat: add app");
        var firstSha = RunGitCapture(_codeRoot, "rev-parse", "HEAD").Trim();

        WriteFile(_codeRoot, "src/app.cs", "class App { void Run() { } }\n");
        RunGit(_codeRoot, "add", "-A");
        RunGit(_codeRoot, "commit", "-q", "-m", "feat: update app");
        var secondSha = RunGitCapture(_codeRoot, "rev-parse", "HEAD").Trim();

        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var watchPath = Uri.EscapeDataString(_workspaceProjectRoot);
        using var historyResponse = await client.GetAsync($"/api/tasks/ASS-900/files/src/app.cs/history?watchPath={watchPath}&scope=code");
        historyResponse.EnsureSuccessStatusCode();

        using var historyDoc = JsonDocument.Parse(await historyResponse.Content.ReadAsStringAsync());
        var entries = historyDoc.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, entries.Count);
        Assert.Equal(secondSha, entries[0].GetProperty("sha").GetString());
        Assert.Equal("code", entries[0].GetProperty("provenance").GetProperty("source").GetString());
        Assert.Equal("feat: update app", entries[0].GetProperty("message").GetString());
        Assert.Equal(JsonValueKind.Null, entries[0].GetProperty("runIndex").ValueKind);

        using var contentResponse = await client.GetAsync($"/api/tasks/ASS-900/files/src/app.cs?watchPath={watchPath}&scope=code&at={firstSha}");
        contentResponse.EnsureSuccessStatusCode();
        Assert.Equal("class App { }\n", await contentResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ResultHistory_ListsAndReadsTaskFolderVersions()
    {
        var job = WriteJob("ASS-RESULT-HISTORY");
        using var factory = CreateFactory();
        var versions = factory.Services.GetRequiredService<ResultVersionStore>();
        versions.Replace(
            job,
            "# Status\n\n- Result: Partial\n- Case: blocked\n",
            ResultProducer.RunAttempt("1"),
            TaskStates.AutoReview,
            new DateTime(2026, 9, 7, 6, 0, 0, DateTimeKind.Utc));
        versions.Replace(
            job,
            "# Status\n\n- Result: Success\n- Case: feature\n",
            ResultProducer.ReviewAttempt("2"),
            TaskStates.HumanReview,
            new DateTime(2026, 9, 11, 8, 21, 0, DateTimeKind.Utc));

        using var client = factory.CreateClient();
        var watchPath = Uri.EscapeDataString(_workspaceProjectRoot);
        using var listResponse = await client.GetAsync(
            $"/api/tasks/ASS-RESULT-HISTORY/result-history?watchPath={watchPath}");
        listResponse.EnsureSuccessStatusCode();
        using var list = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        var entries = list.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, entries.Count);
        var entry = Assert.Single(entries, candidate =>
            candidate.GetProperty("producerKind").GetString() == ResultProducerKinds.RunAttempt);
        Assert.Equal("local-0002", entry.GetProperty("id").GetString());
        Assert.Equal("run attempt #1", entry.GetProperty("producer").GetString());
        Assert.Equal("Partial", entry.GetProperty("result").GetString());
        Assert.Equal("blocked", entry.GetProperty("case").GetString());
        Assert.Equal(TaskStates.AutoReview, entry.GetProperty("lane").GetString());

        using var readResponse = await client.GetAsync(
            $"/api/tasks/ASS-RESULT-HISTORY/result-history/local-0002?watchPath={watchPath}");
        readResponse.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await readResponse.Content.ReadAsStringAsync());
        Assert.Contains("Result: Partial", document.RootElement.GetProperty("markdown").GetString());
    }

    [Fact]
    public async Task ResultHistory_BackfillsPreviousStatusFromWorkspaceGitWithoutMigration()
    {
        const string id = "ASS-LEGACY-RESULT-HISTORY";
        var job = Path.Combine(_workspaceProjectRoot, TaskStates.Escalated, id);
        Directory.CreateDirectory(job);
        File.WriteAllText(Path.Combine(job, "task.json"), JsonSerializer.Serialize(new
        {
            id,
            title = id,
            state = TaskStates.Escalated,
            order = 1,
            agent = "claude",
            createdAt = DateTime.UtcNow,
        }), Encoding.UTF8);
        File.WriteAllText(Path.Combine(job, "prompt.md"), "Do the thing.\n", Encoding.UTF8);
        var status = Path.Combine(job, "status.md");
        File.WriteAllText(status, "# Status\n\n- Result: Partial\n- Case: blocked\n", Encoding.UTF8);
        RunGit(_workspaceRoot, "add", "-A");
        RunGit(_workspaceRoot, "commit", "-q", "-m", "chore(workspace): record run result");
        var previousSha = RunGitCapture(_workspaceRoot, "rev-parse", "HEAD").Trim();
        var moved = Path.Combine(_workspaceProjectRoot, TaskStates.AutoReview, id);
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        Directory.Move(job, moved);
        job = moved;
        status = Path.Combine(job, "status.md");
        File.WriteAllText(Path.Combine(job, "task.json"), JsonSerializer.Serialize(new
        {
            id,
            title = id,
            state = TaskStates.AutoReview,
            order = 1,
            agent = "claude",
            createdAt = DateTime.UtcNow,
        }), Encoding.UTF8);
        File.WriteAllText(status, "# Status\n\n- Result: Success\n- Case: feature\n", Encoding.UTF8);
        RunGit(_workspaceRoot, "add", "-A");
        RunGit(_workspaceRoot, "commit", "-q", "-m", "chore(workspace): record current result");

        using var factory = CreateFactory();
        var rawHistory = factory.Services.GetRequiredService<TaskFileHistoryService>()
            .GetWorkspaceResultHistory(id, _workspaceProjectRoot);
        Assert.True(rawHistory.Success, rawHistory.Error);
        Assert.Equal(2, rawHistory.Value?.Count);
        using var client = factory.CreateClient();
        var watchPath = Uri.EscapeDataString(_workspaceProjectRoot);
        using var listResponse = await client.GetAsync(
            $"/api/tasks/{id}/result-history?watchPath={watchPath}");
        listResponse.EnsureSuccessStatusCode();
        using var list = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        var entry = Assert.Single(list.RootElement.EnumerateArray());
        Assert.Equal("git-" + previousSha, entry.GetProperty("id").GetString());
        Assert.Equal("workspace-history", entry.GetProperty("source").GetString());
        Assert.Equal("Partial", entry.GetProperty("result").GetString());
        Assert.Equal(TaskStates.Escalated, entry.GetProperty("lane").GetString());

        using var readResponse = await client.GetAsync(
            $"/api/tasks/{id}/result-history/git-{previousSha}?watchPath={watchPath}");
        readResponse.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await readResponse.Content.ReadAsStringAsync());
        Assert.Contains("Case: blocked", document.RootElement.GetProperty("markdown").GetString());
    }

    [Fact]
    public async Task MoveEndpoint_Agt2707Replay_KeepsGeneratedResultByteIdenticalOnAutoReviewEntry()
    {
        const string id = "AGT-2707-REPLAY";
        var source = Path.Combine(_workspaceProjectRoot, TaskStates.Escalated, id);
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "task.json"), JsonSerializer.Serialize(new
        {
            id,
            title = "AGT-2707 result replay",
            state = TaskStates.Escalated,
            order = 1,
            agent = "claude",
            createdAt = DateTime.UtcNow,
        }), Encoding.UTF8);
        File.WriteAllText(Path.Combine(source, "prompt.md"), "Review again.\n", Encoding.UTF8);
        var original = Encoding.UTF8.GetBytes(
            "# Status\n\n- Result: Success\n- Case: blocked\n- Duration: 2h 30m\n- Files: 12\n\n" +
            "## What Was Done\n\n- Original generated Result.\n");
        File.WriteAllBytes(Path.Combine(source, "status.md"), original);
        Directory.CreateDirectory(Path.Combine(_workspaceProjectRoot, TaskStates.AutoReview));

        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var watchPath = Uri.EscapeDataString(_workspaceProjectRoot);
        using var response = await client.PostAsJsonAsync(
            $"/api/tasks/{id}/move?watchPath={watchPath}",
            new MoveJobRequest { TargetState = TaskStates.AutoReview, Reason = "Fresh review attempt." });
        response.EnsureSuccessStatusCode();

        var destination = Path.Combine(_workspaceProjectRoot, TaskStates.AutoReview, id, "status.md");
        Assert.True(File.Exists(destination));
        Assert.Equal(original, File.ReadAllBytes(destination));
    }

    private WebApplicationFactory<Program> CreateFactory()
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["TaskRepository"] = _workspaceRoot,
                        ["WatchPaths:0:Name"] = "agent-taskboard",
                        ["WatchPaths:0:Path"] = _workspaceProjectRoot,
                        ["WatchPaths:0:RootPath"] = _codeRoot,
                        ["WatchPaths:0:RepositoryPath"] = _codeRoot,
                    });
                });
            });
    }

    private string WriteJob(string id)
    {
        var job = Path.Combine(_workspaceProjectRoot, "tasks", "001", id);
        Directory.CreateDirectory(job);
        File.WriteAllText(Path.Combine(job, "task.json"), JsonSerializer.Serialize(new
        {
            id,
            title = id,
            state = TaskStates.HumanReview,
            order = 1,
            agent = "claude",
            createdAt = DateTime.UtcNow,
        }), Encoding.UTF8);
        File.WriteAllText(Path.Combine(job, "prompt.md"), "Do the thing.\n", Encoding.UTF8);
        return job;
    }

    private static void WriteGenerationIndex(string job, int runIndex)
    {
        var metadata = Path.Combine(job, ".metadata");
        Directory.CreateDirectory(metadata);
        var entries = new[]
        {
            new FileGenerationMeta
            {
                File = "code-review.md",
                Kind = "aspect",
                Model = "claude-test",
                Cli = "claude",
                TokensIn = 10,
                TokensOut = 5,
                RunIndex = runIndex,
                StepId = "aspect-code-quality",
            }
        };
        File.WriteAllText(
            Path.Combine(metadata, "files.json"),
            JsonSerializer.Serialize(entries, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            }),
            Encoding.UTF8);
    }

    private static void WriteFile(string root, string relativePath, string content)
    {
        var full = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, Encoding.UTF8);
    }

    private static void InitRepo(string root)
    {
        Directory.CreateDirectory(root);
        RunGit(root, "init", "-q", "-b", "main");
        RunGit(root, "config", "user.email", "test@example.com");
        RunGit(root, "config", "user.name", "test");
        RunGit(root, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(root, "README.md"), "seed\n", Encoding.UTF8);
        RunGit(root, "add", "README.md");
        RunGit(root, "commit", "-q", "-m", "seed");
    }

    private static void RunGit(string cwd, params string[] args)
    {
        var result = RunGitResult(cwd, args);
        Assert.Equal(0, result.Code);
    }

    private static string RunGitCapture(string cwd, params string[] args)
    {
        var result = RunGitResult(cwd, args);
        Assert.Equal(0, result.Code);
        return result.Out;
    }

    private static (string Out, string Err, int Code) RunGitResult(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(15_000);
        return (stdout, stderr, p.ExitCode);
    }
}
