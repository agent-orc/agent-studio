using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// HTTP-level coverage for the prompt-management admin surface
/// (<c>/api/admin/prompts*</c>). The service logic is unit-tested in
/// <see cref="PromptAdminServiceTests"/>; this suite exercises the actual
/// minimal-API endpoints end to end (routing, status codes, request/response
/// JSON shape, override-write isolation). Defaults and overrides are pinned to
/// throwaway temp directories via <c>PromptTemplates:RuntimePath/OverridePath</c>
/// so PUT/DELETE never touch the real user-data override store.
/// </summary>
public sealed class PromptAdminEndpointsTests : IDisposable
{
    private const string Template = "runner-fresh-start.md";

    private readonly string _tempDir;
    private readonly string _workspaceRoot;
    private readonly string _workspaceProjectRoot;
    private readonly string _codeRoot;
    private readonly string _defaultsDir;
    private readonly string _overridesDir;

    public PromptAdminEndpointsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "atp-prompt-admin-ep-" + Guid.NewGuid().ToString("N"));
        _workspaceRoot = Path.Combine(_tempDir, "workspace");
        _workspaceProjectRoot = Path.Combine(_workspaceRoot, "projects", "agent-taskboard");
        _codeRoot = Path.Combine(_tempDir, "code");
        _defaultsDir = Path.Combine(_tempDir, "prompt-defaults");
        _overridesDir = Path.Combine(_tempDir, "prompt-overrides");

        Directory.CreateDirectory(_workspaceProjectRoot);
        Directory.CreateDirectory(_codeRoot);
        Directory.CreateDirectory(_defaultsDir);
        Directory.CreateDirectory(_overridesDir);

        // A template that is registered in PromptUsageCatalog (so /detail surfaces
        // usages) and declares slots (so /preview reports filled vs missing).
        File.WriteAllText(Path.Combine(_defaultsDir, Template), "Task {{taskId}} — do {{thing}}.");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Catalog_ListsSeededTemplate()
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);

        using var resp = await client.GetAsync("/api/admin/prompts");
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(items, i => i.GetProperty("name").GetString() == Template
            && i.GetProperty("hasDefault").GetBoolean()
            && !i.GetProperty("hasOverride").GetBoolean());
        Assert.Equal(_overridesDir, doc.RootElement.GetProperty("overrideDirectory").GetString());
    }

    [Fact]
    public async Task Coverage_ReportsCoveredPlusPendingEqualsTotal()
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);

        using var resp = await client.GetAsync("/api/admin/prompts/coverage");
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var total = root.GetProperty("totalSites").GetInt32();
        var covered = root.GetProperty("coveredSites").GetInt32();
        var pending = root.GetProperty("pendingSites").GetInt32();
        Assert.True(total >= 4);
        Assert.Equal(total, covered + pending);
        Assert.Equal(total, root.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Detail_ReturnsSlotsAndUsages_AndUnknownIs404()
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);

        using var ok = await client.GetAsync($"/api/admin/prompts/{Template}");
        ok.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await ok.Content.ReadAsStringAsync());
        var slots = doc.RootElement.GetProperty("slots").EnumerateArray().Select(s => s.GetString()).ToList();
        Assert.Equal(new[] { "taskId", "thing" }, slots);
        Assert.NotEmpty(doc.RootElement.GetProperty("usages").EnumerateArray());

        using var missing = await client.GetAsync("/api/admin/prompts/does-not-exist.md");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Preview_ReportsFilledAndMissingSlots()
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);

        using var resp = await client.PostAsJsonAsync(
            $"/api/admin/prompts/{Template}/preview",
            new { values = new Dictionary<string, string?> { ["taskId"] = "ASS-1741" }, content = (string?)null });
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("Task ASS-1741 — do {{thing}}.", root.GetProperty("rendered").GetString());
        Assert.Equal(new[] { "taskId" }, Names(root, "filledSlots"));
        Assert.Equal(new[] { "thing" }, Names(root, "missingSlots"));
    }

    [Fact]
    public async Task OverrideLifecycle_SaveDetailRebaselineReset_RoundTrips()
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);

        // Save an override.
        using var saved = await client.PutAsJsonAsync(
            $"/api/admin/prompts/{Template}", new { content = "Override {{taskId}}." });
        saved.EnsureSuccessStatusCode();
        using (var doc = JsonDocument.Parse(await saved.Content.ReadAsStringAsync()))
        {
            Assert.True(doc.RootElement.GetProperty("hasOverride").GetBoolean());
            Assert.Equal("Override {{taskId}}.", doc.RootElement.GetProperty("effectiveContent").GetString());
        }
        // Override is written into the isolated temp dir, not the real store.
        Assert.True(File.Exists(Path.Combine(_overridesDir, Template)));

        // Detail now reports the override.
        using var detail = await client.GetAsync($"/api/admin/prompts/{Template}");
        detail.EnsureSuccessStatusCode();
        using (var doc = JsonDocument.Parse(await detail.Content.ReadAsStringAsync()))
            Assert.True(doc.RootElement.GetProperty("hasOverride").GetBoolean());

        // Rebaseline keeps the override but clears any drift flag.
        using var rebaselined = await client.PostAsync($"/api/admin/prompts/{Template}/rebaseline", null);
        rebaselined.EnsureSuccessStatusCode();
        using (var doc = JsonDocument.Parse(await rebaselined.Content.ReadAsStringAsync()))
            Assert.False(doc.RootElement.GetProperty("defaultChangedSinceOverride").GetBoolean());

        // Reset removes the override and returns to the shipped default.
        using var reset = await client.DeleteAsync($"/api/admin/prompts/{Template}");
        reset.EnsureSuccessStatusCode();
        using (var doc = JsonDocument.Parse(await reset.Content.ReadAsStringAsync()))
        {
            Assert.False(doc.RootElement.GetProperty("hasOverride").GetBoolean());
            Assert.Equal("Task {{taskId}} — do {{thing}}.", doc.RootElement.GetProperty("effectiveContent").GetString());
        }
        Assert.False(File.Exists(Path.Combine(_overridesDir, Template)));
    }

    [Fact]
    public async Task SaveOverride_MissingContent_Returns400()
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);

        using var resp = await client.PutAsJsonAsync(
            $"/api/admin/prompts/{Template}", new { notContent = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task ReviewAll_WritesSidecarsAndReturnsFindings()
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);

        using var resp = await client.PostAsJsonAsync(
            "/api/admin/prompts/review-all",
            new { reviewedBy = "endpoint-test" });
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(1, doc.RootElement.GetProperty("reviewedCount").GetInt32());
        Assert.True(doc.RootElement.GetProperty("findingCount").GetInt32() >= 0);
        Assert.True(File.Exists(Path.Combine(_defaultsDir, Template + ".meta.json")));

        using var catalogResp = await client.GetAsync("/api/admin/prompts");
        catalogResp.EnsureSuccessStatusCode();
        using var catalog = JsonDocument.Parse(await catalogResp.Content.ReadAsStringAsync());
        var item = catalog.RootElement.GetProperty("items")[0];
        Assert.Equal(
            "endpoint-test",
            doc.RootElement.GetProperty("results")[0]
                .GetProperty("metadata")
                .GetProperty("reviewedBy")
                .GetString());
        Assert.NotEqual(JsonValueKind.Null, item.GetProperty("lastReviewedAt").ValueKind);
        Assert.True(item.TryGetProperty("lastChangedSha", out _));
    }

    private static string?[] Names(JsonElement root, string property) =>
        root.GetProperty(property).EnumerateArray().Select(s => s.GetString()).ToArray();

    // Reads are anonymous-open; writes (PUT/DELETE and the POST preview/rebaseline)
    // pass the ClientIdentityMiddleware boundary, so send the seeded local identity.
    private static HttpClient CreateClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        return client;
    }

    private WebApplicationFactory<Program> CreateFactory()
    {
        return new TestHostFactory()
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
                        // Pin prompt defaults + overrides to throwaway dirs so the
                        // catalog is deterministic and PUT/DELETE stay isolated.
                        ["PromptTemplates:RuntimePath"] = _defaultsDir,
                        ["PromptTemplates:OverridePath"] = _overridesDir,
                        ["PromptTelemetry:Path"] = Path.Combine(_tempDir, "prompt-calls.jsonl"),
                    });
                });
            });
    }
}
