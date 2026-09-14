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
    private readonly string _watchPath;
    private readonly ITestOutputHelper _output;

    public BoardConditionalReadTests(ITestOutputHelper output)
    {
        _output = output;
        _watchPath = Path.Combine(Path.GetTempPath(), "atp-board-etag-tests-" + Guid.NewGuid().ToString("N"));
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchPath, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort temp cleanup; a retained temp folder must not fail a run.
        }
    }

    private WebApplicationFactory<Program> BuildFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Test");
            b.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["WatchPaths:0:Name"] = "Agent Task Processor",
                    ["WatchPaths:0:Path"] = _watchPath,
                    ["WatchPaths:0:RootPath"] = _watchPath,
                });
            });
        });

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        return client;
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

    [Fact]
    public async Task OptingOutOfTheLegacyAlias_MakesTheResponseMeasurablySmaller()
    {
        // The duplicated lane is a share of the response proportional to how
        // many cards sit in auto-review, which on a review-heavy board is the
        // largest lane there is.
        for (var i = 0; i < 25; i++)
            Seed($"t-review-{i}", $"Awaiting auto review {i}", TaskStates.AutoReview);
        using var factory = BuildFactory();
        using var client = CreateClient(factory);

        var standard = await GetAsync(client, "/api/tasks/grouped");
        var optedOut = await GetAsync(client, "/api/tasks/grouped?includeLegacyReviewLane=false");
        var validated = await GetAsync(client, "/api/tasks/grouped", standard.ETag);

        _output.WriteLine($"grouped-payload-bytes legacyAlias={standard.Body.Length} "
            + $"optedOut={optedOut.Body.Length} validated={validated.Body.Length} cards=25");

        Assert.True(
            optedOut.Body.Length < standard.Body.Length,
            $"expected the opted-out payload to be smaller, got {optedOut.Body.Length} vs {standard.Body.Length}");
        // The validated poll is the real saving: no body at all.
        Assert.Equal(0, validated.Body.Length);
    }

    [Fact]
    public async Task ListRead_ValidatesTheSameWay()
    {
        Seed("t-1", "First task", TaskStates.Ready);
        using var factory = BuildFactory();
        using var client = CreateClient(factory);

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
