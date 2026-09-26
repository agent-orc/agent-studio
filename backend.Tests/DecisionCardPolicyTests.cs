using System.Linq;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Direct policy-matrix tests for the AGT-2795 decision card. No filesystem,
/// process, clock, or DI: the branching lives in <see cref="DecisionCardPolicy"/>
/// and is proven here without a host, per the .NET backend style guide.
/// </summary>
public class DecisionCardPolicyTests
{
    private static DecisionContent Valid(string status = DecisionStatuses.Pending) => new()
    {
        Question = "Lock file or no lock file?",
        Options =
        [
            new DecisionOption { Id = "a", Label = "Lock file", Consequences = "Reproducible", Effort = "S", Risk = "Churn" },
            new DecisionOption { Id = "b", Label = "No lock file", Consequences = "Simpler", Effort = "S", Risk = "Drift" },
        ],
        RecommendedOptionId = "a",
        RecommendationReason = "Reproducibility wins",
        Decider = DecisionDeciders.Operator,
        Status = status,
    };

    [Fact]
    public void ValidContent_HasNoErrors()
    {
        Assert.Empty(DecisionCardPolicy.ValidateContent(Valid()));
    }

    [Fact]
    public void MissingQuestion_IsRejected()
    {
        var errors = DecisionCardPolicy.ValidateContent(Valid() with { Question = "  " });
        Assert.Contains(errors, e => e.Code == DecisionCardErrorCode.MissingQuestion);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void OptionCountOutsideTwoToFour_IsRejected(int count)
    {
        var options = Enumerable.Range(0, count)
            .Select(i => new DecisionOption { Id = $"o{i}", Label = $"Option {i}" })
            .ToList();
        var errors = DecisionCardPolicy.ValidateContent(Valid() with { Options = options, RecommendedOptionId = null });
        Assert.Contains(errors, e => e.Code == DecisionCardErrorCode.OptionCount);
    }

    [Fact]
    public void OptionWithoutIdOrLabel_IsRejected()
    {
        var content = Valid() with
        {
            Options = [new DecisionOption { Id = "", Label = "" }, new DecisionOption { Id = "b", Label = "B" }],
            RecommendedOptionId = null,
        };
        Assert.Contains(DecisionCardPolicy.ValidateContent(content), e => e.Code == DecisionCardErrorCode.OptionShape);
    }

    [Fact]
    public void DuplicateOptionId_IsRejected()
    {
        var content = Valid() with
        {
            Options = [new DecisionOption { Id = "a", Label = "A" }, new DecisionOption { Id = "A", Label = "A2" }],
            RecommendedOptionId = null,
        };
        Assert.Contains(DecisionCardPolicy.ValidateContent(content), e => e.Code == DecisionCardErrorCode.DuplicateOptionId);
    }

    [Fact]
    public void RecommendationForUnknownOption_IsRejected()
    {
        var errors = DecisionCardPolicy.ValidateContent(Valid() with { RecommendedOptionId = "zzz" });
        Assert.Contains(errors, e => e.Code == DecisionCardErrorCode.UnknownRecommendation);
    }

    [Fact]
    public void RecommendationWithoutReason_IsRejected()
    {
        Assert.Contains(DecisionCardPolicy.ValidateContent(Valid() with { RecommendationReason = null }),
            error => error.Code == DecisionCardErrorCode.IncompleteRecommendation);
    }

    [Fact]
    public void Choice_RequiresKnownOption_AndAllowsOptionalRationale()
    {
        var content = Valid();
        Assert.Contains(DecisionCardPolicy.ValidateChoice(content, "zzz", "because"),
            e => e.Code == DecisionCardErrorCode.UnknownOption);
        Assert.Empty(DecisionCardPolicy.ValidateChoice(content, "a", "   "));
        Assert.Empty(DecisionCardPolicy.ValidateChoice(content, "a", "because"));
    }

    [Fact]
    public void Choice_OnDecidedCard_IsNotOpen()
    {
        var content = Valid(DecisionStatuses.Decided);
        Assert.Contains(DecisionCardPolicy.ValidateChoice(content, "a", "because"),
            e => e.Code == DecisionCardErrorCode.NotOpen);
    }

    [Fact]
    public void Choice_OnReopenedCard_IsAllowed()
    {
        var content = Valid(DecisionStatuses.Pending);
        Assert.Empty(DecisionCardPolicy.ValidateChoice(content, "b", "second time"));
    }

    [Fact]
    public void Decide_RecordsOptionRationaleDeciderAndTimestamp()
    {
        var now = new System.DateTime(2026, 9, 13, 10, 0, 0, System.DateTimeKind.Utc);
        var decided = DecisionCardPolicy.Decide(Valid(), "b", "  simpler  ", "alice", now, "decision-record.md");
        Assert.Equal(DecisionStatuses.Decided, decided.Status);
        Assert.Equal("b", decided.ChosenOptionId);
        Assert.Equal("simpler", decided.Rationale);
        Assert.Equal("alice", decided.DecidedBy);
        Assert.Equal(now, decided.DecidedAt);
        Assert.Equal("decision-record.md", decided.RecordPath);
        Assert.Equal("No lock file", DecisionCardPolicy.ChosenOption(decided)!.Label);
    }

    [Fact]
    public void Reopen_RequiresDecidedCard_AndClearsChoice()
    {
        Assert.Contains(DecisionCardPolicy.ValidateReopen(Valid(), "needs review"), e => e.Code == DecisionCardErrorCode.NotDecided);

        var decided = DecisionCardPolicy.Decide(Valid(), "a", "reason", "op", System.DateTime.UtcNow);
        Assert.Empty(DecisionCardPolicy.ValidateReopen(decided, "needs review"));
        Assert.Contains(DecisionCardPolicy.ValidateReopen(decided, null), e => e.Code == DecisionCardErrorCode.MissingReopenNote);

        var reopened = DecisionCardPolicy.Reopen(decided, "  needs rework  ");
        Assert.Equal(DecisionStatuses.Pending, reopened.Status);
        Assert.Null(reopened.ChosenOptionId);
        Assert.Null(reopened.DecidedAt);
        Assert.Equal("needs rework", reopened.ReopenNote);
    }

    [Fact]
    public void Kind_NormalizeAndPredicates()
    {
        Assert.Equal(TaskKinds.Decision, TaskKinds.Normalize("Decision"));
        Assert.Equal(TaskKinds.Task, TaskKinds.Normalize("garbage"));
        Assert.True(TaskKinds.IsDecision("decision"));
        Assert.False(TaskKinds.IsDecision("epic"));
    }

    [Fact]
    public void NamedDeciderAndRole_AreCheckedAgainstCaller()
    {
        Assert.True(DecisionCardPolicy.MayDecide(Valid(), "alice", null));
        Assert.True(DecisionCardPolicy.MayDecide(Valid() with { Decider = "alice" }, "alice", null));
        Assert.False(DecisionCardPolicy.MayDecide(Valid() with { Decider = "alice" }, "bob", null));
        Assert.True(DecisionCardPolicy.MayDecide(Valid() with { Decider = "role:owner" }, "bob", "owner"));
        Assert.False(DecisionCardPolicy.MayDecide(Valid() with { Decider = "role:owner" }, "bob", "operator"));
    }

    [Fact]
    public void RemoteClaim_RefusesDecisionAndItsPendingDependant()
    {
        var settings = new ProjectSettings
        {
            PickupMode = PickupModes.Auto,
            ExecutionLocation = "runner-1",
            IntakeEnabled = false,
        };
        var decision = new TaskInfo
        {
            Id = "choice", Key = "APP-1", Kind = TaskKinds.Decision,
            State = TaskStates.Ready, Decision = Valid(),
        };
        var dependant = new TaskInfo
        {
            Id = "implementation", Key = "APP-2", State = TaskStates.Ready,
            References = new TaskReferences { DependsOn = [new TaskDependencyReference("APP-1")] },
        };
        var plain = new TaskInfo { Id = "plain", Key = "APP-3", State = TaskStates.Ready };
        var index = TaskReferenceIndex.Build([decision, dependant, plain]);
        Assert.Equal(["APP-1"], DecisionBlockProjection.BlockedBy(index.EvaluateWaitsOn(dependant)));
        Assert.True(RemoteDispatchEligibility.IsAssignedAndRoutable(
            plain, settings, "runner-1", null, index));
        Assert.False(RemoteDispatchEligibility.IsAssignedAndRoutable(
            decision, settings, "runner-1", null, index));
        Assert.False(RemoteDispatchEligibility.IsAssignedAndRoutable(
            dependant, settings, "runner-1", null, index));
    }
}
