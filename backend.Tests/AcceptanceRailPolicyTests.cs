using Xunit;

namespace AgentStudio.Tests;

public sealed class AcceptanceRailPolicyTests
{
    private static readonly AcceptanceRailOptions Options = new(
        true,
        TimeSpan.FromMinutes(3),
        5,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            AcceptanceRailDefaults.OperatorHoldTag,
            "AGT-HOLD",
        });

    [Fact]
    public void Options_DefaultToEnabledBoundedRail()
    {
        var configuration = new ConfigurationBuilder().Build();

        var options = AcceptanceRailOptions.FromConfiguration(configuration);

        Assert.True(options.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(180), options.Interval);
        Assert.Equal(5, options.MaxRequeues);
        Assert.Contains(AcceptanceRailDefaults.OperatorHoldTag, options.HoldList);
    }

    [Fact]
    public void Options_ReadDisableIntervalRetryAndHoldList()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AcceptanceRail:Enabled"] = "false",
                ["AcceptanceRail:IntervalSeconds"] = "120",
                ["AcceptanceRail:MaxRequeues"] = "3",
                ["AcceptanceRail:HoldList:0"] = "AGT-42",
            })
            .Build();

        var options = AcceptanceRailOptions.FromConfiguration(configuration);

        Assert.False(options.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(120), options.Interval);
        Assert.Equal(3, options.MaxRequeues);
        Assert.Contains("AGT-42", options.HoldList);
        Assert.Contains(AcceptanceRailDefaults.OperatorHoldTag, options.HoldList);
    }

    [Fact]
    public void IntegratedCodingCard_IsAccepted()
    {
        var decision = AcceptanceRailPolicy.Decide(
            Card(),
            Status(IntegrationStatuses.Integrated),
            conflictRequeues: 0,
            Options);

        Assert.Equal(AcceptanceRailAction.Accept, decision.Action);
    }

    [Theory]
    [InlineData("orchestrator-hold", "AGT-1")]
    [InlineData("ordinary", "AGT-HOLD")]
    public void HeldCard_IsUntouched(string tag, string key)
    {
        var decision = AcceptanceRailPolicy.Decide(
            Card() with { Key = key, Tags = [tag] },
            Status(IntegrationStatuses.Integrated),
            conflictRequeues: 0,
            Options);

        Assert.Equal(AcceptanceRailAction.Ignore, decision.Action);
        Assert.Equal("operator-hold", decision.Reason);
    }

    [Fact]
    public void RecoverableConflict_IsRequeued()
    {
        var decision = AcceptanceRailPolicy.Decide(
            Card(),
            RecoverableConflict(),
            conflictRequeues: 2,
            Options);

        Assert.Equal(AcceptanceRailAction.Requeue, decision.Action);
    }

    [Theory]
    [InlineData("infrastructure")]
    [InlineData("quota")]
    public void RetryableReviewFailure_IsRequeuedWithoutIntegrationEvidence(string failureClass)
    {
        var decision = AcceptanceRailPolicy.Decide(
            Card() with
            {
                ReviewFailure = new ReviewFailureStatus
                {
                    FailureClass = failureClass,
                    RetryNumber = 1,
                    MaximumRetries = 3,
                    RetryAtUtc = DateTime.UtcNow.AddMinutes(-1),
                },
            },
            integration: null,
            conflictRequeues: 0,
            Options);

        Assert.Equal(AcceptanceRailAction.Requeue, decision.Action);
        Assert.Equal("retryable-review-failure", decision.Reason);
    }

    [Fact]
    public void RetryableReviewFailure_RespectsBackoff()
    {
        var decision = AcceptanceRailPolicy.Decide(
            Card() with
            {
                ReviewFailure = new ReviewFailureStatus
                {
                    FailureClass = "infrastructure",
                    RetryNumber = 1,
                    MaximumRetries = 3,
                    RetryAtUtc = DateTime.UtcNow.AddMinutes(1),
                },
            },
            integration: null,
            conflictRequeues: 0,
            Options);

        Assert.Equal(AcceptanceRailAction.Ignore, decision.Action);
        Assert.Equal("review-failure-backoff", decision.Reason);
    }

    [Fact]
    public void ExhaustedReviewFailure_RemainsForHumanReview()
    {
        var decision = AcceptanceRailPolicy.Decide(
            Card() with
            {
                ReviewFailure = new ReviewFailureStatus
                {
                    FailureClass = "quota",
                    RetryNumber = 3,
                    MaximumRetries = 3,
                    Exhausted = true,
                },
            },
            integration: null,
            conflictRequeues: 0,
            Options);

        Assert.Equal(AcceptanceRailAction.Ignore, decision.Action);
        Assert.Equal("review-failure-retry-budget-exhausted", decision.Reason);
    }

    [Fact]
    public void ConflictAtConfiguredLimit_IsEscalated()
    {
        var decision = AcceptanceRailPolicy.Decide(
            Card(),
            RecoverableConflict(),
            conflictRequeues: Options.MaxRequeues,
            Options);

        Assert.Equal(AcceptanceRailAction.Escalate, decision.Action);
        Assert.Equal("integration-requeue-budget-exhausted", decision.Reason);
    }

    [Fact]
    public void EscalatedGenuineBounce_UsesSameBoundedRequeue()
    {
        var decision = AcceptanceRailPolicy.Decide(
            Card() with { State = TaskStates.Escalated },
            RecoverableConflict(),
            conflictRequeues: 0,
            Options);

        Assert.Equal(AcceptanceRailAction.Requeue, decision.Action);
    }

    [Fact]
    public void ConceptCard_IsUntouched()
    {
        var decision = AcceptanceRailPolicy.Decide(
            Card() with { Mode = TaskModes.Concept },
            Status(IntegrationStatuses.Integrated),
            conflictRequeues: 0,
            Options);

        Assert.Equal(AcceptanceRailAction.Ignore, decision.Action);
        Assert.Equal("no-code-acceptance", decision.Reason);
    }

    [Theory]
    [InlineData(IntegrationStatuses.Pending)]
    [InlineData(IntegrationStatuses.Partial)]
    [InlineData(IntegrationStatuses.NoBranch)]
    [InlineData(IntegrationStatuses.ConflictSkipped)]
    public void NonIntegratedCard_IsNeverAccepted(string integrationStatus)
    {
        var status = integrationStatus == IntegrationStatuses.ConflictSkipped
            ? Status(integrationStatus) with
            {
                Failure = new TaskIntegrationFailure
                {
                    Code = AcceptedIntegrationFailureCodes.BuildGateFailed,
                    RebaseRecoveryAvailable = false,
                },
            }
            : Status(integrationStatus);

        var decision = AcceptanceRailPolicy.Decide(
            Card(),
            status,
            conflictRequeues: 0,
            Options);

        Assert.NotEqual(AcceptanceRailAction.Accept, decision.Action);
    }

    private static TaskInfo Card() => new()
    {
        Id = "rail-card",
        Key = "AGT-1",
        TaskKey = "fixture::rail-card",
        State = TaskStates.HumanReview,
        Mode = TaskModes.Coding,
        TaskType = TaskTypes.Chore,
    };

    private static TaskIntegrationStatus Status(string status) => new()
    {
        Status = status,
        IntegrationBranch = "develop",
    };

    private static TaskIntegrationStatus RecoverableConflict()
        => Status(IntegrationStatuses.ConflictSkipped) with
        {
            Failure = new TaskIntegrationFailure
            {
                Code = AcceptedIntegrationFailureCodes.MergeConflict,
                RebaseRecoveryAvailable = true,
            },
        };
}
