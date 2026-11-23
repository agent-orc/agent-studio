using AgentStudio.OrchestrationDecisions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class RedTestTriagePolicyTests
{
    private static readonly RedTestRunPair Pass = new(RedTestResult.Pass, RedTestResult.Pass);
    private static readonly RedTestRunPair Fail = new(RedTestResult.Fail, RedTestResult.Fail);
    private static readonly RedTestRunPair Mixed = new(RedTestResult.Fail, RedTestResult.Pass);
    private static RedTestTriageFacts Confirmed => new("Test.Method", "backend-routine", RedTestCategory.Routine)
    {
        SameShaRepeat = RedTestResult.Fail, EnvelopeCanary = RedTestResult.Pass,
        FingerprintMatchesReference = true, IncidentFingerprint = "failure-a",
    };
    private static RedTestTriageFacts Regression => Confirmed with
    {
        LastGreenControlResults = [RedTestResult.Pass, RedTestResult.Pass],
        BisectPoints = [new("older", Pass, Pass), new("first-fail", Fail, Pass)],
    };

    public static TheoryData<string, RedTestTriageFacts, RedTestClassification> HistoricalReds => new()
    {
        // AGT-3975: documentation-only suspect, fixture races. A recovering focused repeat blames no merge.
        { "AGT-3975", Confirmed with { TestId = "DockerFixture", SameShaRepeat = RedTestResult.Pass }, RedTestClassification.Flaky },
        // AGT-3980: 4/90 failures alone, 11/90 under load. Double-run bisect exposes the race even after two reds.
        { "AGT-3980", Regression with { TestId = "EngineReportReviewTests.Theory", BisectPoints = [new("point", Mixed, Pass)] }, RedTestClassification.Flaky },
        // AGT-3981: inherited RUNNER_* and AGENT_HOST_* variables fail the envelope canary first.
        { "AGT-3981", Confirmed with { TestId = "RunnerOptionsTests", EnvelopeCanary = RedTestResult.Fail }, RedTestClassification.Environment },
        // AGT-3983: daemon cgroup rather than worker leaf, green in the reference envelope.
        { "AGT-3983", Confirmed with { TestId = "CgroupTests", FingerprintMatchesReference = false,
            ReferenceEnvelopeRepeat = RedTestResult.Pass }, RedTestClassification.Environment },
    };

    [Theory]
    [MemberData(nameof(HistoricalReds))]
    public void Dossier_four_reds_attribute_no_merge_and_never_stop_the_line(string card,
        RedTestTriageFacts facts, RedTestClassification expected)
    {
        Assert.StartsWith("AGT-", card);
        var decision = RedTestTriagePolicy.Decide(facts);
        Assert.Equal(expected, decision.Classification);
        Assert.Null(decision.AttributedMerge);
        Assert.DoesNotContain(decision.Decisions, d => d.Action == RedTestAction.LineStop);
    }

    [Fact]
    public void Regression_requires_two_green_controls_and_a_double_run_parent_boundary()
    {
        var decision = RedTestTriagePolicy.Decide(Regression);
        Assert.Equal(RedTestClassification.Regression, decision.Classification);
        Assert.Equal("first-fail", decision.AttributedMerge);
        Assert.Contains(decision.Decisions, d => d.Action == RedTestAction.StartHeld);
        Assert.Equal(RedTestModelRung.High, Assert.Single(decision.Decisions, d => d.Action == RedTestAction.FixForward).ModelRung);
        Assert.Equal(RedTestClassification.Ambiguous, RedTestTriagePolicy.Classify(Regression with
        { BisectPoints = [new("first-fail", Fail, Fail)] }));
        Assert.NotEqual(RedTestClassification.Regression, RedTestTriagePolicy.Classify(Regression with
        { LastGreenControlResults = [RedTestResult.Pass] }));
    }

    [Theory]
    [InlineData(false, RedTestCategory.Base)]
    [InlineData(true, RedTestCategory.Routine)]
    public void Confirmed_base_build_or_prepare_stops_line_and_never_enters_quarantine(bool build, RedTestCategory category)
    {
        var facts = Confirmed with { Category = category, BuildOrPrepareFailure = build };
        var decision = RedTestTriagePolicy.Decide(facts);
        Assert.Contains(decision.Decisions, d => d.Action == RedTestAction.LineStop);
        Assert.DoesNotContain(decision.Decisions, d => d.Action is RedTestAction.StartHeld or RedTestAction.StartReportOnly);
        Assert.DoesNotContain(RedTestTriagePolicy.Decide(facts with { SameShaRepeat = RedTestResult.Unknown }).Decisions,
            d => d.Action == RedTestAction.LineStop);
    }

    [Fact]
    public void Build_confirmation_can_stop_line_before_a_canary_can_run()
        => Assert.Contains(RedTestTriagePolicy.Decide(Confirmed with { BuildOrPrepareFailure = true,
            EnvelopeCanary = RedTestResult.Unknown, FingerprintMatchesReference = null }).Decisions,
            d => d.Action == RedTestAction.LineStop);

    [Fact]
    public void Base_flakiness_is_counted_but_never_quarantined_automatically()
    {
        var decision = RedTestTriagePolicy.Decide(Confirmed with { Category = RedTestCategory.Base,
            SameShaRepeat = RedTestResult.Pass, DistinctFlakyRuns14Days = 3 });
        Assert.Contains(decision.Decisions, d => d.Action == RedTestAction.CountFlaky);
        Assert.DoesNotContain(decision.Decisions, d => d.Action is RedTestAction.StartReportOnly or RedTestAction.StartHeld);
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void Flaky_threshold_counts_distinct_runs(int count, bool quarantine)
    {
        var decision = RedTestTriagePolicy.Decide(Confirmed with
        { SameShaRepeat = RedTestResult.Pass, DistinctFlakyRuns14Days = count });
        Assert.Contains(decision.Decisions, d => d.Action == RedTestAction.CountFlaky);
        Assert.Equal(quarantine, decision.Decisions.Any(d => d.Action == RedTestAction.StartReportOnly));
        if (quarantine)
        {
            Assert.Equal(TimeSpan.FromDays(7), Assert.Single(decision.Decisions, d => d.Action == RedTestAction.StartReportOnly).Duration);
            Assert.Equal(RedTestModelRung.Medium, Assert.Single(decision.Decisions, d => d.Action == RedTestAction.FixForward).ModelRung);
        }
    }

    [Fact]
    public void Mixed_bisect_starts_report_only_immediately()
        => Assert.Contains(RedTestTriagePolicy.Decide(Regression with
        { BisectPoints = [new("point", Mixed, Pass)], DistinctFlakyRuns14Days = 1 }).Decisions,
            d => d.Action == RedTestAction.StartReportOnly);

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void Environment_is_inconclusive_and_recurring_environment_is_report_only(int occurrences, bool report)
    {
        var decision = RedTestTriagePolicy.Decide(Confirmed with
        { EnvelopeCanary = RedTestResult.Fail, EnvironmentOccurrences = occurrences });
        Assert.Contains(decision.Decisions, d => d.Action == RedTestAction.MarkInconclusive);
        Assert.Equal(report, decision.Decisions.Any(d => d.Action == RedTestAction.StartReportOnly));
        Assert.DoesNotContain(decision.Decisions, d => d.Action == RedTestAction.StartHeld);
    }

    [Fact]
    public void Unverified_envelope_is_inconclusive_and_never_holds()
    {
        var decision = RedTestTriagePolicy.Decide(Confirmed with { FingerprintMatchesReference = false });
        Assert.Equal(RedTestClassification.Inconclusive, decision.Classification);
        Assert.Contains(decision.Decisions, d => d.Action == RedTestAction.MarkInconclusive);
        Assert.DoesNotContain(decision.Decisions, d => d.Action == RedTestAction.StartHeld);
    }

    [Theory]
    [InlineData(3, RedTestQuarantineKind.Held)]
    [InlineData(8, RedTestQuarantineKind.ReportOnly)]
    public void Cap_exceeded_asks_owner_and_a_fourth_held_test_stops_line(int count, RedTestQuarantineKind kind)
    {
        var facts = Confirmed with { ProjectQuarantines = Enumerable.Range(0, count)
            .Select(i => new RedTestQuarantine($"test-{i}", "fingerprint", kind)).ToArray() };
        var decision = RedTestTriagePolicy.Decide(facts);
        Assert.Equal(kind == RedTestQuarantineKind.Held, decision.Decisions.Any(d => d.Action == RedTestAction.LineStop));
        Assert.DoesNotContain(decision.Decisions, d => d.Action == RedTestAction.StartHeld);
        var question = Assert.Single(decision.Decisions, d => d.Action == RedTestAction.AskOwner).Question!;
        Assert.Equal(RedTestOwnerReason.CapExceeded, question.Reason);
        Assert.Equal(RedTestOwnerOption.StopLine, question.Recommended);
    }

    [Theory]
    [InlineData(7, true)]
    [InlineData(8, false)]
    public void Relapse_reholds_and_asks_owner_for_a_new_card_one_rung_up(int days, bool ask)
    {
        var decision = RedTestTriagePolicy.Decide(Confirmed with { SinceLastLift = TimeSpan.FromDays(days) });
        Assert.Contains(decision.Decisions, d => d.Action == RedTestAction.StartHeld);
        Assert.Equal(ask, decision.Decisions.Any(d => d.Question?.Reason == RedTestOwnerReason.Relapse));
        if (ask) Assert.Equal(RedTestOwnerOption.ReholdNewCardHigherRung,
            Assert.Single(decision.Decisions, d => d.Question?.Reason == RedTestOwnerReason.Relapse).Question!.Recommended);
    }

    [Theory]
    [InlineData(23, 0, TaskStates.Progress, false, false)]
    [InlineData(24, 0, TaskStates.Progress, true, false)]
    [InlineData(24, 0, TaskStates.AutoReview, true, false)]
    [InlineData(24, 0, TaskStates.HumanReview, false, true)]
    [InlineData(24, 0, TaskStates.Completed, false, true)]
    [InlineData(24, 0, TaskStates.Ready, false, true)]
    [InlineData(47, 1, TaskStates.Progress, false, false)]
    [InlineData(48, 1, TaskStates.AutoReview, false, true)]
    [InlineData(48, 0, TaskStates.Progress, false, true)]
    public void Held_renewal_is_once_for_24_hours_only_while_repair_is_in_progress(int hours, int renewals,
        string lane, bool renew, bool ask)
    {
        var decision = RedTestTriagePolicy.Decide(Confirmed with
        { ActiveQuarantine = new(RedTestQuarantineKind.Held, TimeSpan.FromHours(hours), renewals), RepairCardLane = lane });
        Assert.Equal(renew, decision.Decisions.Any(d => d.Action == RedTestAction.Renew));
        Assert.Equal(ask, decision.Decisions.Any(d => d.Action == RedTestAction.AskOwner));
        if (renew) Assert.Equal(TimeSpan.FromHours(24), Assert.Single(decision.Decisions, d => d.Action == RedTestAction.Renew).Duration);
        Assert.DoesNotContain(decision.Decisions, d => d.Action == RedTestAction.StartHeld);
        foreach (var question in decision.Decisions.Where(d => d.Question is not null).Select(d => d.Question!))
            Assert.Contains(question.Recommended, question.Options);
    }

    [Theory]
    [InlineData(false, RedTestOwnerOption.ReportOnlyWithCard)]
    [InlineData(true, RedTestOwnerOption.RevertAttributedMerge)]
    public void Renewal_limit_recommendation_depends_on_new_failures(bool newFailure, RedTestOwnerOption expected)
    {
        var decision = RedTestTriagePolicy.Decide(Confirmed with { AreaHasNewFailure = newFailure,
            ActiveQuarantine = new(RedTestQuarantineKind.Held, TimeSpan.FromHours(48), 1) });
        var question = Assert.Single(decision.Decisions, d => d.Action == RedTestAction.AskOwner).Question!;
        Assert.Equal(RedTestOwnerReason.RenewalLimit, question.Reason);
        Assert.Equal(expected, question.Recommended);
    }

    [Theory]
    [InlineData(59, false, false)]
    [InlineData(60, true, false)]
    [InlineData(61, true, true)]
    public void Line_stop_asks_at_60_minutes_and_recommends_only_a_safe_revert(int minutes, bool asks, bool clean)
    {
        var decision = RedTestTriagePolicy.Decide(Regression with { Category = RedTestCategory.Base,
            LineStopAge = TimeSpan.FromMinutes(minutes), RevertAppliesCleanly = clean, RepairCardLane = TaskStates.AutoReview });
        Assert.Equal(asks, decision.Decisions.Any(d => d.Question?.Reason == RedTestOwnerReason.LineStopAge));
        if (asks) Assert.Equal(clean ? RedTestOwnerOption.RevertAttributedMerge : RedTestOwnerOption.KeepFixingForward,
            Assert.Single(decision.Decisions, d => d.Action == RedTestAction.AskOwner).Question!.Recommended);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void T2_request_asks_without_automatically_quarantining_base(bool moving)
    {
        var decision = RedTestTriagePolicy.Decide(Confirmed with { Category = RedTestCategory.Base,
            T2Requested = true, BaseTestMovingToRoutine = moving });
        Assert.Equal(moving ? RedTestOwnerOption.GrantWithExpiry : RedTestOwnerOption.Refuse,
            Assert.Single(decision.Decisions, d => d.Action == RedTestAction.AskOwner).Question!.Recommended);
        Assert.DoesNotContain(decision.Decisions, d => d.Action == RedTestAction.StartHeld);
    }

    [Theory]
    [InlineData(RedTestQuarantineKind.Held)]
    [InlineData(RedTestQuarantineKind.ReportOnly)]
    public void Lift_requires_the_latest_two_covering_passes_with_repair_and_standard_envelope(RedTestQuarantineKind kind)
    {
        var good = new RedTestLiftRun(RedTestResult.Pass, true, true);
        var facts = Confirmed with { FirstResult = RedTestResult.Pass, SameShaRepeat = RedTestResult.Pass,
            ActiveQuarantine = new(kind, TimeSpan.FromHours(24), 0), LiftRuns = [good, good] };
        Assert.Contains(RedTestTriagePolicy.Decide(facts).Decisions, d => d.Action == RedTestAction.Lift);
        foreach (var bad in new[] { good with { Result = RedTestResult.Fail }, good with { StandardEnvelope = false },
            good with { ContainsRepairIntegration = false } })
            Assert.DoesNotContain(RedTestTriagePolicy.Decide(facts with { LiftRuns = [good, bad, good] }).Decisions,
                d => d.Action == RedTestAction.Lift);
        Assert.DoesNotContain(RedTestTriagePolicy.Decide(facts with { LiftRuns = [good] }).Decisions, d => d.Action == RedTestAction.Lift);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Contradictory_evidence_withdraws_attribution(bool reverted)
    {
        var decision = RedTestTriagePolicy.Decide(Regression with { EarlierSuspectRevertIntegrated = reverted,
            LaterControlContradictsAttribution = !reverted });
        Assert.Equal(RedTestClassification.WrongAttribution, decision.Classification);
        Assert.Null(decision.AttributedMerge);
        Assert.Contains(decision.Decisions, d => d.Action == RedTestAction.WithdrawAttribution);
        Assert.DoesNotContain(decision.Decisions, d => d.Action == RedTestAction.ProposeRevert);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    public void Revert_needs_confirmed_clean_independent_suspect(bool clean, bool coupled, bool revert)
    {
        var decision = RedTestTriagePolicy.Decide(Regression with { RevertAppliesCleanly = clean, LaterMergeCoupled = coupled });
        Assert.Equal(revert, decision.Decisions.Any(d => d.Action == RedTestAction.ProposeRevert));
        Assert.Equal(!revert, decision.Decisions.Any(d => d.Action == RedTestAction.FixForward));
    }

    [Fact]
    public void Ambiguous_evidence_requests_one_classification_and_holds_without_a_suspect()
    {
        var first = RedTestTriagePolicy.Decide(Confirmed);
        Assert.Equal(RedTestClassification.Ambiguous, first.Classification);
        Assert.Null(first.AttributedMerge);
        Assert.Contains(first.Decisions, d => d.Action == RedTestAction.RequestClassification);
        Assert.Contains(first.Decisions, d => d.Action == RedTestAction.StartHeld);
        Assert.DoesNotContain(RedTestTriagePolicy.Decide(Confirmed with { AmbiguousClassificationAttempted = true }).Decisions,
            d => d.Action == RedTestAction.RequestClassification);
    }

    [Fact]
    public void A_new_fingerprint_does_not_renew_an_old_quarantine()
    {
        var decision = RedTestTriagePolicy.Decide(Confirmed with
        { ProjectQuarantines = [new(Confirmed.TestId, "old-fingerprint", RedTestQuarantineKind.Held)] });
        Assert.Contains(decision.Decisions, d => d.Action == RedTestAction.StartHeld);
        Assert.DoesNotContain(decision.Decisions, d => d.Action == RedTestAction.Renew);
    }

    [Fact]
    public void Conversion_to_report_only_does_not_consume_an_extra_cap_slot()
    {
        var quarantines = Enumerable.Range(0, 7).Select(i => new RedTestQuarantine($"other-{i}", "fingerprint", RedTestQuarantineKind.ReportOnly))
            .Append(new(Confirmed.TestId, Confirmed.IncidentFingerprint, RedTestQuarantineKind.Held)).ToArray();
        var decision = RedTestTriagePolicy.Decide(Confirmed with { ProjectQuarantines = quarantines,
            ActiveQuarantine = new(RedTestQuarantineKind.Held, TimeSpan.FromHours(10), 0),
            EnvelopeCanary = RedTestResult.Fail, EnvironmentOccurrences = 2 });
        Assert.Contains(decision.Decisions, d => d.Action == RedTestAction.StartReportOnly);
        Assert.DoesNotContain(decision.Decisions, d => d.Action == RedTestAction.LineStop);
    }

    [Theory]
    [InlineData(RedTestClassification.Regression, 1, RedTestModelRung.High)]
    [InlineData(RedTestClassification.Regression, 2, RedTestModelRung.HighPlusOne)]
    [InlineData(RedTestClassification.Environment, 1, RedTestModelRung.Medium)]
    [InlineData(RedTestClassification.Environment, 2, RedTestModelRung.High)]
    [InlineData(RedTestClassification.Flaky, 1, RedTestModelRung.Medium)]
    [InlineData(RedTestClassification.Flaky, 3, RedTestModelRung.High)]
    public void Repair_model_moves_one_rung_on_second_attempt(RedTestClassification classification, int attempt, RedTestModelRung expected)
        => Assert.Equal(expected, RedTestTriagePolicy.RepairRung(classification, attempt));
}
