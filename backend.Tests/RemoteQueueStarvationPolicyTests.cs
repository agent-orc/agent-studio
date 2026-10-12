using Xunit;
using Microsoft.Extensions.Logging;

namespace AgentStudio.Tests;

public sealed class RemoteQueueStarvationPolicyTests
{
    private static readonly DateTime Now = new(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void LocalCodingDenial_IsImmediatelyVisibleWithoutRemoteCapacity()
    {
        var task = ReadyTask(1) with { Mode = TaskModes.Coding };
        var settings = RemoteSettings() with { ExecutionLocation = ExecutionLocations.Local };
        var snapshot = RemoteQueueStarvationPolicy.Evaluate(
            Now, TimeSpan.FromMinutes(30), [task], _ => settings,
            TaskReferenceIndex.Build([task]), []);

        Assert.True(snapshot.Active);
        Assert.Equal(1, snapshot.WaitingTaskCount);
        Assert.Equal(RemoteExecutionRequirement.Code, Assert.Single(snapshot.Items).BlockReasonCode);
        Assert.Contains("local coding is disabled", snapshot.Items[0].BlockReason);
    }

    [Theory]
    [InlineData(31, 2, 0, 2, true)]
    [InlineData(30, 2, 0, 2, true)]
    [InlineData(29, 2, 0, 2, false)]
    [InlineData(31, 0, 0, 0, false)]
    [InlineData(31, 2, 3, 0, false)]
    public void Evaluate_RequiresOldRemoteReadyWorkAndFreshFreeCapacity(
        int waitingMinutes,
        int availableSlots,
        int runnerAgeMinutes,
        int expectedAvailableSlots,
        bool expectedActive)
    {
        var task = ReadyTask(waitingMinutes);
        var snapshot = RemoteQueueStarvationPolicy.Evaluate(
            Now,
            TimeSpan.FromMinutes(30),
            [task],
            _ => RemoteSettings(),
            TaskReferenceIndex.Build([task]),
            [Runner(availableSlots, runnerAgeMinutes)]);

        Assert.Equal(expectedActive, snapshot.Active);
        Assert.Equal(expectedAvailableSlots, snapshot.AvailableSlots);
        Assert.Equal(waitingMinutes >= 30 ? 1 : 0, snapshot.WaitingTaskCount);
    }

    [Fact]
    public void Evaluate_SuppressesAlarmWhileSerialQueueClaimsKeepProgressing()
    {
        var task = ReadyTask(90);

        var snapshot = RemoteQueueStarvationPolicy.Evaluate(
            Now,
            TimeSpan.FromMinutes(30),
            [task],
            _ => RemoteSettings(),
            TaskReferenceIndex.Build([task]),
            [Runner(2, 0, lastClaimMinutesAgo: 1)]);

        Assert.False(snapshot.Active);
        Assert.False(snapshot.ClaimProgressStalled);
        Assert.Equal(Now.AddMinutes(-1), snapshot.LastSuccessfulClaimAt);
        Assert.Empty(snapshot.Items);
    }

    [Theory]
    [InlineData(29, false)]
    [InlineData(30, true)]
    public void Evaluate_DebouncesMissingClaimProgressUntilThreshold(
        int lastClaimMinutesAgo,
        bool expectedActive)
    {
        var task = ReadyTask(90);

        var snapshot = RemoteQueueStarvationPolicy.Evaluate(
            Now,
            TimeSpan.FromMinutes(30),
            [task],
            _ => RemoteSettings(),
            TaskReferenceIndex.Build([task]),
            [Runner(2, 0, lastClaimMinutesAgo)]);

        Assert.Equal(expectedActive, snapshot.Active);
        Assert.Equal(expectedActive, snapshot.ClaimProgressStalled);
        Assert.Equal(expectedActive ? 1 : 0, snapshot.WaitingTaskCount);
    }

    [Fact]
    public void Evaluate_ActivatesForRecordedRejectionEvenWhileOtherClaimsProgress()
    {
        var rejection = new RemoteDispatchRejection
        {
            Code = "future-admission-rule",
            RunnerId = "runner-01",
            RunnerName = "Runner 01",
            Reason = "future admission rule refused the card",
            RejectedAtUtc = Now.AddMinutes(-4),
        };
        var task = ReadyTask(4) with { RemoteDispatchRejection = rejection };

        var snapshot = RemoteQueueStarvationPolicy.Evaluate(
            Now,
            TimeSpan.FromMinutes(30),
            [task],
            _ => RemoteSettings(),
            TaskReferenceIndex.Build([task]),
            [Runner(1, 0, lastClaimMinutesAgo: 1)]);

        Assert.True(snapshot.Active);
        Assert.False(snapshot.ClaimProgressStalled);
        Assert.True(snapshot.HasRejections);
        Assert.Equal(rejection, Assert.Single(snapshot.Items).LastRejection);
    }

    [Fact]
    public void Evaluate_CountsBuildProfileGateBlockedCardsImmediately()
    {
        var task = ReadyTask(1);
        var blocked = RemoteSettings() with
        {
            BuildProfile = new BuildProfile { Status = BuildProfileStatuses.Declared },
        };

        var snapshot = RemoteQueueStarvationPolicy.Evaluate(
            Now,
            TimeSpan.FromMinutes(30),
            [task],
            _ => blocked,
            TaskReferenceIndex.Build([task]),
            [Runner(0, 0, lastClaimMinutesAgo: 1)]);

        Assert.True(snapshot.Active);
        Assert.Equal(0, snapshot.AvailableSlots);
        Assert.Equal(1, snapshot.BuildProfileGateBlockedTaskCount);
        var item = Assert.Single(snapshot.Items);
        Assert.Equal("build-profile-gate", item.BlockReasonCode);
        Assert.Contains("not yet validated", item.BlockReason);
    }

    [Fact]
    public void Visibility_ProviderLimitOverridesStalledAndKeepsOtherCliCapacityDistinct()
    {
        var limit = new ProviderLimitStatus(
            "claude",
            Now,
            Now.AddHours(2),
            $"claude: limited until {Now.AddHours(2):O}",
            true);
        var projected = RemoteQueueStarvationVisibility.Project(
            new RemoteQueueStarvationSnapshot { AvailableSlots = 4, Signal = "stalled" },
            [new RemoteQueueStarvationItem { TaskKey = "AGT-1", EnteredLaneAt = Now.AddHours(-1) }],
            [new ProjectRunnerStatus { ProjectName = "demo", ProviderLimits = [limit] }]);

        Assert.True(projected.Active);
        Assert.Equal("limited", projected.Signal);
        Assert.Equal(limit, Assert.Single(projected.ProviderLimits));
        Assert.Empty(projected.PickupPauses);
    }

    [Fact]
    public void Visibility_InfraBreakerPauseCarriesReasonWhileManualPauseStaysManual()
    {
        const string reason = "pickup paused: infra breaker, 3 failures cliType=claude at 2026-08-23T22:10:00Z";
        var projected = RemoteQueueStarvationVisibility.Project(
            new RemoteQueueStarvationSnapshot(),
            [],
            [
                new ProjectRunnerStatus
                {
                    ProjectName = "breaker-project",
                    Mode = "paused",
                    ModeSource = "circuit-breaker",
                    ModeReason = reason,
                },
                new ProjectRunnerStatus
                {
                    ProjectName = "operator-project",
                    Mode = "paused",
                    ModeSource = "user",
                    ModeReason = "api-toggle",
                },
            ]);

        Assert.True(projected.Active);
        Assert.Equal("paused", projected.Signal);
        var pause = Assert.Single(projected.PickupPauses);
        Assert.Equal("breaker-project", pause.ProjectName);
        Assert.Equal(reason, pause.Reason);
    }

    [Fact]
    public void Watchdog_LogsWarningOnceForAnAcuteQueueAndRecoveryWhenItClears()
    {
        var logger = new CapturingLogger();
        var watchdog = new RemoteQueueStarvationWatchdog(
            null!,
            null!,
            null!,
            new ConfigurationBuilder().Build(),
            logger);
        var acute = new RemoteQueueStarvationSnapshot
        {
            Active = true,
            WaitingTaskCount = 1,
            AvailableSlots = 2,
            OldestEnteredLaneAt = Now.AddMinutes(-40),
            ObservedAt = Now,
            Items = [new RemoteQueueStarvationItem { TaskKey = "AGT-1" }],
        };

        watchdog.PublishLogTransition(new RemoteQueueStarvationSnapshot(), acute, Now);
        watchdog.PublishLogTransition(acute, acute, Now.AddMinutes(1));
        watchdog.PublishLogTransition(acute, new RemoteQueueStarvationSnapshot(), Now.AddMinutes(2));

        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Information
            && entry.Message.Contains("recovered", StringComparison.Ordinal));
    }

    private static TaskInfo ReadyTask(int waitingMinutes) => new()
    {
        Id = "task-1",
        Key = "AGT-1",
        Title = "Waiting task",
        ProjectName = "demo",
        State = TaskStates.Ready,
        Agent = AgentTypes.Codex,
        EnteredLaneAt = Now.AddMinutes(-waitingMinutes),
        CreatedAt = Now.AddHours(-1),
    };

    private static ProjectSettings RemoteSettings() => new()
    {
        PickupMode = PickupModes.Auto,
        ExecutionLocation = "runner-01",
    };

    private static ClientIdentity Runner(
        int availableSlots,
        int ageMinutes,
        int? lastClaimMinutesAgo = null) => new()
    {
        Id = "runner-01",
        DisplayName = "Runner 01",
        Kind = ClientIdentityKind.Service,
        LastSeenAt = Now.AddMinutes(-ageMinutes),
        RunnerDaemonState = "running",
        RunnerAvailableSlots = availableSlots,
        RunnerLastClaimAt = lastClaimMinutesAgo is { } minutes
            ? Now.AddMinutes(-minutes)
            : null,
    };

    private sealed class CapturingLogger : ILogger<RemoteQueueStarvationWatchdog>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
