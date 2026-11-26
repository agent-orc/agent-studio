namespace AgentStudio.OrchestrationDecisions;

/// <summary>RT-2: deterministic triage and incident ladder. Returned actions never move a card.</summary>
public static class RedTestTriagePolicy
{
    public static RedTestClassification Classify(RedTestTriageFacts facts)
    {
        if (facts.EarlierSuspectRevertIntegrated && ConfirmedFailure(facts)
            || facts.LaterControlContradictsAttribution)
            return RedTestClassification.WrongAttribution;
        if (facts.EnvelopeCanary == RedTestResult.Fail
            || facts.FingerprintMatchesReference == false && facts.ReferenceEnvelopeRepeat == RedTestResult.Pass
            || facts.LastGreenControlResults.Contains(RedTestResult.Fail))
            return RedTestClassification.Environment;
        if (facts.FirstResult == RedTestResult.Fail && facts.SameShaRepeat == RedTestResult.Pass)
            return RedTestClassification.Flaky;
        var bisect = BisectVerdictPolicy.Decide(facts.LastGreenControlResults, facts.BisectPoints);
        if (bisect.Classification == RedTestClassification.Flaky) return RedTestClassification.Flaky;
        if (facts.BuildOrPrepareFailure && ConfirmedFailure(facts) && facts.FingerprintMatchesReference != false)
            return bisect.Classification == RedTestClassification.Regression
                ? RedTestClassification.Regression : RedTestClassification.Ambiguous;
        if (facts.EnvelopeCanary != RedTestResult.Pass || facts.FingerprintMatchesReference != true
            || !ConfirmedFailure(facts))
            return RedTestClassification.Inconclusive;
        return bisect.Classification == RedTestClassification.Regression
            ? RedTestClassification.Regression : RedTestClassification.Ambiguous;
    }

    /// <summary>
    /// RT-3: the classification the post-merge runner's own facts support. The
    /// runner measures the same-SHA repeat, the last-green control and the bisect
    /// points; it does not compare its envelope with a reference envelope, which
    /// is the server's step once RT-6 provides the canary and the reference
    /// fingerprint. An envelope fact the runner did not measure therefore does not
    /// contradict the hint, while a measured one still decides. The hint is
    /// evidence on the receipt, never an action.
    /// </summary>
    public static RedTestClassification ClassificationHint(RedTestTriageFacts facts)
        => Classify(facts with
        {
            EnvelopeCanary = facts.EnvelopeCanary == RedTestResult.Unknown ? RedTestResult.Pass : facts.EnvelopeCanary,
            FingerprintMatchesReference = facts.FingerprintMatchesReference ?? true,
        });

