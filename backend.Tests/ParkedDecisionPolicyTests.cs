using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2816. AGT-2736 sat in <c>5e-escalated</c> for three days showing
/// <c>Result: Success</c> and <c>Open Items: None</c> while the run was in fact
/// parked on an operator decision. Three things had to become true, and each is
/// pinned here:
///
/// <list type="number">
///   <item>the park projects the QUESTION, not just a slug
///     (<see cref="ParkedDecisionReader"/>);</item>
///   <item>the projection says whether the last recall verdict is still current
///     (<see cref="ParkedBlockerMarker.ToStatus"/>);</item>
///   <item>a parked card never reports zero open items
///     (<see cref="ParkedOpenItems"/> and the two stub producers).</item>
/// </list>
/// </summary>
public sealed class ParkedDecisionPolicyTests
{
    private static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>The AGT-2736 message, verbatim in shape.</summary>
    private const string Agt2736Message = """
        Which deployment strategy should I implement?

        - Option A: managed connector. Recommended for simpler operations.
        - Option B: direct LAN-reachable Studio backend. Requires customer network access.
        - Option C: ship both behind a setting. Doubles the surface to support.

        The full write-up is in docs/operations/setup/docker-compose-connector-gap.md.
        """;

    // -- The reason is a question ------------------------------------------

    [Theory]
    // The AGT-2736 reason: a sentence whose payload is a slug.
    [InlineData(
        "[agent-needs-input] The remote agent requires operator input: choose-connector-vs-lan-deployment-strategy",
        "choose-connector-vs-lan-deployment-strategy")]
    [InlineData("[agent-needs-input] clarify-target", "clarify-target")]
    // A prose park still yields the trailing identifier when it has one.
    [InlineData("Parked on choose-primary-column", "choose-primary-column")]
    // No slug-shaped token at all: an empty id beats a fabricated one.
    [InlineData("[watchdog-kill] CLI exceeded the watchdog deadline", "")]
    [InlineData(null, "")]
    public void ReadQuestionId_KeepsTheSlugAsAnIdentifier(string? reason, string expected)
        => Assert.Equal(expected, ParkedDecisionReader.ReadQuestionId(reason));

    [Fact]
    public void Read_LiftsTheQuestionOptionsAndDocumentOutOfTheRunsOwnMessage()
    {
        var decision = ParkedDecisionReader.Read(
            "[agent-needs-input] The remote agent requires operator input: choose-connector-vs-lan-deployment-strategy",
            Agt2736Message);

        Assert.Equal("choose-connector-vs-lan-deployment-strategy", decision.QuestionId);
        Assert.Equal("Which deployment strategy should I implement?", decision.Question);
        Assert.True(decision.Stated);

        Assert.Equal(3, decision.Options.Count);
        Assert.Equal(["a", "b", "c"], decision.Options.Select(option => option.Id));
        Assert.Equal("managed connector.", decision.Options[0].Label);
        Assert.Equal("Recommended for simpler operations.", decision.Options[0].Consequences);
        Assert.True(decision.Options[0].Recommended);
        Assert.False(decision.Options[1].Recommended);

        Assert.Equal(
            ["docs/operations/setup/docker-compose-connector-gap.md"],
            decision.Documents);
    }

    [Fact]
    public void Read_WithoutAMessage_StatesNoQuestionRatherThanPresentingTheSlugAsOne()
    {
        var decision = ParkedDecisionReader.Read("[agent-needs-input] choose-connector-vs-lan", message: null);

        Assert.Equal("choose-connector-vs-lan", decision.QuestionId);
        Assert.Equal("", decision.Question);
        Assert.False(decision.Stated);
        Assert.Empty(decision.Options);
        Assert.Empty(decision.Documents);
    }

    [Fact]
    public void ReadQuestion_FallsBackToTheFirstSentenceWhenNoLineAsks()
    {
        var question = ParkedDecisionReader.ReadQuestion(
            "I cannot pick the retention window without a policy owner. Everything else is ready.");

        Assert.Equal("I cannot pick the retention window without a policy owner.", question);
    }

    [Fact]
    public void ReadOptions_DoesNotInventOptionsFromAPlainBulletList()
    {
        var options = ParkedDecisionReader.ReadOptions("""
            Which retention window?

            - I checked the existing policy.
            - I could not reach the owner.
            """);

        Assert.Empty(options);
    }

