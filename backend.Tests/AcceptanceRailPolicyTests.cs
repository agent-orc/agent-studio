using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public sealed class AcceptanceRailPolicyTests
{
    private static readonly AcceptanceRailOptions Options = new(
        true,
        TimeSpan.FromMinutes(3),
        5,
        3,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            AcceptanceRailDefaults.OperatorHoldTag,
            "AGT-HOLD",
        });

    /// <summary>Fixed evaluation instant; the policy never reads the clock.</summary>
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Options_DefaultToEnabledBoundedRail()
    {
        var configuration = new ConfigurationBuilder().Build();

        var options = AcceptanceRailOptions.FromConfiguration(configuration);

        Assert.True(options.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(180), options.Interval);
        Assert.Equal(5, options.MaxRequeues);
        Assert.Equal(AcceptanceRailDefaults.MaxInfrastructureRequeues, options.MaxInfrastructureRequeues);
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

    [Theory]
    [InlineData(null, AcceptanceRailDefaults.MaxInfrastructureRequeues)]
    [InlineData("7", 7)]
    [InlineData("0", 1)]
    [InlineData("99", 20)]
    public void Options_ClampInfrastructureRequeueBudget(string? configured, int expected)
    {
        var values = new Dictionary<string, string?>();
        if (configured is not null)
            values["AcceptanceRail:MaxInfrastructureRequeues"] = configured;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var options = AcceptanceRailOptions.FromConfiguration(configuration);

        Assert.Equal(expected, options.MaxInfrastructureRequeues);
    }

    [Fact]
    public void IntegratedCodingCard_IsAccepted()
    {
        var decision = AcceptanceRailPolicy.Decide(
            Card(),
            Status(IntegrationStatuses.Integrated),
            conflictRequeues: 0,
            Options,
            Now);

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
            Options,
            Now);

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
            Options,
            Now);

        Assert.Equal(AcceptanceRailAction.Requeue, decision.Action);
    }

    [Fact]
    public void ConflictAtConfiguredLimit_IsEscalated()
    {
        var decision = AcceptanceRailPolicy.Decide(
            Card(),
            RecoverableConflict(),
            conflictRequeues: Options.MaxRequeues,
            Options,
            Now);

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
            Options,
            Now);

        Assert.Equal(AcceptanceRailAction.Requeue, decision.Action);
    }

    [Fact]
    public void ConceptCard_IsUntouched()
    {
        var decision = AcceptanceRailPolicy.Decide(
            Card() with { Mode = TaskModes.Concept },
            Status(IntegrationStatuses.Integrated),
            conflictRequeues: 0,
            Options,
            Now);

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
                    FailureClass = RunFailureClass.Product,
                    FailureSignature = RunFailureSignatures.NewTestFailures,
                },
            }
            : Status(integrationStatus);

        var decision = AcceptanceRailPolicy.Decide(
            Card(),
            status,
            conflictRequeues: 0,
            Options,
            Now);

        Assert.NotEqual(AcceptanceRailAction.Accept, decision.Action);
    }

    /// <summary>
    /// AGT-2749 decision matrix: only a failure the taxonomy attributes to the
    /// host or the account leaves the park lane, and only while its own budget
    /// lasts.
    /// </summary>
    [Theory]
    [InlineData(RunFailureClass.Infrastructure, 0, AcceptanceRailAction.RequeueInfrastructure, "requeueable-infrastructure-failure")]
    [InlineData(RunFailureClass.Infrastructure, 2, AcceptanceRailAction.RequeueInfrastructure, "requeueable-infrastructure-failure")]
    [InlineData(RunFailureClass.Quota, 0, AcceptanceRailAction.RequeueInfrastructure, "requeueable-quota-failure")]
    [InlineData(RunFailureClass.Infrastructure, 3, AcceptanceRailAction.Escalate, "infrastructure-requeue-budget-exhausted")]
    [InlineData(RunFailureClass.Quota, 4, AcceptanceRailAction.Escalate, "infrastructure-requeue-budget-exhausted")]
    [InlineData(RunFailureClass.Product, 0, AcceptanceRailAction.Ignore, "not-recoverable")]
    [InlineData(RunFailureClass.Unknown, 0, AcceptanceRailAction.Ignore, "not-recoverable")]
    public void FailureClass_DrivesRequeueInsteadOfPark(
        RunFailureClass failureClass,
        int infrastructureRequeues,
        AcceptanceRailAction expectedAction,
        string expectedReason)
    {
        var decision = AcceptanceRailPolicy.Decide(
            Card(),
            ClassifiedFailure(failureClass),
            conflictRequeues: 0,
            Options,
            Now,
            infrastructureRequeues,
            // Older than any backoff step, so only the class and the budget decide.
            lastInfrastructureRequeueAt: Now.AddDays(-1));

        Assert.Equal(expectedAction, decision.Action);
        Assert.Equal(expectedReason, decision.Reason);
    }

    [Fact]
    public void InfrastructureCardInsideItsBackoffWindow_IsIgnored()
    {
        var decision = AcceptanceRailPolicy.Decide(
            Card(),
            ClassifiedFailure(RunFailureClass.Infrastructure),
            conflictRequeues: 0,
            Options,
            Now,
            infrastructureRequeues: 1,
            lastInfrastructureRequeueAt: Now.AddSeconds(-30));

        Assert.Equal(AcceptanceRailAction.Ignore, decision.Action);
        Assert.Equal("infrastructure-backoff", decision.Reason);
    }

    [Fact]
    public void QuotaCard_WaitsForTheKnownResetAndThenRequeues()
    {
        var beforeReset = AcceptanceRailPolicy.Decide(
            Card(),
            ClassifiedFailure(RunFailureClass.Quota, RunFailureSignatures.CliQuotaExhausted),
            conflictRequeues: 0,
            Options,
            Now,
            infrastructureRequeues: 0,
            lastInfrastructureRequeueAt: null,
            quotaResetAt: Now.AddHours(2));

        Assert.Equal(AcceptanceRailAction.Ignore, beforeReset.Action);
        Assert.Equal("infrastructure-backoff", beforeReset.Reason);

        var afterReset = AcceptanceRailPolicy.Decide(
            Card(),
            ClassifiedFailure(RunFailureClass.Quota, RunFailureSignatures.CliQuotaExhausted),
            conflictRequeues: 0,
            Options,
            Now.AddHours(3),
            infrastructureRequeues: 0,
            lastInfrastructureRequeueAt: null,
            quotaResetAt: Now.AddHours(2));

        Assert.Equal(AcceptanceRailAction.RequeueInfrastructure, afterReset.Action);
        Assert.Equal("requeueable-quota-failure", afterReset.Reason);
    }

    [Fact]
    public void RebaseRecoverableConflict_KeepsItsOwnBudgetEvenWhenClassified()
    {
        var status = RecoverableConflict() with
        {
            Failure = new TaskIntegrationFailure
            {
                Code = AcceptedIntegrationFailureCodes.MergeConflict,
                RebaseRecoveryAvailable = true,
                FailureClass = RunFailureClass.Infrastructure,
                FailureSignature = RunFailureSignatures.GitNetworkTimeout,
            },
        };

        var decision = AcceptanceRailPolicy.Decide(
            Card(),
            status,
            conflictRequeues: 0,
            Options,
            Now,
            infrastructureRequeues: 0);

        Assert.Equal(AcceptanceRailAction.Requeue, decision.Action);
        Assert.Equal("recoverable-integration-conflict", decision.Reason);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 60)]
    [InlineData(2, 120)]
    [InlineData(3, 240)]
    [InlineData(20, AcceptanceRailDefaults.InfrastructureBackoffCeilingSeconds)]
    public void Backoff_DoublesPerAttemptUpToTheCeiling(int attempt, int expectedSeconds)
    {
        var wait = AcceptanceRailPolicy.Backoff(
            attempt,
            RunFailureClass.Infrastructure,
            lastRequeueAt: Now,
            quotaResetAt: null,
            Now);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), wait);
    }

    [Fact]
    public void Backoff_IsElapsedOnceTheWindowPassed()
    {
        var wait = AcceptanceRailPolicy.Backoff(
            attempt: 2,
            RunFailureClass.Infrastructure,
            lastRequeueAt: Now.AddMinutes(-5),
            quotaResetAt: null,
            Now);

        Assert.Equal(TimeSpan.Zero, wait);
    }

    [Fact]
    public void Backoff_WithoutAPriorRequeue_IsImmediate()
    {
        var wait = AcceptanceRailPolicy.Backoff(
            attempt: 0,
            RunFailureClass.Infrastructure,
            lastRequeueAt: null,
            quotaResetAt: null,
            Now);

        Assert.Equal(TimeSpan.Zero, wait);
    }

    [Fact]
    public void Backoff_ForQuota_WaitsForTheKnownReset()
    {
        var pending = AcceptanceRailPolicy.Backoff(
            attempt: 0,
            RunFailureClass.Quota,
            lastRequeueAt: null,
            quotaResetAt: Now.AddMinutes(45),
            Now);
        var elapsed = AcceptanceRailPolicy.Backoff(
            attempt: 2,
            RunFailureClass.Quota,
            lastRequeueAt: Now,
            quotaResetAt: Now.AddMinutes(-1),
            Now);

        Assert.Equal(TimeSpan.FromMinutes(45), pending);
        Assert.Equal(TimeSpan.Zero, elapsed);
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

    /// <summary>
    /// A gate failure that is not rebase-recoverable, carrying the class the
    /// shared taxonomy assigned to its evidence.
    /// </summary>
    private static TaskIntegrationStatus ClassifiedFailure(
        RunFailureClass failureClass,
        string signature = RunFailureSignatures.GitNetworkTimeout)
        => Status(IntegrationStatuses.ConflictSkipped) with
        {
            Failure = new TaskIntegrationFailure
            {
                Code = AcceptedIntegrationFailureCodes.BuildGateFailed,
                RebaseRecoveryAvailable = false,
                FailureClass = failureClass,
                FailureSignature = signature,
            },
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
