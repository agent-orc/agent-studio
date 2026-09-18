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
    public void Advancing_records_the_integration_outcome()
    {
        RemoteDeliverySettlementStore.Write(_folder, Record());

        Assert.True(RemoteDeliverySettlementStore.Advance(
            _folder, RemoteDeliverySettlementStage.IntegrationSettled, "Merged"));

        var read = RemoteDeliverySettlementStore.Read(_folder)!;
        Assert.Equal(RemoteDeliverySettlementStage.IntegrationSettled, read.Stage);
        Assert.Equal("Merged", read.IntegrationOutcome);
    }

    [Fact]
    public void Advancing_preserves_the_exact_automatic_recovery_budget_park_reason()
    {
        RemoteDeliverySettlementStore.Write(_folder, Record());

        RemoteDeliverySettlementStore.Advance(
            _folder,
            RemoteDeliverySettlementStage.IntegrationSettled,
            nameof(MergeIntoIntegrationOutcome.AgentRoundRequired),
            "automatic recovery budget used: 2/2");

        var read = RemoteDeliverySettlementStore.Read(_folder)!;
        Assert.Equal("automatic recovery budget used: 2/2", read.AutomaticRecoveryParkReason);
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
}
