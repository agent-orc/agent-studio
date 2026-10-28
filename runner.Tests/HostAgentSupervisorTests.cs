using System.Text.Json;
using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class HostAgentSupervisorTests : IDisposable
{
    private static readonly DateTime SampleTimeUtc = new(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "host-agent-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("request", "request", 0, 17, false, 0, true)]
    [InlineData("request", "other", 0, 17, false, 0, false)]
    [InlineData("request", "request", 1, 17, false, 0, false)]
    [InlineData("request", "request", 0, 17, true, 0, false)]
    [InlineData("request", "request", 0, 17, false, 1, false)]
    [InlineData("request", "request", 0, null, false, 0, false)]
    public void Migration_requires_matching_barrier_dead_process_and_zero_attempts(
        string request, string ack, int active, int? pid, bool alive, int persisted, bool expected)
        => Assert.Equal(expected, HostAgentMigrationPolicy.CanEnable(
            request, ack, active, pid, alive, persisted));

    [Fact]
    public async Task Coding_and_review_can_migrate_only_after_each_old_daemon_drains()
    {
        var config = CreateConfiguration();
        var codingState = Path.Combine(_root, "coding-state");
        var reviewState = Path.Combine(_root, "review-state");
        var codingRequest = CodingDrainGuard.RequestDrain(codingState);
        var reviewRequest = ReviewDrainGuard.RequestDrain(reviewState, "test migration");
        // The old process has exited. These are its last acknowledged control
        // records; no Task Server lease is manufactured by the host agent.
        File.WriteAllText(Path.Combine(codingState, CodingDrainGuard.AcknowledgementFileName),
            JsonSerializer.Serialize(new CodingDrainGuard.Acknowledgement(codingRequest.Id, int.MaxValue, 0, SampleTimeUtc)));
        File.WriteAllText(ReviewDrainGuard.AcknowledgementPath(reviewState),
            JsonSerializer.Serialize(new ReviewDrainGuard.DrainAcknowledgement(reviewRequest.RequestId, int.MaxValue, 0, SampleTimeUtc)));

        Assert.Equal(0, await HostAgentSupervisor.ExecuteAsync(
            ["migrate-role", config, "coding"], _ => { }, CancellationToken.None));
        Assert.Equal(0, await HostAgentSupervisor.ExecuteAsync(
            ["migrate-role", config, "review"], _ => { }, CancellationToken.None));
        var state = JsonSerializer.Deserialize<HostAgentState>(
            File.ReadAllText(Path.Combine(_root, "host-state", "roles.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(state.Roles["coding"].Enabled);
        Assert.True(state.Roles["review"].Enabled);
        Assert.Equal("coding-id", state.Roles["coding"].RunnerId);
        Assert.Equal("review-id", state.Roles["review"].RunnerId);
        Assert.Equal(codingRequest.Id, state.Roles["coding"].LegacyDrainId);
        Assert.Equal(reviewRequest.RequestId, state.Roles["review"].LegacyDrainId);
    }

    [Fact]
    public async Task Unacknowledged_drain_never_enables_an_adapter()
    {
        var config = CreateConfiguration();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            HostAgentSupervisor.ExecuteAsync(["migrate-role", config, "coding"], _ => { }, deadline.Token));
        Assert.False(File.Exists(Path.Combine(_root, "host-state", "roles.json")));
        Assert.NotNull(CodingDrainGuard.ReadRequest(Path.Combine(_root, "coding-state")));
    }

    private string CreateConfiguration()
    {
        Directory.CreateDirectory(_root);
        var codingFile = Path.Combine(_root, "coding.env");
        var reviewFile = Path.Combine(_root, "review.env");
        File.WriteAllLines(codingFile,
        [
            "RUNNER_SERVER_URL=http://127.0.0.1:5030",
            "RUNNER_ID=coding-id",
            "RUNNER_HOSTNAME=one-host",
            $"RUNNER_WORKDIR={Path.Combine(_root, "coding-work")}",
            $"RUNNER_REVIEW_WORKDIR={Path.Combine(_root, "review-work")}",
            $"RUNNER_STATE_DIR={Path.Combine(_root, "coding-state")}",
        ]);
        File.WriteAllLines(reviewFile,
        [
            "RUNNER_SERVER_URL=http://127.0.0.1:5030",
            "RUNNER_ID=review-id",
            "RUNNER_HOSTNAME=one-host",
            $"RUNNER_WORKDIR={Path.Combine(_root, "coding-work")}",
            $"RUNNER_REVIEW_WORKDIR={Path.Combine(_root, "review-work")}",
            $"RUNNER_STATE_DIR={Path.Combine(_root, "review-state")}",
        ]);
        var config = Path.Combine(_root, "host-agent.json");
        File.WriteAllText(config, JsonSerializer.Serialize(new HostAgentConfiguration(
            Path.Combine(_root, "host-state"),
            new HostAgentRoleConfiguration(codingFile, Path.Combine(_root, "coding-home")),
            new HostAgentRoleConfiguration(reviewFile, Path.Combine(_root, "review-home")))));
        return config;
    }
}
