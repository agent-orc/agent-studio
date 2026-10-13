using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;
using AgentStudio.Tasks;
using Microsoft.Extensions.Time.Testing;
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
    public void A_parked_and_held_card_read_later_serializes_to_the_same_bytes()
    {
        // The two read-time counters (parked age, pickup-hold age) changed
        // every card on every read, so an unchanged board never matched its
        // cached body. The card carries the start instants instead.
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-12T22:20:00Z"));
        var parkedAt = clock.GetUtcNow().UtcDateTime.AddDays(-3);
        var marker = new ParkedBlockerRecord
        {
            BlockerType = "operator-decision", Lane = TaskStates.HumanReview, ParkedAt = parkedAt,
            Reason = "Integrate the current delivery and repeat review.",
            Condition = new ParkedBlockerCondition { Kind = "manual", Description = "Only a person can clear this park." },
        };
        var waitsOn = new WaitsOnStatus
        {
            Blocked = true,
            Items = [new WaitsOnItem { Key = "AGT-1", Resolved = true, TargetState = TaskStates.HumanReview }],
        };
        TaskInfo ReadAt(DateTime now)
        {
            var ready = Fixture() with { State = TaskStates.Ready, EnteredLaneAt = parkedAt, WaitsOn = waitsOn };
            return Fixture() with
            {
                ParkedBlocker = ParkedBlockerMarker.ToStatus(marker, now),
                PickupHold = PickupHoldPolicy.Evaluate(new PickupHoldFacts(ready, waitsOn,
                    IntakeEnabled: false, CrashBackoffUntilUtc: null, Rejection: null, NowUtc: now)),
            };
        }

        var first = ReadAt(clock.GetUtcNow().UtcDateTime);
        clock.Advance(TimeSpan.FromMinutes(7));
        var later = ReadAt(clock.GetUtcNow().UtcDateTime);

        Assert.NotEqual(first.ParkedBlocker!.ParkedForSeconds, later.ParkedBlocker!.ParkedForSeconds);
        Assert.NotEqual(first.PickupHold!.HeldForSeconds, later.PickupHold!.HeldForSeconds);
        Assert.Equal(BoardCardSummary.Serialize(first), BoardCardSummary.Serialize(later));
        using var document = JsonDocument.Parse(BoardCardSummary.Serialize(later));
        Assert.False(document.RootElement.GetProperty("parkedBlocker").TryGetProperty("parkedForSeconds", out _));
        Assert.True(document.RootElement.GetProperty("parkedBlocker").TryGetProperty("parkedAt", out _));
        Assert.False(document.RootElement.GetProperty("pickupHold").TryGetProperty("heldForSeconds", out _));
        Assert.True(document.RootElement.GetProperty("pickupHold").TryGetProperty("sinceUtc", out _));
    }

    /// <summary>
    /// The per-card budget on the cards that weigh the most on a live board:
    /// Human Review cards that are parked and carry an integration
    /// verification, an execution location, a merge signal and waits-on
    /// edges. The fixture is 44 such cards taken from the Agent Studio board on
    /// Stable (12.10.2026), converted back to the task model and sanitized
    /// (text, keys, ids, paths and SHAs replaced by filler of the same length).
    /// Those cards averaged 4.4 KB on the wire before this budget existed.
    /// </summary>
    [Fact]
    public void Production_shaped_human_review_cards_average_under_two_kilobytes()
    {
        var cards = ProductionHumanReviewCards();
        Assert.Equal(44, cards.Length);
        Assert.All(cards, card =>
        {
            Assert.Equal(TaskStates.HumanReview, card.State);
            Assert.NotNull(card.ParkedBlocker);
            Assert.NotNull(card.Integration?.Verification);
            Assert.NotNull(card.ExecutionLocation);
            Assert.NotNull(card.MergeSignal);
            Assert.NotNull(card.WaitsOn);
        });

        var sizes = cards.Select(card => BoardCardSummary.Serialize(card).Length).ToArray();
        var average = sizes.Average();
        output.WriteLine($"44 production-shaped Human Review cards: average {average:F0} B, max {sizes.Max()} B, total {sizes.Sum()} B");
        Assert.True(average < 2048, $"Average card was {average:F0} bytes; budget is 2048");
        Assert.True(sizes.Max() < 3072, $"Largest card was {sizes.Max()} bytes");
    }

    private static TaskInfo[] ProductionHumanReviewCards()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "board-cards", "human-review-cards.sanitized.json");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };
        return JsonSerializer.Deserialize<TaskInfo[]>(File.ReadAllText(path), options)!;
    }

    [Fact]
    public void Detail_facts_stay_off_the_card_and_bounded_facts_keep_their_board_fields()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-12T22:20:00Z"));
        var now = clock.GetUtcNow().UtcDateTime;
        var job = Fixture() with
        {
            ExecutionLocation = new TaskExecutionLocation
            {
                State = TaskExecutionStates.RemoteRunning, ExecutionKind = "remote", RunnerId = "runner-1",
                ConfiguredRunnerId = "runner-1", HostDisplayName = "Runner One", StartedAt = now.AddMinutes(-9),
                LastHeartbeat = now.AddSeconds(-5), LastActivityAt = now, ProcessId = 4242,
                SessionId = "session-1", Branch = "task/agt-3043", WorktreePath = "/workspace/tasks/agt-3043",
                ConnectionState = "connected", LeaseState = "active", TrustReason = new string('T', 120),
            },
            MergeSignal = new TaskMergeSignal
            {
                Branch = "task/agt-3043", InIntegration = true, IntegrationSha = "1234567890abcdef1234",
            },
            Integration = new TaskIntegrationStatus
            {
                Status = IntegrationStatuses.Integrated, DeliveryRef = "agent-studio/results/run_1/fence-1/" + new string('a', 40),
                Sha = "1234567", Verification = new TaskIntegrationVerification
                {
                    State = "integrated-unverified", Sha = new string('b', 40), Reason = new string('R', 300),
                },
            },
            TestEvidence = new TaskTestRunEvidence
            {
                EvidenceState = "proven", Summary = "Review build-tests Pass at 1234567",
                Sources = [new TaskTestEvidenceSource { Kind = "review", Summary = "Review build-tests Pass at 1234567" }],
            },
            ParkedBlocker = new ParkedBlockerStatus("operator-decision", "manual", new string('C', 200),
                now.AddDays(-1), 86400, new string('P', 300), "undeterminable", null, "detail"),
            PendingIntent = new PendingIntent { Mode = "steer", Prompt = new string('S', 4000), SavedAt = now },
            WaitsOn = new WaitsOnStatus
            {
                Blocked = true,
                Items = [new WaitsOnItem { Key = "AGT-1", Resolved = true, WaitingForRelease = true,
                    TargetJobId = "a-long-target-job-id", TargetWatchPath = "/workspace/Agent Studio",
                    TargetTitle = "A prerequisite" }],
            },
        };

        using var document = JsonDocument.Parse(BoardCardSummary.Serialize(job));
        var root = document.RootElement;

        var location = root.GetProperty("executionLocation");
        Assert.Equal("remote-running", location.GetProperty("state").GetString());
        Assert.Equal("runner-1", location.GetProperty("runnerId").GetString());
        Assert.Equal("Runner One", location.GetProperty("hostDisplayName").GetString());
        Assert.Equal("connected", location.GetProperty("connectionState").GetString());
        Assert.True(location.TryGetProperty("lastHeartbeat", out _));
        foreach (var detailOnly in new[] { "branch", "worktreePath", "trustReason", "lastActivityAt", "sessionId" })
            Assert.False(location.TryGetProperty(detailOnly, out _), detailOnly);

        var merge = root.GetProperty("mergeSignal");
        Assert.False(merge.TryGetProperty("branch", out _));
        Assert.Equal("123456789", merge.GetProperty("integrationSha").GetString());
        Assert.False(merge.TryGetProperty("integrationBranch", out _));

        var integration = root.GetProperty("integration");
        Assert.False(integration.TryGetProperty("deliveryRef", out _));
        Assert.Equal(60, integration.GetProperty("verification").GetProperty("reason").GetString()!.Length);
        Assert.Equal("bbbbbbbbb", integration.GetProperty("verification").GetProperty("sha").GetString());

        Assert.False(root.GetProperty("testEvidence").TryGetProperty("sourceLabel", out _));
        var park = root.GetProperty("parkedBlocker");
        Assert.False(park.TryGetProperty("conditionDescription", out _));
        Assert.Equal(100, park.GetProperty("reason").GetString()!.Length);
        var intent = root.GetProperty("pendingIntent");
        Assert.Equal(200, intent.GetProperty("prompt").GetString()!.Length);
        Assert.True(intent.GetProperty("promptTruncated").GetBoolean());
        var item = Assert.Single(root.GetProperty("waitsOn").GetProperty("items").EnumerateArray());
        Assert.False(item.TryGetProperty("targetJobId", out _));
        Assert.False(item.TryGetProperty("targetWatchPath", out _));
        Assert.False(item.TryGetProperty("fulfilled", out _));
        Assert.Equal("AGT-1", root.GetProperty("waitsOn").GetProperty("releaseTargetKeys")[0].GetString());
        Assert.False(root.TryGetProperty("epicId", out _));
    }

    [Theory]
    [InlineData(TaskStates.HumanReview, null, false)]
    [InlineData(TaskStates.Ready, null, false)]
    [InlineData(TaskStates.Ready, "runner-1", true)]
    public void An_idle_location_reaches_the_card_only_where_its_configured_runner_is_read(
        string lane, string? configuredRunnerId, bool onCard)
    {
        var job = Fixture() with
        {
            State = lane,
            ExecutionLocation = new TaskExecutionLocation { ConfiguredRunnerId = configuredRunnerId },
        };
        using var document = JsonDocument.Parse(BoardCardSummary.Serialize(job));
        Assert.Equal(onCard, document.RootElement.TryGetProperty("executionLocation", out _));
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
        Assert.Equal("AGT-4", Assert.Single(waits.GetProperty("releaseTargetKeys").EnumerateArray()).GetString());
        Assert.All(waits.GetProperty("items").EnumerateArray(), item =>
        {
            Assert.False(item.TryGetProperty("targetJobId", out _));
            Assert.False(item.TryGetProperty("targetWatchPath", out _));
        });
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
        await cache.GetOrBuildAsync("list-generation-1", "tasks/list", () => list, CancellationToken.None);
        clock.Restart();
        for (var i = 0; i < 1000; i++)
            await cache.GetOrBuildAsync("list-generation-1", "tasks/list", () => throw new Exception("unexpected rebuild"), CancellationToken.None);
        clock.Stop();
        var listLookupMs = clock.Elapsed.TotalMilliseconds / 1000;
        await cache.GetOrBuildAsync("grouped-generation-1", "tasks/grouped", () => grouped, CancellationToken.None);
        clock.Restart();
        for (var i = 0; i < 1000; i++)
            await cache.GetOrBuildAsync("grouped-generation-1", "tasks/grouped", () => throw new Exception("unexpected rebuild"), CancellationToken.None);
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
