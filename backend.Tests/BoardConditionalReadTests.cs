using System.Net;
using System.Text.Json;

using AgentStudio.Tasks;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Xunit;
using Xunit.Abstractions;

namespace AgentStudio.Tests;

/// <summary>
/// End-to-end coverage for the AGT-2703 conditional board read. The board poll
/// is the largest response this API produces (~1.9 MB measured), and an
/// unchanged board re-sent all of it every two seconds. What is pinned here:
/// <list type="bullet">
///   <item>an unchanged board answers <c>304</c> with an empty body;</item>
///   <item>a changed board answers <c>200</c> under a new tag;</item>
///   <item>the same board under a different request variant never reuses a
///   tag, so an opted-out client cannot be served the opted-in payload;</item>
///   <item><c>includeLegacyReviewLane=false</c> drops the duplicated
///   auto-review lane while the default request keeps the pre-ADR-0025
///   contract;</item>
///   <item>the list read (<c>GET /api/tasks/</c>) validates the same way.</item>
/// </list>
/// </summary>
public sealed class BoardConditionalReadTests : IDisposable
{
    private const string ProjectName = "board-etag-test";

    private readonly string _workspace;
    private readonly string _watchPath;
    private readonly ITestOutputHelper _output;

    public BoardConditionalReadTests(ITestOutputHelper output)
    {
        _output = output;
        _workspace = Path.Combine(Path.GetTempPath(), "atp-board-etag-tests-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", ProjectName);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort temp cleanup; a retained temp folder must not fail a run.
        }
    }

    /// <summary>
    /// Isolates both stores this class reads, not just the watch path.
    /// <c>TaskRepository</c> matters as much as <c>WatchPaths</c> here: left
    /// unset it resolves to the machine-wide LocalAppData
    /// <c>project-settings.json</c>, which every other host in the run that
    /// does not isolate also reads and writes. The board ETag folds
    /// <c>ProjectSettingsService.Version</c> and the settings themselves reach
    /// the response through the lane sort and the live-status projection, so a
    /// shared settings file makes this class's results depend on what ran
    /// beside it. Pointing it at the per-test temp workspace, the way
    /// <c>MergeEndpointsIntegrationTests</c> does, removes that coupling.
    /// </summary>
    private WebApplicationFactory<Program> BuildFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Test");
            b.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _workspace,
                    ["WatchPaths:0:Name"] = ProjectName,
                    ["WatchPaths:0:Path"] = _watchPath,
                    ["WatchPaths:0:RootPath"] = _watchPath,
                    // The two recurring background clocks, pushed past the
                    // lifetime of a test. Both re-publish an input the
                    // validator folds in even when the board did not move:
                    // the task index bumps its generation on every safety
                    // rescan, including one that found nothing changed, and
                    // the Git-state sweep can start another index run. A
                    // conditional pair whose two reads straddle either
                    // boundary then answers 200 for a board that stood still -
                    // measured directly: with the safety TTL at 1 s the second
                    // read sees generation 3 where the first saw 2. These
                    // tests run for tens of seconds under a loaded suite,
                    // which is long enough to reach the 30 s default. What
                    // this class pins is the validator, not those timers -
                    // ChangedBoard_AnswersTwoHundredUnderANewTag drives the
                    // index explicitly instead of waiting for one - so they
                    // are moved out of the way rather than raced with.
                    ["TaskIndexCache:SafetyTtlSeconds"] = "3600",
                    ["GitStateIndex:SweepIntervalSeconds"] = "3600",
                });
            });
        });

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        return client;
    }

    /// <summary>
    /// Blocks until the host's background index work has settled, so a
    /// conditional pair taken afterwards measures the validator rather than
    /// boot scheduling.
    ///
    /// <para>The <c>GitStateIndexService</c> runs a "startup" pass per
    /// repository as soon as the host is up, publishing first the
    /// mid-refresh marker and then the snapshot. Both are folded into the
    /// board validator by <c>BoardReadSignatureSource</c> - correctly, since
    /// the response carries <c>gitStateAt</c> and <c>stale</c> - so a read
    /// taken while that pass is still in flight is a read of a board that is
    /// still moving, and the next one cannot validate against it. Measured:
    /// holding the pass back past the first read takes the projection
    /// generation from 0 to 2 between two reads of an unchanged board, and
    /// the second answers 200. On an idle host the pass finishes about a
    /// second before the first request, which is the entire margin a loaded
    /// suite erases.</para>
    ///
    /// <para>The wait is on what the product publishes, not on a sleep: the
    /// indexer's own per-repository status (<see
    /// cref="GitStateIndexService.GetRepositoryStatuses"/>, the same surface
    /// the Admin git-telemetry page reads) plus the generation counters the
    /// validator itself folds in. Quiescence is "every repository has a
    /// snapshot and none is refreshing, and no generation moved across a quiet
    /// window".</para>
    /// </summary>
    private static async Task WaitForBoardQuiescenceAsync(WebApplicationFactory<Program> factory)
    {
        var indexer = factory.Services.GetRequiredService<GitStateIndexService>();
        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        var gitProjection = factory.Services.GetRequiredService<TaskListGitProjectionCache>();
        var sidecars = factory.Services.GetRequiredService<TaskSidecarGeneration>();
        var settings = factory.Services.GetRequiredService<ProjectSettingsService>();

        (long Tasks, long Git, long Sidecars, long Settings) Generations()
            => (scanner.SnapshotGeneration, gitProjection.Generation, sidecars.Generation, settings.Version);

        // Long enough to survive a fully saturated host, short enough that a
        // genuinely stuck indexer fails the test instead of hanging the run.
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        var quietWindow = TimeSpan.FromSeconds(1);

        var settledSince = (DateTime?)null;
        var lastGenerations = Generations();
        while (true)
        {
            var statuses = indexer.GetRepositoryStatuses();
            var indexed = statuses.Count > 0
                && statuses.All(status => status.GitStateAt is not null && !status.Refreshing);
            var generations = Generations();

            if (indexed && generations == lastGenerations)
            {
                settledSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - settledSince >= quietWindow) return;
            }
            else
            {
                settledSince = null;
                lastGenerations = generations;
            }

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(
                    "The board did not reach a quiescent state: "
                    + $"repositories=[{string.Join(";", statuses.Select(s => $"{s.ProjectName}:indexed={s.GitStateAt is not null}:refreshing={s.Refreshing}"))}] "
                    + $"generations={generations}");

            await Task.Delay(25);
        }
    }

    /// <summary>
    /// Returns once <paramref name="route"/> has actually stopped moving: two
    /// back-to-back reads answer with the same validator.
    ///
    /// <para><see cref="WaitForBoardQuiescenceAsync"/> pins the two recurring
    /// clocks and the Git-state startup pass, which is what a *loaded* host
    /// used to lose. It cannot pin the last input: boot writes inside the watch
    /// path (the registry file, the rebuilt layout index, the log directory)
    /// reach the task index through the FileSystemWatcher's debounced path, and
    /// under load that delivery arrives after the generation counters have
    /// already looked still. Measured here: one run in ten of
    /// <c>ListRead_ValidatesTheSameWay</c> still answered 200 to the second read
    /// with the quiescence wait alone.</para>
    ///
    /// <para>So the board's own validator is the thing waited on. This does not
    /// soften what the class pins: the assertion afterwards is still a single
    /// conditional pair that must answer 304. A validator that changed on every
    /// read of an unchanged board - the regression these tests exist to catch -
    /// never produces two equal tags, so this wait times out and fails the test
    /// rather than papering over it.</para>
    /// </summary>
    private static async Task WaitForSettledValidatorAsync(HttpClient client, string route)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        var previous = (await GetAsync(client, route)).ETag;
        while (true)
        {
            var current = (await GetAsync(client, route)).ETag;
            if (!string.IsNullOrWhiteSpace(current) && current == previous) return;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(
                    $"The validator for {route} never settled: last two tags were "
                    + $"'{previous}' and '{current}'.");
            previous = current;
            await Task.Delay(25);
        }
    }

    private void Seed(string slug, string title, string state)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "task.json"),
            JsonSerializer.Serialize(new { id = slug, title, state, order = 1, agent = "claude" }));
    }

    private static async Task<(HttpStatusCode Status, string? ETag, string Body)> GetAsync(
        HttpClient client, string url, string? ifNoneMatch = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (ifNoneMatch is not null) request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        using var response = await client.SendAsync(request);
        return (response.StatusCode, response.Headers.ETag?.ToString(), await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnchangedBoard_AnswersNotModifiedWithAnEmptyBody()
    {
        Seed("t-1", "First task", TaskStates.Ready);
        using var factory = BuildFactory();
        using var client = CreateClient(factory);
        await WaitForBoardQuiescenceAsync(factory);
        await WaitForSettledValidatorAsync(client, "/api/tasks/grouped");

        var first = await GetAsync(client, "/api/tasks/grouped");
        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.False(string.IsNullOrWhiteSpace(first.ETag));
        Assert.Contains("t-1", first.Body, StringComparison.Ordinal);

        var second = await GetAsync(client, "/api/tasks/grouped", first.ETag);

        Assert.Equal(HttpStatusCode.NotModified, second.Status);
        Assert.Equal(string.Empty, second.Body);
        // The validator is re-stated so the client can keep polling conditionally.
        Assert.Equal(first.ETag, second.ETag);
    }

    [Fact]
    public async Task ChangedBoard_AnswersTwoHundredUnderANewTag()
    {
        Seed("t-1", "First task", TaskStates.Ready);
        using var factory = BuildFactory();
        using var client = CreateClient(factory);
        await WaitForBoardQuiescenceAsync(factory);
        await WaitForSettledValidatorAsync(client, "/api/tasks/grouped");

        var before = await GetAsync(client, "/api/tasks/grouped");

        Seed("t-2", "Second task", TaskStates.Ready);
        // Stand in for the watcher event the FileSystemWatcher would deliver, so
        // the assertion is about the validator rather than about debounce timing.
        factory.Services.GetRequiredService<TaskIndexCache>().ForceRefresh();

        var after = await GetAsync(client, "/api/tasks/grouped", before.ETag);

        Assert.Equal(HttpStatusCode.OK, after.Status);
        Assert.NotEqual(before.ETag, after.ETag);
        Assert.Contains("t-2", after.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestVariantsDoNotShareATag()
    {
        Seed("t-1", "First task", TaskStates.AutoReview);
        using var factory = BuildFactory();
        using var client = CreateClient(factory);
        await WaitForBoardQuiescenceAsync(factory);

        var standard = await GetAsync(client, "/api/tasks/grouped");
        var withoutAlias = await GetAsync(client, "/api/tasks/grouped?includeLegacyReviewLane=false");
        var withFixtures = await GetAsync(client, "/api/tasks/grouped?includeFixtures=true");

        Assert.NotEqual(standard.ETag, withoutAlias.ETag);
        Assert.NotEqual(standard.ETag, withFixtures.ETag);

        // Holding one variant's tag must never validate another variant.
        var crossVariant = await GetAsync(
            client, "/api/tasks/grouped?includeLegacyReviewLane=false", standard.ETag);
        Assert.Equal(HttpStatusCode.OK, crossVariant.Status);
    }

    [Fact]
    public async Task LegacyReviewAlias_IsServedByDefaultAndDroppedOnOptOut()
    {
        Seed("t-review", "Awaiting auto review", TaskStates.AutoReview);
        using var factory = BuildFactory();
        using var client = CreateClient(factory);

        using var standard = JsonDocument.Parse((await GetAsync(client, "/api/tasks/grouped")).Body);
        var standardRoot = standard.RootElement;
        Assert.Equal(1, standardRoot.GetProperty("autoReview").GetArrayLength());
        // Pre-ADR-0025 clients still get the populated alias, byte for byte.
        Assert.Equal(1, standardRoot.GetProperty("review").GetArrayLength());

        using var optedOut = JsonDocument.Parse(
            (await GetAsync(client, "/api/tasks/grouped?includeLegacyReviewLane=false")).Body);
        var optedOutRoot = optedOut.RootElement;
        Assert.Equal(1, optedOutRoot.GetProperty("autoReview").GetArrayLength());
        // The key stays so the payload still parses; the duplicated lane is gone.
        Assert.Equal(0, optedOutRoot.GetProperty("review").GetArrayLength());
    }

    /// <summary>
    /// What the opt-out is worth, asserted on structure rather than on a
    /// comparison of two responses' byte totals.
    ///
    /// <para>Comparing <c>standard.Body.Length</c> against
    /// <c>optedOut.Body.Length</c> looks like the direct measurement, but the
    /// two numbers come from two board reads and therefore from two board
    /// states. Several ETag inputs move with no file changing - the git
    /// projection publishes its first index asynchronously, and on this
    /// 25-card auto-review board the post-processing queue positions folded in
    /// per card shift as the background worker drains them. Under a loaded
    /// full-suite run that lands between the reads, and the size comparison
    /// then measures scheduling instead of the payload. The duplicated lane is
    /// measured inside a single response instead, where nothing can move it.</para>
    /// </summary>
    [Fact]
    public async Task OptingOutOfTheLegacyAlias_RemovesASecondCopyOfTheAutoReviewLane()
    {
        // The duplicated lane is a share of the response proportional to how
        // many cards sit in auto-review, which on a review-heavy board is the
        // largest lane there is.
        const int cards = 25;
        for (var i = 0; i < cards; i++)
            Seed($"t-review-{i}", $"Awaiting auto review {i}", TaskStates.AutoReview);
        using var factory = BuildFactory();
        using var client = CreateClient(factory);
        await WaitForBoardQuiescenceAsync(factory);
        await WaitForSettledValidatorAsync(client, "/api/tasks/grouped");

        var standard = await GetAsync(client, "/api/tasks/grouped");
        var optedOut = await GetAsync(client, "/api/tasks/grouped?includeLegacyReviewLane=false");
        var validated = await GetAsync(client, "/api/tasks/grouped", standard.ETag);

        using var standardDoc = JsonDocument.Parse(standard.Body);
        using var optedOutDoc = JsonDocument.Parse(optedOut.Body);
        var standardAutoReview = standardDoc.RootElement.GetProperty("autoReview");
        var standardAlias = standardDoc.RootElement.GetProperty("review");

        // The default request keeps the pre-ADR-0025 contract: the alias is
        // populated, and it carries the auto-review lane a second time.
        Assert.Equal(cards, standardAutoReview.GetArrayLength());
        Assert.Equal(cards, standardAlias.GetArrayLength());
        // Byte for byte the same lane, which is what makes it pure overhead.
        Assert.Equal(standardAutoReview.GetRawText(), standardAlias.GetRawText());

        // The opt-out keeps the key - an older client still parses the payload -
        // and stops paying for the duplicate.
        Assert.Equal(cards, optedOutDoc.RootElement.GetProperty("autoReview").GetArrayLength());
        Assert.Equal(0, optedOutDoc.RootElement.GetProperty("review").GetArrayLength());

        var aliasBytes = standardAlias.GetRawText().Length;
        _output.WriteLine($"grouped-payload-bytes legacyAlias={standard.Body.Length} "
            + $"optedOut={optedOut.Body.Length} aliasLane={aliasBytes} "
            + $"validated={validated.Body.Length} validatedStatus={validated.Status} cards={cards}");

        // "Measurably smaller" stated against the one response it was measured
        // in: on a review-heavy board the alias is not a rounding error, it is
        // a large fraction of everything sent. Both sides come from
        // standard.Body, so no concurrent board movement can affect the ratio.
        Assert.True(
            aliasBytes > standard.Body.Length / 4,
            $"expected the alias lane to be a large share of the response, "
            + $"got {aliasBytes} of {standard.Body.Length} bytes");

        // The validated poll is the real saving. Whether it can validate at all
        // depends on those same background inputs, so the invariant asserted
        // here is the protocol one, which holds either way; that an idle board
        // does validate is pinned by UnchangedBoard_AnswersNotModifiedWithAnEmptyBody.
        if (validated.Status == HttpStatusCode.NotModified)
            Assert.Equal(0, validated.Body.Length);
        else
            Assert.Equal(HttpStatusCode.OK, validated.Status);
    }

    [Fact]
    public async Task ListRead_ValidatesTheSameWay()
    {
        Seed("t-1", "First task", TaskStates.Ready);
        using var factory = BuildFactory();
        using var client = CreateClient(factory);
        await WaitForBoardQuiescenceAsync(factory);
        await WaitForSettledValidatorAsync(client, "/api/tasks/");

        var first = await GetAsync(client, "/api/tasks/");
        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.False(string.IsNullOrWhiteSpace(first.ETag));

        var second = await GetAsync(client, "/api/tasks/", first.ETag);

        Assert.Equal(HttpStatusCode.NotModified, second.Status);
        Assert.Equal(string.Empty, second.Body);
    }

    [Fact]
    public async Task GitStateHeadersSurviveANotModifiedAnswer()
    {
        Seed("t-1", "First task", TaskStates.Ready);
        using var factory = BuildFactory();
        using var client = CreateClient(factory);
        await WaitForBoardQuiescenceAsync(factory);
        await WaitForSettledValidatorAsync(client, "/api/tasks/grouped");

        var first = await GetAsync(client, "/api/tasks/grouped");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/tasks/grouped");
        request.Headers.TryAddWithoutValidation("If-None-Match", first.ETag);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        // The board reads freshness from headers, not from the body, so a
        // validated response still has to carry it.
        Assert.True(response.Headers.Contains("X-Git-State-Stale"));
    }
}