    public static RedTestTriageDecision Decide(RedTestTriageFacts facts)
    {
        var classification = Classify(facts);
        if (classification == RedTestClassification.Ambiguous && facts.AmbiguousClassificationAttempted
            && facts.ModelClassification is { } modelClass) classification = modelClass;
        var attributed = classification == RedTestClassification.Regression
            ? BisectVerdictPolicy.Decide(facts.LastGreenControlResults, facts.BisectPoints).AttributedMerge : null;
        var actions = new List<RedTestDecision>();
        var lineStop = ConfirmedFailure(facts) && (facts.BuildOrPrepareFailure || facts.Category == RedTestCategory.Base)
            && classification is not (RedTestClassification.Environment or RedTestClassification.Inconclusive);
        if (lineStop)
        {
            actions.Add(new(RedTestAction.LineStop, "Build, prepare or a base test failed twice in the reference envelope."));
            if (facts.LineStopAge >= RedTestTriageBounds.LineStopOwnerAskAge)
                actions.Add(Ask(RedTestOwnerReason.LineStopAge,
                    [RedTestOwnerOption.RevertAttributedMerge, RedTestOwnerOption.KeepFixingForward,
                        RedTestOwnerOption.SwitchProjectToStrict],
                    attributed is not null && CanProposeRevert(1, facts.RevertAppliesCleanly, facts.LaterMergeCoupled)
                        ? RedTestOwnerOption.RevertAttributedMerge : RedTestOwnerOption.KeepFixingForward,
                    "The line stop has lasted 60 minutes; prefer a proven safe revert, otherwise keep fixing forward."));
        }
        if (facts.LineStopActive && HasCoveringPasses(facts))
            actions.Add(new(RedTestAction.ResolveLineStop, "Two covering passes contain the line-stop repair integration."));
        if (facts.T2Requested)
            actions.Add(Ask(RedTestOwnerReason.T2Request,
                [RedTestOwnerOption.GrantWithExpiry, RedTestOwnerOption.Refuse],
                facts.BaseTestMovingToRoutine ? RedTestOwnerOption.GrantWithExpiry : RedTestOwnerOption.Refuse,
                "An owner grant needs an expiry and use cap; refuse unless the base test is moving to routine."));

        if (classification == RedTestClassification.Ambiguous && !facts.AmbiguousClassificationAttempted
            && !facts.BuildOrPrepareFailure)
            actions.Add(new(RedTestAction.RequestClassification, "Request one bounded model classification with evidence; the model cannot choose an action."));
        if (classification == RedTestClassification.WrongAttribution)
            actions.Add(new(RedTestAction.WithdrawAttribution,
                "Withdraw the false blame, release suspect-only holds and reclassify using the remaining evidence."));
        if (classification == RedTestClassification.Environment
            || classification == RedTestClassification.Inconclusive && facts.FirstResult != RedTestResult.Pass)
            actions.Add(new(RedTestAction.MarkInconclusive, "Repeat the run in the reference envelope; attribute no merge."));
        if (classification == RedTestClassification.Flaky)
            actions.Add(new(RedTestAction.CountFlaky, "Count this run once in the 14-day flaky window."));

        var mayQuarantine = !facts.BuildOrPrepareFailure && facts.Category == RedTestCategory.Routine;
        if (mayQuarantine && HasLiftEvidence(facts))
        {
            actions.Add(new(RedTestAction.Lift, "Two consecutive covering passes in the reference envelope contain the repair integration."));
            return new(classification, attributed, actions);
        }
        if (mayQuarantine)
        {
            var reportOnly = classification == RedTestClassification.Flaky
                && (facts.DistinctFlakyRuns14Days >= RedTestTriageBounds.FlakyRunThreshold || HasMixedPoint(facts))
                || classification == RedTestClassification.Environment && facts.EnvironmentOccurrences >= 2;
            var held = ConfirmedFailure(facts)
                && classification is RedTestClassification.Regression or RedTestClassification.Ambiguous;
            if (reportOnly && facts.ActiveQuarantine?.Kind != RedTestQuarantineKind.ReportOnly)
                Start(facts, RedTestQuarantineKind.ReportOnly, actions);
            else if (held && facts.ActiveQuarantine is null)
                Start(facts, RedTestQuarantineKind.Held, actions);
            else if (facts.ActiveQuarantine is { Kind: RedTestQuarantineKind.Held } active)
                MaintainHeld(facts, active, actions);

            if (ConfirmedFailure(facts) && facts.SinceLastLift is { } age
                && age >= TimeSpan.Zero && age <= RedTestTriageBounds.RelapseWindow)
                actions.Add(Ask(RedTestOwnerReason.Relapse,
                    [RedTestOwnerOption.ReholdSameCard, RedTestOwnerOption.ReholdNewCardHigherRung, RedTestOwnerOption.ReportOnly],
                    RedTestOwnerOption.ReholdNewCardHigherRung, "A confirmed relapse within seven days needs a new repair card one rung up."));
        }

        if (lineStop || classification is RedTestClassification.Regression or RedTestClassification.Ambiguous
            or RedTestClassification.Environment || actions.Any(a => a.Action == RedTestAction.StartReportOnly))
        {
            var rung = RepairRung(classification, facts.IncidentAge >= RedTestTriageBounds.ReevaluateAge
                && facts.RepairCardLane is not (TaskStates.AutoReview or TaskStates.HumanReview or TaskStates.Completed or TaskStates.Archive)
                    ? Math.Max(2, facts.RepairAttempt) : facts.RepairAttempt,
                facts.SinceLastLift is { } age && age >= TimeSpan.Zero && age <= RedTestTriageBounds.RelapseWindow);
            actions.Add(attributed is not null && CanProposeRevert(1, facts.RevertAppliesCleanly, facts.LaterMergeCoupled)
                ? new(RedTestAction.ProposeRevert, "One confirmed suspect has a clean independent revert through the normal card flow.", ModelRung: rung)
                : new(RedTestAction.FixForward, "Create or reuse the repair at the front of the queue; preserve its current review or settled lane.", ModelRung: rung));
        }
        return new(classification, attributed, actions);
    }

    public static bool CanProposeRevert(int suspectCount, bool clean, bool coupled)
        => suspectCount == 1 && clean && !coupled;

    public static RedTestModelRung RepairRung(RedTestClassification classification, int attempt, bool relapse = false)
    {
        var initial = classification == RedTestClassification.Regression ? RedTestModelRung.High : RedTestModelRung.Medium;
        return attempt >= 2 || relapse ? (RedTestModelRung)((int)initial + 1) : initial;
    }

