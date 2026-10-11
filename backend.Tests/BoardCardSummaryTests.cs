using System.Text.Json;
using System.Diagnostics;
using AgentStudio.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace AgentStudio.Tests;

public sealed class BoardCardSummaryTests(ITestOutputHelper output)
{
    [Fact]
    public void Representative_card_stays_within_two_kilobytes_and_excludes_detail()
    {
        var job = Fixture();
        var bytes = BoardCardSummary.Serialize(job);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        Assert.True(bytes.Length < 2048, $"Card was {bytes.Length} bytes");
        Assert.Equal(1, root.GetProperty("commitCount").GetInt32());
        Assert.False(root.TryGetProperty("commits", out _));
        Assert.False(root.TryGetProperty("provenance", out _));
        Assert.False(root.TryGetProperty("reviewProjection", out _));
        Assert.False(root.TryGetProperty("tokenSummary", out var token)
            && token.TryGetProperty("entries", out _));
    }

    [Fact]
    public void Two_hundred_thirty_five_cards_fit_in_board_budget()
    {
        var cards = Enumerable.Range(0, 235).Select(i => Fixture() with
        {
            Id = $"agt-{i}",
            TaskKey = $"/workspace/Agent Studio::agt-{i}",
            Key = $"AGT-{i}",
            State = i % 6 == 0 ? TaskStates.AutoReview : TaskStates.HumanReview,
        }).ToArray();
        var clock = Stopwatch.StartNew();
        var list = BoardCardSummary.SerializeList(cards);
        clock.Stop();
        var listBuildMs = clock.Elapsed.TotalMilliseconds;
        var lanes = new Dictionary<string, IReadOnlyList<TaskInfo>>
        {
            ["autoReview"] = cards.Where(card => card.State == TaskStates.AutoReview).ToArray(),
            ["humanReview"] = cards.Where(card => card.State == TaskStates.HumanReview).ToArray(),
            ["review"] = cards.Where(card => card.State == TaskStates.AutoReview).ToArray(),
        };
        clock.Restart();
        var grouped = BoardCardSummary.SerializeGrouped(lanes, null, false);
        clock.Stop();
        var groupedBuildMs = clock.Elapsed.TotalMilliseconds;
        var cache = new BoardReadConcurrencyGate();
        cache.GetOrBuild("list-generation-1", () => list);
        clock.Restart();
        for (var i = 0; i < 1000; i++)
            cache.GetOrBuild("list-generation-1", () => throw new Exception("unexpected rebuild"));
        clock.Stop();
        var listLookupMs = clock.Elapsed.TotalMilliseconds / 1000;
        cache.GetOrBuild("grouped-generation-1", () => grouped);
        clock.Restart();
        for (var i = 0; i < 1000; i++)
            cache.GetOrBuild("grouped-generation-1", () => throw new Exception("unexpected rebuild"));
        clock.Stop();
        output.WriteLine($"235-card synthetic list: {list.Length} bytes; build {listBuildMs:F1} ms; cached lookup {listLookupMs:F4} ms");
        output.WriteLine($"235-card synthetic grouped: {grouped.Length} bytes; build {groupedBuildMs:F1} ms; cached lookup {clock.Elapsed.TotalMilliseconds / 1000:F4} ms");
        Assert.True(list.Length < 500_000, $"List was {list.Length} bytes");
        Assert.True(grouped.Length < 500_000, $"Grouped was {grouped.Length} bytes");
    }

    private static TaskInfo Fixture() => new TaskInfo
        {
            Id = "agt-3043",
            TaskKey = "/workspace/Agent Studio::agt-3043",
            Key = "AGT-3043",
            Title = "Board card list returns slim cards, detail data only on demand",
            State = TaskStates.HumanReview,
            WatchPath = "/workspace/Agent Studio",
            ProjectName = "Agent Studio",
            FolderPath = "/workspace/Agent Studio/projects/Agent Studio/5-human-review/agt-3043",
            Model = "gpt-6-astra",
            CliType = "codex",
            Tags = ["type:feature", "code-review:grade-a"],
            Commits = [new TaskCommitInfo
            {
                ShortSha = "1234567",
                Message = "fix: serialize compact board cards\n\nLong commit body should stay in detail",
                FilesChanged = 3,
                Files = ["backend/Features/Tasks/TaskCrudEndpoints.cs"],
            }],
            Provenance = new TaskProvenance
            {
                Branch = "task/agt-3043",
                Transitions = [new TaskProvenanceTransition { BranchTip = "1234567890abcdef" }],
            },
            TokenSummary = new TaskTokenSummary
            {
                InputTokens = 12000,
                OutputTokens = 3000,
                Entries = [new TaskTokenCall { Model = "gpt-6-astra", InputTokens = 12000 }],
            },
        };
}
