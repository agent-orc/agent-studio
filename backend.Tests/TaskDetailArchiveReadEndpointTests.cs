using System.Collections.Concurrent;
using System.Text.Json;
using AgentStudio.Runner;
using AgentStudio.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3109: the card detail polls <c>/runs</c>, <c>/timeline</c> and
/// <c>/steering-feedback</c>. Each request reads the authority projection, and
/// with it the archive shards of the card, exactly once; a card without
/// archived attempts opens no shard at all.
/// </summary>
public sealed class TaskDetailArchiveReadEndpointTests : IDisposable
{
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), "task-detail-archive-read-" + Guid.NewGuid().ToString("N"));
    private readonly string _projectPath;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 13, 10, 0, 0, TimeSpan.Zero));
    private readonly ConcurrentDictionary<string, int> _opens = new(StringComparer.Ordinal);

    public TaskDetailArchiveReadEndpointTests()
    {
        _projectPath = Path.Combine(_workspace, "alpha");
        SeedTask("archived-card", "AGT-7");
        SeedTask("fresh-card", "AGT-8");
        WriteShard("attempt-authority.archive-2026-10-12-08.json", "AGT-7");
        WriteShard("attempt-authority.archive-2026-10-12-09.json", "AGT-5");
    }

    [Fact]
    public async Task Each_detail_read_projects_the_authority_once_and_a_card_without_archives_opens_no_shard()
    {
        await using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var project = factory.Services.GetRequiredService<AgentStudio.Registry.ProjectRegistry>()
            .FindByStorageLocation(_projectPath)!;
        Assert.Equal(2, factory.Services.GetRequiredService<AttemptAuthorityService>().WarmArchiveIndex());

        foreach (var surface in new[] { "timeline", "runs", "steering-feedback" })
        {
            _opens.Clear();
            using var archived = await client.GetAsync(
                $"/api/tasks/archived-card/{surface}?project={Uri.EscapeDataString(project.Id)}");
            archived.EnsureSuccessStatusCode();
            Assert.Equal(
                [new KeyValuePair<string, int>("attempt-authority.archive-2026-10-12-08.json", 1)],
                _opens.ToArray());

            _opens.Clear();
            using var fresh = await client.GetAsync(
                $"/api/tasks/fresh-card/{surface}?project={Uri.EscapeDataString(project.Id)}");
            fresh.EnsureSuccessStatusCode();
            Assert.Empty(_opens);
        }

        using var timeline = await client.GetAsync(
            $"/api/tasks/archived-card/timeline?project={Uri.EscapeDataString(project.Id)}");
        using var body = JsonDocument.Parse(await timeline.Content.ReadAsStringAsync());
        Assert.Contains(body.RootElement.EnumerateArray(), entry =>
            entry.TryGetProperty("details", out var details)
            && details.TryGetProperty("identity", out var identity)
            && identity.GetString() == "run:run-archived:terminal");
    }

    private WebApplicationFactory<Program> BuildFactory() =>
        new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _workspace,
                    ["WatchPaths:0:Name"] = "Alpha Project",
                    ["WatchPaths:0:Path"] = _projectPath,
                    ["WatchPaths:0:RootPath"] = _projectPath,
                }));
            builder.ConfigureTestServices(services =>
                services.AddSingleton(provider => new AttemptAuthorityService(
                    provider.GetRequiredService<IConfiguration>(),
                    provider.GetRequiredService<ILogger<AttemptAuthorityService>>(),
                    () => _time.GetUtcNow().UtcDateTime,
                    writer: null,
                    readArchiveText: path =>
                    {
                        _opens.AddOrUpdate(Path.GetFileName(path), 1, (_, count) => count + 1);
                        return File.ReadAllText(path);
                    })));
        });

    private void SeedTask(string id, string key)
    {
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_projectPath, state));
        var folder = Path.Combine(_projectPath, TaskStates.Ready, id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id,
            key,
            title = "Card " + key,
            state = TaskStates.Ready,
            order = 1,
            agent = "codex",
        }));
    }

    private void WriteShard(string name, string taskKey)
    {
        var metadata = Path.Combine(_workspace, ".metadata");
        Directory.CreateDirectory(metadata);
        var createdAt = _time.GetUtcNow().UtcDateTime.AddDays(-1);
        File.WriteAllText(Path.Combine(metadata, name), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            archivedAt = createdAt,
            runAttempts = new[]
            {
                new
                {
                    attemptId = taskKey == "AGT-7" ? "run-archived" : "run-other",
                    taskKey,
                    repositoryId = "PROJ-1",
                    state = AttemptLifecycleState.Completed,
                    createdAt,
                    terminalAt = createdAt.AddMinutes(30),
                },
            },
            reviewAttempts = Array.Empty<object>(),
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, recursive: true);
    }
}