    [Fact]
    public void ReadQuestion_BoundsALongQuestionToOneLine()
    {
        var question = ParkedDecisionReader.ReadQuestion(new string('a', 40) + " " + new string('b', 400) + "?");

        Assert.True(question.Length <= ParkedDecisionReader.MaximumQuestionLength + 1);
        Assert.EndsWith("…", question);
    }

    // -- Projection: park present, absent, stale evaluation ----------------

    [Fact]
    public void ToStatus_WithoutARecord_ProjectsNothing()
        => Assert.Null(ParkedBlockerMarker.ToStatus(null, Now));

    [Fact]
    public void ToStatus_ProjectsTheQuestionOptionsDocumentsAndAge()
    {
        var record = Record(Now.AddDays(-3)) with
        {
            NeedsInputFile = NeedsInputArtifact.RelativePath,
            Decision = ParkedDecisionReader.Read("[agent-needs-input] choose-connector-vs-lan", Agt2736Message),
            LastEvaluation = new ParkedBlockerEvaluation
            {
                Status = ParkedBlockerStatuses.Blocked,
                At = Now.AddMinutes(-10),
                Detail = "Only a person can clear this park.",
            },
        };

        var status = ParkedBlockerMarker.ToStatus(record, Now)!;

        Assert.Equal(TimeSpan.FromDays(3).TotalSeconds, status.ParkedForSeconds);
        Assert.Equal(TaskStates.Escalated, status.Lane);
        Assert.Equal(NeedsInputArtifact.RelativePath, status.NeedsInputFile);
        Assert.True(status.RequiresDecisionCard);
        Assert.Equal("Which deployment strategy should I implement?", status.Decision!.Question);
        Assert.Equal(3, status.Decision.Options.Count);
        Assert.Single(status.Decision.Documents);
        Assert.Equal(600, status.EvaluationAgeSeconds);
        Assert.False(status.EvaluationStale);
    }

    /// <summary>
    /// A park with no recorded verdict must not be projected as "still blocked":
    /// the marker's default status IS <c>blocked</c>, so only
    /// <see cref="ParkedBlockerStatus.EvaluationStale"/> separates "nobody has
    /// checked" from a real verdict.
    /// </summary>
    [Fact]
    public void ToStatus_ReportsAnUnevaluatedBlockerAsSuchInsteadOfAsAVerdict()
    {
        var never = ParkedBlockerMarker.ToStatus(Record(Now.AddDays(-4)), Now)!;

        Assert.True(never.EvaluationStale);
        Assert.Null(never.EvaluationAgeSeconds);
        Assert.Null(never.LastEvaluatedAt);
    }

    /// <summary>
    /// An OLD verdict is not stale. The sweep deliberately does not re-persist an
    /// unchanged verdict (that would reset the card's activity age), so the
    /// recorded instant means "this verdict has held since then" - evidence, not
    /// decay. An age threshold here would call a continuously re-checked card
    /// unchecked.
    /// </summary>
    [Fact]
    public void ToStatus_DoesNotTreatALongHeldVerdictAsUnchecked()
    {
        var held = ParkedBlockerMarker.ToStatus(
            Record(Now.AddDays(-4)) with
            {
                LastEvaluation = new ParkedBlockerEvaluation
                {
                    Status = ParkedBlockerStatuses.Blocked,
                    At = Now.AddDays(-4),
                    Detail = "still blocked",
                },
            },
            Now)!;

        Assert.False(held.EvaluationStale);
        Assert.Equal((long)TimeSpan.FromDays(4).TotalSeconds, held.EvaluationAgeSeconds);
    }

    [Theory]
    [InlineData(ParkedBlockerCatalog.OperatorDecision, true)]
    [InlineData(HumanReviewEscalationCategories.AgentNeedsInput, true)]
    [InlineData(HumanReviewEscalationCategories.HumanDecisionNeeded, true)]
    // A failure escalation is not a decision card: nobody chooses between
    // options, somebody fixes the fault.
    [InlineData(HumanReviewEscalationCategories.InfraCrash, false)]
    [InlineData(HumanReviewEscalationCategories.Quarantined, false)]
    [InlineData(null, false)]
    public void RequiresDecisionCard_SeparatesADecisionFromAFailure(string? blockerType, bool expected)
        => Assert.Equal(expected, ParkedBlockerCatalog.RequiresDecisionCard(blockerType));

