using Xunit;

namespace AgentStudio.Tests;

public sealed class AcceptedIntegrationBackstopPolicyTests
{
    private static readonly DateTime Now = new(2026, 8, 9, 16, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SweepTelemetry_MixedOutcomes_LogsTruthfulCounters()
    {
        var summary = AcceptedIntegrationBackstopPolicy.Summarize(
        [
            MergeIntoIntegrationOutcome.Merged,
            MergeIntoIntegrationOutcome.MergedAfterRebase,
            MergeIntoIntegrationOutcome.AlreadyMerged,
            MergeIntoIntegrationOutcome.Error,
            MergeIntoIntegrationOutcome.Conflict,
            MergeIntoIntegrationOutcome.NoTaskBranch,
        ]);
        var logger = new CapturingLogger();

        AcceptedIntegrationBackstopTelemetry.LogSweep(logger, summary);

        Assert.Equal(6, summary.Attempted);
        Assert.Equal(2, summary.Merged);
        Assert.Equal(1, summary.AlreadyMerged);
        Assert.Equal(3, summary.Failed);
        Assert.Equal(2, summary.Integrated);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("attempted=6", entry.Message, StringComparison.Ordinal);
        Assert.Contains("merged=2", entry.Message, StringComparison.Ordinal);
        Assert.Contains("alreadyMerged=1", entry.Message, StringComparison.Ordinal);
        Assert.Contains("failed=3", entry.Message, StringComparison.Ordinal);
        Assert.Contains("integrated=2", entry.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(9, true)]
    public void GateEnvironmentRetries_AreBounded(int priorAttempts, bool exhausted)
    {
        // AGT-2720: a gate that dies in its own toolchain decided nothing, so the
        // sweep retries it - but each attempt is a full suite holding the machine
        // gate. A fault that survives three fresh installs is the gate host and
        // must reach an operator instead of looping every sweep.
        Assert.Equal(
            exhausted,
            AcceptedIntegrationBackstopPolicy.GateEnvironmentBudgetExhausted(priorAttempts));
    }

    [Fact]
    public void GateEnvironmentAttempt_IsReadFromTheFailureCodeOrTheLegacyVerdict()
    {
        Assert.True(TaskIntegrationStatusService.IsGateEnvironmentAttempt(new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopStepId,
            Status = PipelineStepStatus.Failed,
            FailureCode = AcceptedIntegrationFailureCodes.GateEnvironment,
        }));
        Assert.True(TaskIntegrationStatusService.IsGateEnvironmentAttempt(new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopStepId,
            Status = PipelineStepStatus.Failed,
            Verdict = "gate-environment",
        }));
        Assert.False(TaskIntegrationStatusService.IsGateEnvironmentAttempt(new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopStepId,
            Status = PipelineStepStatus.Failed,
            Verdict = "gate-failed",
            FailureCode = AcceptedIntegrationFailureCodes.BuildGateFailed,
        }));
        Assert.False(TaskIntegrationStatusService.IsGateEnvironmentAttempt(null));
    }

    [Fact]
    public void Alert_OverThirtyMinutesWithoutSuccessfulIntegration_SetsProjectHintAndLogsTaskKeys()
    {
        var snapshot = AcceptedIntegrationBackstopPolicy.EvaluateAlert(
            Now,
            TimeSpan.FromMinutes(30),
        [
            Candidate("AGT-2531", 31, MergeIntoIntegrationOutcome.NoTaskBranch.ToString()),
            Candidate("AGT-2541", 45, MergeIntoIntegrationOutcome.Error.ToString()),
            Candidate("AGT-NEW", 29, MergeIntoIntegrationOutcome.Conflict.ToString()),
            Candidate("AGT-DONE", 90, MergeIntoIntegrationOutcome.Merged.ToString(), IntegrationStatuses.Integrated),
        ]);
        var logger = new CapturingLogger();
        var logState = new AcceptedIntegrationAlertLogState();

        logState.Publish(logger, new AcceptedIntegrationAlertSnapshot(), snapshot, Now);

        Assert.True(snapshot.Active);
        Assert.Equal(2, snapshot.StalledTaskCount);
        Assert.Equal(["AGT-2541", "AGT-2531"], snapshot.Items.Select(item => item.TaskKey));
        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("AGT-2531", warning.Message, StringComparison.Ordinal);
        Assert.Contains("AGT-2541", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("AGT-NEW", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Alert_NoRecordedOutcome_StillSetsHintAfterThreshold()
    {
        var snapshot = AcceptedIntegrationBackstopPolicy.EvaluateAlert(
            Now,
            TimeSpan.FromMinutes(30),
            [Candidate("AGT-UNKNOWN", 35, lastOutcome: null)]);

        var item = Assert.Single(snapshot.Items);
        Assert.True(snapshot.Active);
        Assert.Null(item.LastOutcome);
    }

    [Fact]
    public void Alert_PreInvariantCandidateWithoutIntegrationRecord_IsNotEvaluated()
    {
        var snapshot = AcceptedIntegrationBackstopPolicy.EvaluateAlert(
            Now,
            TimeSpan.FromMinutes(30),
            [Candidate("ASS-499", 10_000, lastOutcome: null, hasIntegrationRecord: false)]);

        Assert.False(snapshot.Active);
        Assert.Equal(0, snapshot.StalledTaskCount);
        Assert.Empty(snapshot.Items);
    }

    [Theory]
    [InlineData(IntegrationRecordClasses.IntegratedVerified)]
    [InlineData(IntegrationRecordClasses.IntegratedHistorical)]
    [InlineData(IntegrationRecordClasses.NoCodeExpected)]
    [InlineData(IntegrationRecordClasses.NoAttributionLegacy)]
    [InlineData(IntegrationRecordClasses.ContentOnFence)]
    [InlineData(IntegrationRecordClasses.GenuinelyMissing)]
    public void Alert_HistoricalVerificationClass_IsNotALiveAcceptanceStall(string classification)
    {
        var candidate = Candidate("AGT-2589", 45, MergeIntoIntegrationOutcome.Error.ToString()) with
        {
            HasHistoricalVerification = true,
            Task = Candidate("AGT-2589", 45, MergeIntoIntegrationOutcome.Error.ToString()).Task with
            {
                IntegrationRecords =
                [
                    new TaskIntegrationRecord
                    {
                        Id = "operator-gpt-verification-2026-08-11",
                        Classification = classification,
                    },
                ],
            },
        };

        var snapshot = AcceptedIntegrationBackstopPolicy.EvaluateAlert(
            Now,
            TimeSpan.FromMinutes(30),
            [candidate]);

        Assert.False(snapshot.Active);
        Assert.Empty(snapshot.Items);
    }

    [Theory]
    [InlineData(TaskStates.Archive, true, true)]
    [InlineData(TaskStates.Completed, false, false)]
    [InlineData(TaskStates.Completed, true, true)]
    [InlineData(TaskStates.HumanReview, true, false)]
    public void AlertCandidate_RequiresCurrentLaneAndIntegrationRecord(
        string state,
        bool hasIntegrationRecord,
        bool expected)
    {
        var candidate = Candidate("AGT-2588", 45, MergeIntoIntegrationOutcome.Error.ToString()) with
        {
            Task = Candidate("AGT-2588", 45, MergeIntoIntegrationOutcome.Error.ToString()).Task with
            {
                State = state,
                Phase = state == TaskStates.HumanReview ? LifecyclePhases.Integrating : null,
            },
            HasIntegrationRecord = hasIntegrationRecord,
        };

        Assert.Equal(expected, AcceptedIntegrationBackstopPolicy.IsAlertCandidate(candidate));
    }

    [Theory]
    [InlineData(IntegrationStatuses.ConflictSkipped, MergeIntoIntegrationOutcome.Error)]
    [InlineData(IntegrationStatuses.Partial, MergeIntoIntegrationOutcome.NoTaskBranch)]
    [InlineData(IntegrationStatuses.Pending, MergeIntoIntegrationOutcome.Conflict)]
    public void Alert_HumanReviewIntegrationFailures_AreNotMislabelledAsAccepted(
        string integrationStatus,
        MergeIntoIntegrationOutcome lastOutcome)
    {
        var baseline = Candidate("QS-70", 45, lastOutcome.ToString(), integrationStatus);
        var candidate = baseline with
        {
            Task = baseline.Task with
            {
                State = TaskStates.HumanReview,
                Phase = LifecyclePhases.Integrating,
                Tags = [IntegrationStatuses.PendingTag],
            },
        };

        var snapshot = AcceptedIntegrationBackstopPolicy.EvaluateAlert(
            Now,
            TimeSpan.FromMinutes(30),
            [candidate]);

        Assert.False(AcceptedIntegrationBackstopPolicy.IsAlertCandidate(candidate));
        Assert.False(snapshot.Active);
        Assert.Empty(snapshot.Items);
    }

    [Theory]
    [InlineData(TaskStates.Completed, null, null, true)]
    [InlineData(TaskStates.Archive, null, null, true)]
    [InlineData(TaskStates.HumanReview, LifecyclePhases.Integrating, null, true)]
    [InlineData(TaskStates.HumanReview, null, null, false)]
    [InlineData(TaskStates.Completed, null, "genuinely-missing", false)]
    [InlineData(TaskStates.Completed, null, "content-on-fence", false)]
    [InlineData(TaskStates.Completed, null, "integrated-historical", false)]
    public void RecoveryCandidate_NeverTreatsHistoricalBookkeepingAsMergeAuthority(
        string state,
        string? phase,
        string? verificationClass,
        bool expected)
    {
        var task = Candidate("AGT-2589", 45, lastOutcome: null).Task with
        {
            State = state,
            Phase = phase,
            IntegrationRecords = verificationClass is null
                ? []
                :
                [
                    new TaskIntegrationRecord
                    {
                        Id = HistoricalIntegrationVerificationSweep.RecordId,
                        Classification = verificationClass,
                    },
                ],
        };

        Assert.Equal(expected, AcceptedIntegrationBackstopPolicy.IsRecoveryCandidate(task));
    }

    private static AcceptedIntegrationAlertCandidate Candidate(
        string key,
        int acceptedMinutesAgo,
        string? lastOutcome,
        string integrationStatus = IntegrationStatuses.Pending,
        bool hasIntegrationRecord = true)
        => new()
        {
            Task = new TaskInfo
            {
                Id = key.ToLowerInvariant(),
                TaskKey = key,
                Key = key,
                Title = $"Delivery {key}",
                ProjectName = "Agent Studio",
                State = TaskStates.Completed,
                Mode = TaskModes.Coding,
                EnteredLaneAt = Now.AddMinutes(-acceptedMinutesAgo),
            },
            AcceptedAt = Now.AddMinutes(-acceptedMinutesAgo),
            IntegrationStatus = integrationStatus,
            LastOutcome = lastOutcome,
            HasIntegrationRecord = hasIntegrationRecord,
        };

    private sealed class CapturingLogger : ILogger
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
