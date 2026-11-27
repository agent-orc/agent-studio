using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The durable resume point behind AGT-2860. Its whole reason to exist is that
/// it survives the process that wrote it, so the stage machine has to be
/// monotonic and a record from an older delivery generation must never be
/// mistaken for the current one.
/// </summary>
public sealed class RemoteDeliverySettlementStoreTests : IDisposable
{
    private readonly string _folder;

    public RemoteDeliverySettlementStoreTests()
    {
        _folder = Path.Combine(
            Path.GetTempPath(), "remote-delivery-settlement-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    private static RemoteDeliverySettlementRecord Record(
        string attemptId = "review_a",
        RemoteDeliverySettlementStage stage = RemoteDeliverySettlementStage.IntegrationPending)
        => new()
        {
            TaskKey = "AGT-2855",
            ReviewAttemptId = attemptId,
            Outcome = nameof(ReviewTerminalOutcome.Pass),
            ShouldIntegrate = true,
            BuildTestGate = nameof(RemoteBuildTestGateClass.Passed),
            GateReason = "All applicable Remote Review build/test gates passed.",
            IntegrationBranch = "develop",
            IntegrationStrategy = "merge",
            PipelineType = "coding",
            DeliveredAtUtc = new DateTimeOffset(2026, 9, 17, 8, 39, 0, TimeSpan.Zero),
            Stage = stage,
            RecordedAtUtc = new DateTimeOffset(2026, 9, 17, 8, 39, 1, TimeSpan.Zero),
        };

    [Fact]
    public void A_written_record_round_trips_every_field_the_resume_needs()
    {
        RemoteDeliverySettlementStore.Write(_folder, Record());

        var read = RemoteDeliverySettlementStore.Read(_folder)!;

        Assert.Equal("review_a", read.ReviewAttemptId);
        Assert.True(read.ShouldIntegrate);
        Assert.Equal("develop", read.IntegrationBranch);
        Assert.Equal("merge", read.IntegrationStrategy);
        Assert.Equal("coding", read.PipelineType);
        Assert.Equal(
            new DateTimeOffset(2026, 9, 17, 8, 39, 0, TimeSpan.Zero),
            read.DeliveredAtUtc);
        Assert.Equal(RemoteDeliverySettlementStage.IntegrationPending, read.Stage);
    }

    [Fact]
    public void A_missing_record_reads_as_absent_rather_than_throwing()
        => Assert.Null(RemoteDeliverySettlementStore.Read(_folder));

    [Fact]
    public void A_torn_record_reads_as_absent_rather_than_throwing()
    {
        RemoteDeliverySettlementStore.Write(_folder, Record());
        File.WriteAllText(RemoteDeliverySettlementStore.PathFor(_folder), "{ \"stage\": ");

        Assert.Null(RemoteDeliverySettlementStore.Read(_folder));
    }

    [Fact]
    public void Advancing_records_the_integration_outcome_and_park_reason()
    {
        RemoteDeliverySettlementStore.Write(_folder, Record());

        Assert.True(RemoteDeliverySettlementStore.Advance(
            _folder,
            RemoteDeliverySettlementStage.IntegrationSettled,
            "AgentRoundRequired",
            "automatic recovery budget used: 2/2"));

        var read = RemoteDeliverySettlementStore.Read(_folder)!;
        Assert.Equal(RemoteDeliverySettlementStage.IntegrationSettled, read.Stage);
        Assert.Equal("AgentRoundRequired", read.IntegrationOutcome);
        Assert.Equal("automatic recovery budget used: 2/2", read.IntegrationDetail);
    }

    [Fact]
    public void The_stage_never_rewinds_when_a_resumed_pass_races_the_original_request()
    {
        RemoteDeliverySettlementStore.Write(
            _folder, Record(stage: RemoteDeliverySettlementStage.LaneSettled));

        RemoteDeliverySettlementStore.Advance(
            _folder, RemoteDeliverySettlementStage.IntegrationPending);

        Assert.Equal(
            RemoteDeliverySettlementStage.LaneSettled,
            RemoteDeliverySettlementStore.Read(_folder)!.Stage);
    }

    [Fact]
    public void Advancing_a_folder_without_a_record_reports_that_there_was_nothing_to_advance()
        => Assert.False(RemoteDeliverySettlementStore.Advance(
            _folder, RemoteDeliverySettlementStage.LaneSettled));

    [Fact]
    public void A_record_from_an_older_delivery_generation_is_not_the_current_one()
    {
        var record = Record(attemptId: "review_old");

        Assert.True(RemoteDeliverySettlementStore.MatchesAttempt(record, "review_old"));
        Assert.False(RemoteDeliverySettlementStore.MatchesAttempt(record, "review_new"));
        Assert.False(RemoteDeliverySettlementStore.MatchesAttempt(record, null));
        Assert.False(RemoteDeliverySettlementStore.MatchesAttempt(null, "review_old"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_readers_observe_pending_but_the_later_integration_write_cannot_rewind_the_lane(bool recordIntegration)
    {
        RemoteDeliverySettlementStore.Write(_folder, Record());
        var bothRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var laneWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readers = 0;
        async Task<RemoteDeliverySettlementRecord> Snapshot(string path)
        {
            var snapshot = RemoteDeliverySettlementStore.Read(path)!;
            Assert.Equal(RemoteDeliverySettlementStage.IntegrationPending, snapshot.Stage);
            if (Interlocked.Increment(ref readers) == 2) bothRead.SetResult();
            await bothRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return snapshot;
        }

        var oldCompletion = Task.Run(async () =>
        {
            var snapshot = await Snapshot(Path.Combine(_folder, "."));
            await laneWritten.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return recordIntegration
                ? RemoteDeliverySettlementStore.RecordIntegration(_folder, snapshot, "old-result", "old-detail")
                : RemoteDeliverySettlementStore.Advance(_folder, snapshot,
                    RemoteDeliverySettlementStage.IntegrationSettled, "old-result", "old-detail");
        });
        var laneCompletion = Task.Run(async () =>
        {
            var snapshot = await Snapshot(_folder);
            try
            {
                Assert.NotNull(RemoteDeliverySettlementStore.Advance(_folder, snapshot,
                    RemoteDeliverySettlementStage.LaneSettled, "Merged"));
            }
            finally { laneWritten.TrySetResult(); }
        });
        await Task.WhenAll(oldCompletion, laneCompletion);

        Assert.Null(await oldCompletion);
        var current = RemoteDeliverySettlementStore.Read(_folder)!;
        Assert.Equal(RemoteDeliverySettlementStage.LaneSettled, current.Stage);
        Assert.Equal("Merged", current.IntegrationOutcome);
        Assert.Null(current.IntegrationDetail);
        Assert.Equal(1, current.Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_generation_replaced_while_integration_is_paused_is_untouched_by_the_old_completion(bool recordIntegration)
    {
        RemoteDeliverySettlementStore.Write(_folder, Record());
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldCompletion = Task.Run(async () =>
        {
            var expected = RemoteDeliverySettlementStore.Read(_folder)!;
            read.SetResult();
            await complete.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return recordIntegration
                ? RemoteDeliverySettlementStore.RecordIntegration(_folder, expected, "old-result", "old-detail")
                : RemoteDeliverySettlementStore.Advance(_folder, expected,
                    RemoteDeliverySettlementStage.IntegrationSettled, "old-result", "old-detail");
        });
        await read.Task.WaitAsync(TimeSpan.FromSeconds(10));
        byte[] replacement;
        try
        {
            RemoteDeliverySettlementStore.Write(_folder, Record("review_successor"));
            replacement = File.ReadAllBytes(RemoteDeliverySettlementStore.PathFor(_folder));
        }
        finally { complete.TrySetResult(); }

        Assert.Null(await oldCompletion);
        Assert.Equal(replacement, File.ReadAllBytes(RemoteDeliverySettlementStore.PathFor(_folder)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reopening_the_same_review_invalidates_completions_from_before_the_retry(bool infrastructure)
    {
        RemoteDeliverySettlementStore.Write(_folder, Record(stage: RemoteDeliverySettlementStage.LaneSettled) with
        {
            IntegrationOutcome = "Error",
            IntegrationDetail = "host timeout",
        });
        var old = RemoteDeliverySettlementStore.Read(_folder)!;
        var reopened = infrastructure
            ? RemoteDeliverySettlementStore.RequeueInfrastructure(_folder, old)
            : RemoteDeliverySettlementStore.ReopenForRetry(_folder, old);

        Assert.NotNull(reopened);
        Assert.Equal(RemoteDeliverySettlementStage.IntegrationPending, reopened.Stage);
        Assert.True(reopened.DiscardCompletedReplay);
        Assert.Null(reopened.IntegrationOutcome);
        Assert.Null(reopened.IntegrationDetail);
        Assert.Equal(old.Revision + 1, reopened.Revision);
        Assert.Null(RemoteDeliverySettlementStore.RecordIntegration(_folder, old, "stale", "stale-detail"));
        Assert.Null(RemoteDeliverySettlementStore.Advance(_folder, old, RemoteDeliverySettlementStage.LaneSettled));
        Assert.Null(RemoteDeliverySettlementStore.ReopenForRetry(_folder, old));
        Assert.Null(RemoteDeliverySettlementStore.RequeueInfrastructure(_folder, old));

        var integrated = RemoteDeliverySettlementStore.RecordIntegration(_folder, reopened, "Merged", null)!;
        Assert.Equal(RemoteDeliverySettlementStage.IntegrationSettled, integrated.Stage);
        Assert.False(integrated.DiscardCompletedReplay);
        Assert.Null(integrated.IntegrationDetail);
        Assert.NotNull(RemoteDeliverySettlementStore.Advance(_folder, integrated,
            RemoteDeliverySettlementStage.LaneSettled));
    }

    [Fact]
    public void A_pending_infrastructure_retry_also_invalidates_earlier_completions()
    {
        RemoteDeliverySettlementStore.Write(_folder, Record());
        var before = RemoteDeliverySettlementStore.Read(_folder)!;
        var reopened = RemoteDeliverySettlementStore.RequeueInfrastructure(_folder, before)!;
        Assert.Equal(before.Revision + 1, reopened.Revision);
        Assert.Null(RemoteDeliverySettlementStore.RecordIntegration(_folder, before, "old-result", null));
    }

    [Fact]
    public void A_refused_infrastructure_move_can_restore_only_its_own_unchanged_retry()
    {
        RemoteDeliverySettlementStore.Write(_folder, Record(stage: RemoteDeliverySettlementStage.LaneSettled) with
        {
            IntegrationOutcome = "Error",
            IntegrationDetail = "host timeout",
        });
        var previous = RemoteDeliverySettlementStore.Read(_folder)!;
        var reopened = RemoteDeliverySettlementStore.RequeueInfrastructure(_folder, previous)!;
        var restored = RemoteDeliverySettlementStore.TryWrite(_folder, reopened, previous)!;
        Assert.Equal(previous.Stage, restored.Stage);
        Assert.Equal(previous.IntegrationOutcome, restored.IntegrationOutcome);
        Assert.Equal(previous.IntegrationDetail, restored.IntegrationDetail);
        Assert.True(restored.Revision > reopened.Revision);
        Assert.Null(RemoteDeliverySettlementStore.RecordIntegration(_folder, previous, "old-result", null));

        reopened = RemoteDeliverySettlementStore.RequeueInfrastructure(_folder, restored)!;
        var completed = RemoteDeliverySettlementStore.RecordIntegration(_folder, reopened, "Merged", null)!;
        Assert.Null(RemoteDeliverySettlementStore.TryWrite(_folder, reopened, previous));
        Assert.Equal(completed, RemoteDeliverySettlementStore.Read(_folder));
    }

    [Fact]
    public void A_retry_or_rollback_cannot_touch_a_successor_even_if_its_revision_matches()
    {
        RemoteDeliverySettlementStore.Write(_folder, Record());
        var previous = RemoteDeliverySettlementStore.Read(_folder)!;
        RemoteDeliverySettlementStore.Write(_folder, Record("review_successor"));
        var successor = RemoteDeliverySettlementStore.Read(_folder)!;
        var wrongAttempt = previous with { Revision = successor.Revision };
        Assert.Null(RemoteDeliverySettlementStore.RequeueInfrastructure(_folder, wrongAttempt));
        Assert.Null(RemoteDeliverySettlementStore.ReopenForRetry(_folder, wrongAttempt));
        Assert.Null(RemoteDeliverySettlementStore.TryWrite(_folder, wrongAttempt, previous));
        Assert.False(RemoteDeliverySettlementStore.ReopenForRetry(_folder, previous.ReviewAttemptId));
        Assert.Equal(successor, RemoteDeliverySettlementStore.Read(_folder));
    }

    [Fact]
    public void A_snapshot_advancement_stays_monotonic_and_an_idempotent_replay_keeps_its_revision()
    {
        RemoteDeliverySettlementStore.Write(_folder, Record(stage: RemoteDeliverySettlementStage.LaneSettled));
        var before = RemoteDeliverySettlementStore.Read(_folder)!;
        var after = RemoteDeliverySettlementStore.Advance(_folder, before,
            RemoteDeliverySettlementStage.IntegrationPending)!;
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(RemoteDeliverySettlementStage.LaneSettled, after.Stage);
    }

    [Fact]
    public void A_legacy_record_without_a_revision_migrates_on_its_first_mutation()
    {
        RemoteDeliverySettlementStore.Write(_folder, Record());
        var path = RemoteDeliverySettlementStore.PathFor(_folder);
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json.Remove("revision");
        File.WriteAllText(path, json.ToJsonString());
        var legacy = RemoteDeliverySettlementStore.Read(_folder)!;
        Assert.Equal(0, legacy.Revision);
        var advanced = RemoteDeliverySettlementStore.RecordIntegration(_folder, legacy, "Merged", null)!;
        Assert.Equal(1, advanced.Revision);
        Assert.Equal(1, RemoteDeliverySettlementStore.Read(_folder)!.Revision);
    }

}