    private static bool ConfirmedFailure(RedTestTriageFacts facts)
        => facts.FirstResult == RedTestResult.Fail && facts.SameShaRepeat == RedTestResult.Fail;

    private static bool HasMixedPoint(RedTestTriageFacts facts)
        => facts.BisectPoints.Any(p => BisectVerdictPolicy.Verdict(p.Runs) == RedTestPointVerdict.Mixed
            || BisectVerdictPolicy.Verdict(p.ParentRuns) == RedTestPointVerdict.Mixed);

    private static bool HasLiftEvidence(RedTestTriageFacts facts)
        => facts.ActiveQuarantine is not null && HasCoveringPasses(facts);

    private static bool HasCoveringPasses(RedTestTriageFacts facts)
        => facts.LiftRuns.Count >= 2 && facts.LiftRuns.TakeLast(2).All(r => r.Result == RedTestResult.Pass
                && r.StandardEnvelope && r.ContainsRepairIntegration);

    private static void Start(RedTestTriageFacts facts, RedTestQuarantineKind kind, List<RedTestDecision> actions)
    {
        var active = facts.ProjectQuarantines.DistinctBy(q => (q.TestId, q.IncidentFingerprint)).ToArray();
        var alreadyCounted = active.Any(q => q.TestId == facts.TestId && q.IncidentFingerprint == facts.IncidentFingerprint);
        if (!alreadyCounted && active.Length >= RedTestTriageBounds.MaximumTotal
            || kind == RedTestQuarantineKind.Held && active.Count(q => q.Kind == RedTestQuarantineKind.Held) >= RedTestTriageBounds.MaximumHeld)
        {
            if (kind == RedTestQuarantineKind.Held
                && active.Count(q => q.Kind == RedTestQuarantineKind.Held) >= RedTestTriageBounds.MaximumHeld)
                actions.Add(new(RedTestAction.LineStop, "A fourth held test would exceed the project cap."));
            actions.Add(Ask(RedTestOwnerReason.CapExceeded,
                [RedTestOwnerOption.StopLine, RedTestOwnerOption.RaiseCapOnce, RedTestOwnerOption.SwitchProjectToStrict],
                RedTestOwnerOption.StopLine, "At most three held and eight total quarantines may be active."));
            return;
        }
        actions.Add(kind == RedTestQuarantineKind.Held
            ? new(RedTestAction.StartHeld, "Confirmed routine failure: hold covering folders and confirmed suspect paths; keep running the test.", RedTestTriageBounds.HeldDuration)
            : new(RedTestAction.StartReportOnly, "Flaky or recurring environment evidence: report the test with a fix card and hold nothing.", RedTestTriageBounds.ReportOnlyDuration));
    }

    private static void MaintainHeld(RedTestTriageFacts facts, RedTestActiveQuarantine active, List<RedTestDecision> actions)
    {
        if (active.Age < RedTestTriageBounds.HeldDuration) return;
        if (active.Age >= RedTestTriageBounds.HeldMaximumAge)
        {
            actions.Add(Ask(RedTestOwnerReason.RenewalLimit,
                [RedTestOwnerOption.Extend24Hours, RedTestOwnerOption.RevertAttributedMerge,
                    RedTestOwnerOption.ReportOnlyWithCard, RedTestOwnerOption.StopLine],
                facts.AreaHasNewFailure ? RedTestOwnerOption.RevertAttributedMerge : RedTestOwnerOption.ReportOnlyWithCard,
                "The 48-hour limit requires an owner decision; prefer report-only if the area has no new failure, otherwise revert."));
            return;
        }
        if (active.Renewals >= RedTestTriageBounds.MaximumRenewals) return;
        if (facts.RepairCardLane is TaskStates.Progress or TaskStates.AutoReview)
            actions.Add(new(RedTestAction.Renew, "Renew once while the repair is coding, under review or waiting for integration.", RedTestTriageBounds.RenewalDuration));
        else
            actions.Add(Ask(RedTestOwnerReason.RepairNotInProgress,
                [RedTestOwnerOption.Extend24Hours, RedTestOwnerOption.RevertAttributedMerge,
                    RedTestOwnerOption.ReportOnlyWithCard, RedTestOwnerOption.StopLine],
                facts.AreaHasNewFailure ? RedTestOwnerOption.RevertAttributedMerge : RedTestOwnerOption.ReportOnlyWithCard,
                "The held quarantine expired without an in-progress repair; the owner must choose its disposition."));
    }

    private static RedTestDecision Ask(RedTestOwnerReason reason, IReadOnlyList<RedTestOwnerOption> options,
        RedTestOwnerOption recommended, string explanation)
        => new(RedTestAction.AskOwner, explanation, Question: new(reason, options, recommended, explanation));
}
