using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.TestHost;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
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
        using var client = CreateClient(factory);
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
        using var client = CreateClient(factory);
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
        using var client = CreateClient(factory);
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
        using var client = CreateClient(factory);
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
        using var client = CreateClient(factory);
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
    public async Task CodeFileHistory_WhenTheGitBudgetIsSpent_ReportsItInsteadOfLettingTheClientAbort()
    {
        // The loaded-host case made deterministic: instead of waiting for a slow
        // host to push the lookup past the client's deadline, the server starts
        // with no budget at all. What matters is the shape of the outcome - a
        // real HTTP response naming the server's own limit, arriving well inside
        // ClientTimeout, rather than the aborted request this test used to fail
        // on (AGT-2867).
        WriteJob("ASS-901");
        WriteFile(_codeRoot, "src/app.cs", "class App { }\n");
        RunGit(_codeRoot, "add", "-A");
        RunGit(_codeRoot, "commit", "-q", "-m", "feat: add app");

        using var factory = CreateFactory(gitCallBudget: TimeSpan.Zero);
        using var client = CreateClient(factory);
        var watchPath = Uri.EscapeDataString(_workspaceProjectRoot);

        var started = Stopwatch.StartNew();
        using var response = await client.GetAsync(
            $"/api/tasks/ASS-901/files/src/app.cs/history?watchPath={watchPath}&scope=code");
        started.Stop();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            TaskFileHistoryService.GitBudgetExhausted,
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
        Assert.True(
            started.Elapsed < ClientTimeout,
            $"The server answered in {started.Elapsed}, which must stay inside the client deadline {ClientTimeout}.");
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

        using var client = CreateClient(factory);
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
        var (id, _, previousSha) = ArrangeLegacyCard();

        using var factory = CreateFactory();
        using var client = CreateClient(factory);
        var watchPath = Uri.EscapeDataString(_workspaceProjectRoot);
        using (var firstOpen = await client.GetAsync($"/api/tasks/{id}/result-history?watchPath={watchPath}"))
        {
            firstOpen.EnsureSuccessStatusCode();
            Assert.Equal(LegacyResultHistoryStates.Pending, LegacyHeader(firstOpen));
        }

        await factory.Services.GetRequiredService<LegacyResultHistoryBackfill>()
            .RunBackfillAsync(id, _workspaceProjectRoot);

        using var listResponse = await client.GetAsync(
            $"/api/tasks/{id}/result-history?watchPath={watchPath}");
        listResponse.EnsureSuccessStatusCode();
        Assert.Equal(LegacyResultHistoryStates.Complete, LegacyHeader(listResponse));
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
    public async Task ResultHistory_AfterACompleteBackfill_ListAndReadStartNoGitProcess()
    {
        var (id, job, previousSha) = ArrangeLegacyCard();
        var git = new RecordingGitRunner();

        using var factory = CreateFactory(runner: git);
        var backfill = factory.Services.GetRequiredService<LegacyResultHistoryBackfill>();
        await backfill.RunBackfillAsync(id, _workspaceProjectRoot);

        var persisted = LegacyResultHistoryFile.Read(job);
        Assert.Equal(LegacyResultHistoryStates.Complete, persisted.State);
        Assert.Equal(2, persisted.Record!.Entries.Count);
        // The commit metadata rides along with the walk: one log, no `show -s`.
        Assert.Single(git.Calls, call => call.StartsWith("log ", StringComparison.Ordinal));
        Assert.DoesNotContain(git.Calls, call => call.StartsWith("show -s", StringComparison.Ordinal));
        // Other lane paths are absent from each commit; that is an answer, not a failure.
        Assert.Contains(git.Calls, call => call.StartsWith("cat-file -e", StringComparison.Ordinal));

        var spawnsAfterBackfill = git.Calls.Count;
        using var client = CreateClient(factory);
        var watchPath = Uri.EscapeDataString(_workspaceProjectRoot);
        using var listResponse = await client.GetAsync($"/api/tasks/{id}/result-history?watchPath={watchPath}");
        listResponse.EnsureSuccessStatusCode();
        Assert.Equal(LegacyResultHistoryStates.Complete, LegacyHeader(listResponse));
        using var readResponse = await client.GetAsync(
            $"/api/tasks/{id}/result-history/git-{previousSha}?watchPath={watchPath}");
        readResponse.EnsureSuccessStatusCode();

        Assert.Equal(spawnsAfterBackfill, git.Calls.Count);
        Assert.Equal(1, backfill.StartedRuns);
    }

    [Fact]
    public async Task ResultHistory_WhenTheBackfillBudgetIsSpent_PersistsFailedAndStillListsLocalVersions()
    {
        const string id = "ASS-RESULT-HISTORY-SPENT";
        var job = WriteJob(id);
        using var factory = CreateFactory(gitCallBudget: TimeSpan.Zero);
        ReplaceResultTwice(factory, job);

        await factory.Services.GetRequiredService<LegacyResultHistoryBackfill>()
            .RunBackfillAsync(id, _workspaceProjectRoot);

        var persisted = LegacyResultHistoryFile.Read(job);
        Assert.Equal(LegacyResultHistoryStates.Failed, persisted.State);
        Assert.Equal(TaskFileHistoryService.GitBudgetExhausted, persisted.Reason);

        using var client = CreateClient(factory);
        var watchPath = Uri.EscapeDataString(_workspaceProjectRoot);
        using var listResponse = await client.GetAsync($"/api/tasks/{id}/result-history?watchPath={watchPath}");
        listResponse.EnsureSuccessStatusCode();
        Assert.Equal(LegacyResultHistoryStates.Failed, LegacyHeader(listResponse));
        using var list = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        Assert.Equal(
            new[] { "local-0001", "local-0002" },
            list.RootElement.EnumerateArray().Select(entry => entry.GetProperty("id").GetString()).Order().ToArray());

        using var readResponse = await client.GetAsync(
            $"/api/tasks/{id}/result-history/git-0123456789abcdef?watchPath={watchPath}");
        Assert.Equal(HttpStatusCode.NotFound, readResponse.StatusCode);
        Assert.Contains("could not be loaded", await readResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResultHistory_WhenOnePerCommitReadFails_PersistsFailedNotComplete()
    {
        var (id, job, _) = ArrangeLegacyCard();
        var git = new RecordingGitRunner
        {
            // The walk itself succeeds; reading one historical status.md does not.
            Override = args => args[0] == "show" && args[^1].EndsWith("/status.md", StringComparison.Ordinal)
                ? TaskHistoryGitResult.Failed(TaskHistoryGitResult.TimedOut)
                : null,
        };

        using var factory = CreateFactory(runner: git);
        await factory.Services.GetRequiredService<LegacyResultHistoryBackfill>()
            .RunBackfillAsync(id, _workspaceProjectRoot);

        var persisted = LegacyResultHistoryFile.Read(job);
        Assert.Equal(LegacyResultHistoryStates.Failed, persisted.State);
        Assert.Equal(TaskHistoryGitResult.TimedOut, persisted.Reason);
        Assert.Empty(persisted.Record!.Entries);

        using var client = CreateClient(factory);
        var watchPath = Uri.EscapeDataString(_workspaceProjectRoot);
        using var listResponse = await client.GetAsync($"/api/tasks/{id}/result-history?watchPath={watchPath}");
        listResponse.EnsureSuccessStatusCode();
        Assert.Equal(LegacyResultHistoryStates.Failed, LegacyHeader(listResponse));
    }

    [Fact]
    public async Task ResultHistory_WithoutAnOutcome_AnswersPendingAtOnceAndSchedulesOneBackfill()
    {
        var (id, job, previousSha) = ArrangeLegacyCard();
        var git = new RecordingGitRunner { Hold = new ManualResetEventSlim(false) };

        using var factory = CreateFactory(runner: git);
        var backfill = factory.Services.GetRequiredService<LegacyResultHistoryBackfill>();
        factory.Services.GetRequiredService<ResultVersionStore>().Replace(
            job,
            "# Status\n\n- Result: Success\n- Case: rework\n",
            ResultProducer.ReviewAttempt("2"),
            TaskStates.AutoReview,
            new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc));
        try
        {
            using var client = CreateClient(factory);
            var watchPath = Uri.EscapeDataString(_workspaceProjectRoot);
            var listUrl = $"/api/tasks/{id}/result-history?watchPath={watchPath}";
            // Both answer while the scheduled backfill is still held inside git.
            var responses = await Task.WhenAll(client.GetAsync(listUrl), client.GetAsync(listUrl));
            foreach (var response in responses)
            {
                using (response)
                {
                    response.EnsureSuccessStatusCode();
                    Assert.Equal(LegacyResultHistoryStates.Pending, LegacyHeader(response));
                    using var list = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    Assert.Equal("local-0001", Assert.Single(list.RootElement.EnumerateArray()).GetProperty("id").GetString());
                }
            }
            Assert.True(git.Entered.Wait(ClientTimeout), "The scheduled backfill never started git.");
            Assert.Equal(1, backfill.StartedRuns);

            using var local = await client.GetAsync($"/api/tasks/{id}/result-history/local-0001?watchPath={watchPath}");
            local.EnsureSuccessStatusCode();
            using var legacy = await client.GetAsync($"/api/tasks/{id}/result-history/git-{previousSha}?watchPath={watchPath}");
            Assert.Equal(HttpStatusCode.NotFound, legacy.StatusCode);
            Assert.Contains("Legacy history not loaded yet.", await legacy.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            // Only the held rev-parse of the one scheduled backfill ever ran.
            Assert.Equal(new[] { "rev-parse --show-toplevel" }, git.Calls.ToArray());
        }
        finally
        {
            git.Hold.Set();
        }

        await backfill.WhenIdleAsync();
        Assert.Equal(1, backfill.StartedRuns);
        Assert.Equal(LegacyResultHistoryStates.Complete, LegacyResultHistoryFile.Read(job).State);
    }

    [Fact]
    public async Task ResultHistory_FailedOutcome_IsRetriedOnceAfterTheRetryInterval()
    {
        const string id = "ASS-RESULT-HISTORY-RETRY";
        var job = WriteJob(id);
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-12T08:00:00Z"));
        using var factory = CreateFactory(gitCallBudget: TimeSpan.Zero, clock: clock);
        var backfill = factory.Services.GetRequiredService<LegacyResultHistoryBackfill>();
        await backfill.RunBackfillAsync(id, _workspaceProjectRoot);
        Assert.Equal(clock.GetUtcNow(), LegacyResultHistoryFile.Read(job).Record!.ComputedAt);

        using var client = CreateClient(factory);
        var listUrl = $"/api/tasks/{id}/result-history?watchPath={Uri.EscapeDataString(_workspaceProjectRoot)}";
        async Task<string?> OpenAsync()
        {
            using var response = await client.GetAsync(listUrl);
            response.EnsureSuccessStatusCode();
            return LegacyHeader(response);
        }

        clock.Advance(LegacyResultHistoryPolicy.RetryInterval - TimeSpan.FromMinutes(1));
        Assert.Equal(LegacyResultHistoryStates.Failed, await OpenAsync());
        Assert.Equal(1, backfill.StartedRuns);

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(LegacyResultHistoryStates.Failed, await OpenAsync());
        Assert.Equal(2, backfill.StartedRuns);
        await backfill.WhenIdleAsync();
        Assert.Equal(LegacyResultHistoryStates.Failed, await OpenAsync());
        Assert.Equal(2, backfill.StartedRuns);
        Assert.Equal(clock.GetUtcNow(), LegacyResultHistoryFile.Read(job).Record!.ComputedAt);
    }

    [Fact]
    public async Task ResultHistory_TruncatedLegacyEntry_IsListedExcludedByStoredHashAndNotReadable()
    {
        const string id = "ASS-RESULT-HISTORY-TRUNCATED";
        var job = WriteJob(id);
        const string current = "# Status\n\n- Result: Success\n- Case: feature\n";
        File.WriteAllText(Path.Combine(job, "status.md"), current, Encoding.UTF8);
        LegacyResultHistoryFile.Write(job, new LegacyResultHistoryRecord(
            LegacyResultHistoryFile.SchemaVersion,
            DateTimeOffset.Parse("2026-10-12T08:00:00Z"),
            LegacyResultHistoryStates.Complete,
            null,
            ElapsedMs: 0,
            [
                LegacyEntry("aaaa1111", "Partial", ResultHistoryContent.Hash("# Status\n\n- Result: Partial\n")),
                LegacyEntry("bbbb2222", "Success", ResultHistoryContent.Hash(current.Replace("\n", "\r\n"))),
            ]));

        var git = new RecordingGitRunner();
        using var factory = CreateFactory(runner: git);
        using var client = CreateClient(factory);
        var watchPath = Uri.EscapeDataString(_workspaceProjectRoot);
        using var listResponse = await client.GetAsync($"/api/tasks/{id}/result-history?watchPath={watchPath}");
        listResponse.EnsureSuccessStatusCode();
        Assert.Equal(LegacyResultHistoryStates.Complete, LegacyHeader(listResponse));
        using var list = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        Assert.Equal("git-aaaa1111", Assert.Single(list.RootElement.EnumerateArray()).GetProperty("id").GetString());

        using var readResponse = await client.GetAsync($"/api/tasks/{id}/result-history/git-aaaa1111?watchPath={watchPath}");
        Assert.Equal(HttpStatusCode.NotFound, readResponse.StatusCode);
        Assert.Contains("too large", await readResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty(git.Calls);
    }

    [Fact]
    public async Task ResultHistory_CorruptLegacyFile_ReadsAsFailedNotAsMissing()
    {
        const string id = "ASS-RESULT-HISTORY-CORRUPT";
        var job = WriteJob(id);
        var legacyPath = LegacyResultHistoryFile.PathFor(job);
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        File.WriteAllText(legacyPath, "{ not json", Encoding.UTF8);

        using var factory = CreateFactory(gitCallBudget: TimeSpan.Zero);
        using var client = CreateClient(factory);
        var watchPath = Uri.EscapeDataString(_workspaceProjectRoot);
        using var listResponse = await client.GetAsync($"/api/tasks/{id}/result-history?watchPath={watchPath}");
        listResponse.EnsureSuccessStatusCode();
        Assert.Equal(LegacyResultHistoryStates.Failed, LegacyHeader(listResponse));

        using var readResponse = await client.GetAsync($"/api/tasks/{id}/result-history/git-aaaa1111?watchPath={watchPath}");
        Assert.Equal(HttpStatusCode.NotFound, readResponse.StatusCode);
        Assert.Contains("could not be loaded", await readResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await factory.Services.GetRequiredService<LegacyResultHistoryBackfill>().WhenIdleAsync();
    }

    private static LegacyResultHistoryEntry LegacyEntry(string sha, string result, string hash) => new(
        sha,
        new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc),
        "run attempt",
        ResultProducerKinds.RunAttempt,
        TaskStates.Escalated,
        result,
        "generic",
        hash,
        Markdown: null,
        Truncated: true);

    private static string? LegacyHeader(HttpResponseMessage response)
        => response.Headers.TryGetValues(LegacyResultHistoryStates.HeaderName, out var values)
            ? values.SingleOrDefault()
            : null;

    private static void ReplaceResultTwice(WebApplicationFactory<Program> factory, string job)
    {
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
    }

    /// <summary>
    /// A card whose previous Result exists only in workspace git: committed in
    /// the escalated lane, then moved to auto-review with a new Result.
    /// </summary>
    private (string Id, string Job, string PreviousSha) ArrangeLegacyCard()
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
        return (id, job, previousSha);
    }

    /// <summary>
    /// Records every git call the history services make and runs it for real,
    /// unless <see cref="Override"/> answers instead or <see cref="Hold"/>
    /// keeps the call waiting.
    /// </summary>
    private sealed class RecordingGitRunner : ITaskHistoryGitRunner
    {
        private readonly ProcessTaskHistoryGitRunner _git = new();
        public ConcurrentQueue<string> Calls { get; } = new();
        public ManualResetEventSlim Entered { get; } = new(false);
        public ManualResetEventSlim? Hold { get; init; }
        public Func<IReadOnlyList<string>, TaskHistoryGitResult?>? Override { get; init; }

        public TaskHistoryGitResult Run(string cwd, IReadOnlyList<string> args, TimeSpan timeout)
        {
            Calls.Enqueue(string.Join(' ', args));
            Entered.Set();
            Hold?.Wait(ClientTimeout);
            return Override?.Invoke(args) ?? _git.Run(cwd, args, timeout);
        }
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
        using var client = CreateClient(factory);
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

    /// <summary>
    /// The deadline every client in this class gets, stated instead of inherited
    /// from <see cref="HttpClient"/>'s 100-second default.
    ///
    /// <para>It has one job: stay above
    /// <see cref="GitCallBudgetPolicy.DefaultTotalBudget"/> so the two clocks are
    /// ordered. A loaded host then always produces a real response carrying the
    /// server's own git failure, instead of the client aborting mid-request and
    /// the assertions below failing on a <see cref="TaskCanceledException"/> that
    /// says nothing about the endpoint (AGT-2867).
    /// <see cref="GitCallBudgetPolicyTests"/> guards the ordering.</para>
    /// </summary>
    internal static readonly TimeSpan ClientTimeout = TimeSpan.FromMinutes(2);

    private HttpClient CreateClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.Timeout = ClientTimeout;
        return client;
    }

    private WebApplicationFactory<Program> CreateFactory(
        TimeSpan? gitCallBudget = null,
        ITaskHistoryGitRunner? runner = null,
        TimeProvider? clock = null)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureTestServices(services =>
                {
                    if (runner is not null)
                        services.AddSingleton(runner);
                    if (gitCallBudget is { } budget)
                    {
                        services.AddSingleton(sp => new TaskFileHistoryService(
                            sp.GetRequiredService<TaskScannerService>(),
                            sp.GetRequiredService<GitService>(),
                            sp.GetRequiredService<ILogger<TaskFileHistoryService>>(),
                            budget,
                            sp.GetRequiredService<ITaskHistoryGitRunner>()));
                    }
                    if (gitCallBudget is not null || clock is not null)
                    {
                        // Only the backfill gets the test clock; the rest of
                        // the host keeps its own.
                        services.AddSingleton(sp => new LegacyResultHistoryBackfill(
                            sp.GetRequiredService<TaskScannerService>(),
                            sp.GetRequiredService<TaskFileHistoryService>(),
                            clock ?? sp.GetRequiredService<TimeProvider>(),
                            sp.GetRequiredService<ILogger<LegacyResultHistoryBackfill>>(),
                            gitCallBudget));
                    }
                });
                builder.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["TaskRepository"] = _workspaceRoot,
                        ["WatchPaths:0:Name"] = "agent-taskboard",
                        ["WatchPaths:0:Path"] = _workspaceProjectRoot,
                        ["WatchPaths:0:RootPath"] = _codeRoot,
                        ["WatchPaths:0:RepositoryPath"] = _codeRoot,
                        ["DeliveryChain:Guarded"] = "false",
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
