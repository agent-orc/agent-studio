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
    public void Summary_status_uses_the_host_string_enum_contract()
    {
        var job = Fixture() with { SummaryState = new TaskSummaryState { Status = TaskSummaryStatus.Failed } };
        using var document = JsonDocument.Parse(BoardCardSummary.Serialize(job));
        Assert.Equal("failed", document.RootElement.GetProperty("summaryState")
            .GetProperty("status").GetString());
    }

    [Fact]
    public void Production_shaped_card_keeps_long_detail_off_the_list()
    {
        var job = Fixture() with
        {
            AutoLoop = new AutoLoopSnapshot
            {
                Iteration = 2, MaxIterations = 5,
                LastQuestion = new string('Q', 400), LastReply = new string('R', 20_000),
            },
            PostProcessingChecks = [new LifecycleCheck
                { Name = "build-test-gate", Status = "running", Detail = new string('D', 10_000) }],
            NeedsInput = new NeedsInputStatus(new string('N', 10_000), "Choose a path",
                "attempt-1", null, "/results/needs-input.md"),
            LiveStatus = new TaskLiveStatus
            {
                Attempt = 2,
                NextSteps = Enumerable.Range(0, 20).Select(i => new TaskLiveStepPreview
                    { StepId = $"step-{i}", DisplayName = $"Step {i}" }).ToList(),
            },
            WaitsOn = new WaitsOnStatus
            {
                Blocked = true,
                Items = [new WaitsOnItem { Key = "AGT-1", Resolved = true,
                    TargetTitle = "A prerequisite", TargetJobId = "agt-1" }],
            },
        };
        var bytes = BoardCardSummary.Serialize(job);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        Assert.True(bytes.Length < 2048, $"Production-shaped card was {bytes.Length} bytes");
        Assert.Equal("Choose a path", root.GetProperty("needsInput").GetProperty("message").GetString());
        Assert.False(root.GetProperty("autoLoop").TryGetProperty("lastReply", out _));
        Assert.Equal(3, root.GetProperty("liveStatus").GetProperty("nextSteps").GetArrayLength());
        Assert.Equal(1, root.GetProperty("waitsOn").GetProperty("itemCount").GetInt32());
        Assert.False(root.GetProperty("waitsOn").GetProperty("items")[0]
            .TryGetProperty("unsatisfiableReason", out _));
    }

    [Fact]
    public void Many_dependencies_keep_the_card_bounded_and_preserve_release_gate_labels()
    {
        var job = Fixture() with
        {
            References = new TaskReferences
            {
                DependsOn = Enumerable.Range(0, 100)
                    .Select(i => new TaskDependencyReference($"AGT-{i}", i == 1)).ToList(),
            },
        };
        var bytes = BoardCardSummary.Serialize(job);
        using var document = JsonDocument.Parse(bytes);
        var references = document.RootElement.GetProperty("references");
        Assert.True(bytes.Length < 4096, $"100-edge card was {bytes.Length} bytes");
        Assert.Equal(100, references.GetProperty("dependsOnCount").GetInt32());
        Assert.Equal(3, references.GetProperty("dependsOn").GetArrayLength());
        Assert.Equal(100, references.GetProperty("dependsOnKeys").GetArrayLength());
        Assert.Equal("AGT-99", references.GetProperty("dependsOnKeys")[99].GetString());
        Assert.True(references.GetProperty("dependsOn")[1].GetProperty("releaseGate").GetBoolean());
    }

    [Fact]
    public void Release_targets_include_waits_beyond_the_visible_labels()
    {
        var job = Fixture() with
        {
            WaitsOn = new WaitsOnStatus
            {
                Blocked = true,
                Items = Enumerable.Range(0, 5).Select(i => new WaitsOnItem
                {
                    Key = $"AGT-{i}", Fulfilled = false,
                    WaitingForRelease = i == 4,
                    TargetJobId = $"agt-{i}",
                }).ToList(),
            },
        };
        using var document = JsonDocument.Parse(BoardCardSummary.Serialize(job));
        var waits = document.RootElement.GetProperty("waitsOn");
        Assert.True(waits.GetProperty("anyWaitingForRelease").GetBoolean());
        Assert.Equal(3, waits.GetProperty("items").GetArrayLength());
        Assert.Equal("agt-4", waits.GetProperty("releaseTargets")[0]
            .GetProperty("targetJobId").GetString());
    }

    [Fact]
    public async Task Two_hundred_thirty_five_cards_fit_in_board_budget()
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
        await cache.GetOrBuildAsync("list-generation-1", () => list, CancellationToken.None);
        clock.Restart();
        for (var i = 0; i < 1000; i++)
            await cache.GetOrBuildAsync("list-generation-1", () => throw new Exception("unexpected rebuild"), CancellationToken.None);
        clock.Stop();
        var listLookupMs = clock.Elapsed.TotalMilliseconds / 1000;
        await cache.GetOrBuildAsync("grouped-generation-1", () => grouped, CancellationToken.None);
        clock.Restart();
        for (var i = 0; i < 1000; i++)
            await cache.GetOrBuildAsync("grouped-generation-1", () => throw new Exception("unexpected rebuild"), CancellationToken.None);
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
