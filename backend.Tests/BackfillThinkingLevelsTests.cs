using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2808 backfill: cards that carry a <c>model</c> from before this policy
/// fix but never got a derived <c>thinkingLevel</c> (AGT-2793, AGT-2807 are
/// the reference cases). Report mode must never write; apply mode must
/// persist a resolved level as policy-derived (<c>thinkingLevelExplicit=false</c>).
/// </summary>
public sealed class BackfillThinkingLevelsTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _watchPath;
    private const string Project = "backfill-test";

    public BackfillThinkingLevelsTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "backfill-thinking-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", Project);
        Directory.CreateDirectory(_watchPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void ReportMode_ListsAffectedCards_AndNeverWrites()
    {
        var (scanner, mutations) = Build();
        SeedLegacyCard(TaskStates.Backlog, "agt-2793-like", "claude-sonnet-5");

        var entries = mutations.BackfillThinkingLevels(apply: false);

        Assert.Single(entries);
        Assert.Equal("agt-2793-like", entries[0].JobId);
        Assert.Equal("claude-sonnet-5", entries[0].Model);
        Assert.False(string.IsNullOrWhiteSpace(entries[0].ResolvedThinkingLevel));

        var info = scanner.FindJob("agt-2793-like", _watchPath);
        Assert.NotNull(info);
        Assert.True(string.IsNullOrWhiteSpace(info!.ThinkingLevel));
    }

    [Fact]
    public void ApplyMode_PersistsResolvedLevel_AsPolicyDerived_AndIsIdempotent()
    {
        var (scanner, mutations) = Build();
        SeedLegacyCard(TaskStates.Backlog, "agt-2807-like", "claude-sonnet-5");

        var applied = mutations.BackfillThinkingLevels(apply: true);
        Assert.Single(applied);

        var info = scanner.FindJob("agt-2807-like", _watchPath);
        Assert.NotNull(info);
        Assert.Equal(applied[0].ResolvedThinkingLevel, info!.ThinkingLevel);
        Assert.False(info.ThinkingLevelExplicit);

        var second = mutations.BackfillThinkingLevels(apply: true);
        Assert.Empty(second);
    }

    [Fact]
    public void SkipsCardsWithNoModel_AndCardsThatAlreadyHaveALevel()
    {
        var (_, mutations) = Build();
        SeedFolder(TaskStates.Backlog, "no-model", model: null, thinkingLevel: null);
        SeedFolder(TaskStates.Backlog, "already-leveled", model: "claude-sonnet-5", thinkingLevel: "low");

        var entries = mutations.BackfillThinkingLevels(apply: false);

        Assert.Empty(entries);
    }

    private void SeedLegacyCard(string state, string slug, string model)
        => SeedFolder(state, slug, model, thinkingLevel: null);

    private void SeedFolder(string state, string slug, string? model, string? thinkingLevel)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        var modelJson = model == null ? "" : $",\"model\":\"{model}\"";
        var levelJson = thinkingLevel == null ? "" : $",\"thinkingLevel\":\"{thinkingLevel}\"";
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\",\"order\":1,\"agent\":\"claude\"{modelJson}{levelJson}}}");
    }

    private (TaskScannerService scanner, TaskMutationService mutations) Build()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var laneMutex = new LaneMutexRegistry(NullLogger<LaneMutexRegistry>.Instance);
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance,
            timeline: null,
            laneMutex: laneMutex);
        return (scanner, mutations);
    }
}