    [Fact]
    public void Build_SeedsTheQuestionIdAtLaneChangeTimeEvenWithoutAMessage()
    {
        var record = ParkedBlockerCatalog.Build(
            TaskStates.Escalated,
            "[agent-needs-input] The remote agent requires operator input: choose-connector-vs-lan",
            Now)!;

        Assert.Equal("choose-connector-vs-lan", record.Decision!.QuestionId);
        Assert.False(record.Decision.Stated);
    }

    // -- A parked card never reports zero open items -----------------------

    [Fact]
    public void Items_AreNeverEmptyForAParkedCard()
    {
        foreach (var park in ParkedCases())
        {
            var items = ParkedOpenItems.Items(park);
            Assert.NotEmpty(items);
            Assert.DoesNotContain(items, item => IsNone(item));
        }
    }

    [Fact]
    public void Items_NameTheQuestionWhenTheRunStatedOneAndSaySoWhenItDidNot()
    {
        var stated = ParkedOpenItems.Items(ParkedBlockerMarker.ToStatus(
            Record(Now) with { Decision = ParkedDecisionReader.Read("[agent-needs-input] x-y", Agt2736Message) },
            Now)!);
        Assert.Contains("Which deployment strategy should I implement?", stated[0]);

        var unstated = ParkedOpenItems.Items(
            blockerType: ParkedBlockerCatalog.OperatorDecision, question: null, reason: null);
        Assert.Contains(ParkedOpenItems.UnstatedQuestion, unstated[0]);
    }

    /// <summary>
    /// The escalation stub is one of the two places a summary is PRODUCED. It
    /// used to carry no open-items section at all, which reads as "nothing is
    /// open" on a card the runtime just parked for a human.
    /// </summary>
    [Fact]
    public void BuildStatusStub_NeverPresentsAParkedCardAsHavingNothingOpen()
    {
        var stub = AgentStudio.Runner.HumanReviewEscalation.BuildStatusStub(
            HumanReviewEscalationCategories.AgentNeedsInput,
            "The remote agent requires operator input: choose-connector-vs-lan-deployment-strategy");

        Assert.Contains(ParkedOpenItems.Heading, stub);
        var items = OpenItemsOf(stub);
        Assert.NotEmpty(items);
        Assert.DoesNotContain(items, IsNone);
        Assert.Contains(items, item => item.Contains("choose-connector-vs-lan-deployment-strategy"));

        // The board lifts exactly these two lines back out; the new section must
        // not introduce a second one of either shape.
        Assert.Equal(1, CountLinesStartingWith(stub, "- Category:"));
        Assert.Equal(1, CountLinesStartingWith(stub, "- Reason:"));
    }

    // -- Helpers -----------------------------------------------------------

    private static IEnumerable<ParkedBlockerStatus> ParkedCases()
    {
        yield return ParkedBlockerMarker.ToStatus(Record(Now.AddDays(-3)), Now)!;
        yield return ParkedBlockerMarker.ToStatus(
            ParkedBlockerCatalog.Build(TaskStates.HumanReview, reason: null, parkedAt: Now), Now)!;
        yield return ParkedBlockerMarker.ToStatus(
            ParkedBlockerCatalog.Build(
                TaskStates.Escalated, "[review-subject-unmaterialisierbar] baseline unavailable", Now),
            Now)!;
    }

    private static ParkedBlockerRecord Record(DateTime parkedAt) => new()
    {
        BlockerType = HumanReviewEscalationCategories.AgentNeedsInput,
        Condition = ParkedBlockerCatalog.ConditionFor(HumanReviewEscalationCategories.AgentNeedsInput),
        Lane = TaskStates.Escalated,
        ParkedAt = parkedAt,
        Reason = "[agent-needs-input] The remote agent requires operator input: choose-connector-vs-lan",
    };

    private static bool IsNone(string item)
        => item.TrimEnd('.').Trim().Equals("None", StringComparison.OrdinalIgnoreCase)
            || item.Contains("None recorded", StringComparison.OrdinalIgnoreCase);

    /// <summary>Checklist rows under the <c>## Open Items</c> heading.</summary>
    private static IReadOnlyList<string> OpenItemsOf(string markdown)
    {
        var items = new List<string>();
        var inSection = false;
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                inSection = line.Equals(ParkedOpenItems.Heading, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inSection || line.Length == 0) continue;
            items.Add(line.TrimStart('-', ' ').Replace("[ ]", "").Trim());
        }
        return items;
    }

    private static int CountLinesStartingWith(string markdown, string prefix)
        => markdown.Replace("\r\n", "\n").Split('\n')
            .Count(line => line.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
