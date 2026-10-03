using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Dossier decision-cards, apply step and creation paths: the two apply
/// outcomes (linked implementation card, cards from the chosen option), the
/// decision cards raised by a blocked card and by Dossier promotion, and the
/// overdue reminder. Runner and failure-intervention wiring are covered in
/// <see cref="ReviewDecisionOrchestratorTests"/> and <see cref="FailureInterventionTests"/>.
/// </summary>
public sealed class DecisionCardApplyTests : IDisposable
{
    private const string Project = "demo";
    private readonly string _workspace;
    private readonly string _watchPath;

    public DecisionCardApplyTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "decision-apply-tests-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", Project);
        Directory.CreateDirectory(_watchPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    private static DecisionContent LockFileDecision(params string[] appliesTo) => new()
    {
        Question = "Ship the Stable release with a lock file?",
        Options =
        [
            new DecisionOption { Id = "a", Label = "Lock file", Consequences = "Reproducible installs" },
            new DecisionOption
            {
                Id = "b", Label = "No lock file", Consequences = "Identity without a lock file",
                Requirements =
                [
                    new ConceptImplementationTask
                    {
                        Title = "Release identity without lock file",
                        PromptMarkdown = "Derive the release identity from the manifest instead of the lock file.",
                    },
                    new ConceptImplementationTask
                    {
                        Title = "Drop the lock file from the release gate",
                        PromptMarkdown = "Remove the lock file check from the Stable release preflight.",
                    },
                ],
            },
        ],
        RecommendedOptionId = "a",
        RecommendationReason = "Reproducibility outweighs churn.",
        AppliesTo = [.. appliesTo],
    };

    // ---- pure apply policy ----

    [Theory]
    [InlineData(TaskStates.Backlog, DecisionLinkedCardAction.AppendAndMove)]
    [InlineData(TaskStates.Preparation, DecisionLinkedCardAction.AppendAndMove)]
    [InlineData(TaskStates.OrchestratorPrep, DecisionLinkedCardAction.AppendAndMove)]
    [InlineData(TaskStates.Escalated, DecisionLinkedCardAction.AppendAndMove)]
    [InlineData(TaskStates.Ready, DecisionLinkedCardAction.AppendOnly)]
    [InlineData(TaskStates.Progress, DecisionLinkedCardAction.Skip)]
    [InlineData(TaskStates.AutoReview, DecisionLinkedCardAction.Skip)]
    [InlineData(TaskStates.Completed, DecisionLinkedCardAction.Skip)]
    [InlineData(null, DecisionLinkedCardAction.Missing)]
    public void ApplyPolicy_LinkedCard_ActionFollowsItsLane(string? state, DecisionLinkedCardAction expected)
    {
        var decided = LockFileDecision("AGT-9") with { Status = DecisionStatuses.Decided, ChosenOptionId = "b" };

        var plan = DecisionApplyPolicy.Plan(decided, [new DecisionLinkedCardFact("AGT-9", state)]);

        Assert.Equal(DecisionApplyOutcomes.LinkedCards, plan.Outcome);
        Assert.Equal(expected, Assert.Single(plan.LinkedCards).Action);
        Assert.Empty(plan.Requirements);
    }

    [Theory]
    [InlineData("b", DecisionApplyOutcomes.CreatedCards, 2)]
    [InlineData("a", DecisionApplyOutcomes.Nothing, 0)]
    public void ApplyPolicy_NoLinkedCard_CreatesFromChosenRequirements(string chosen, string outcome, int count)
    {
        var decided = LockFileDecision() with { Status = DecisionStatuses.Decided, ChosenOptionId = chosen };

        var plan = DecisionApplyPolicy.Plan(decided, []);

        Assert.Equal(outcome, plan.Outcome);
        Assert.Equal(count, plan.Requirements.Count);
    }

    [Fact]
    public void PromptBlock_CarriesQuestionChoiceRationaleAndRecord_AndAppendsOnce()
    {
        var at = new DateTime(2026, 9, 29, 10, 0, 0, DateTimeKind.Utc);
        var decided = LockFileDecision() with
        {
            Status = DecisionStatuses.Decided, ChosenOptionId = "b", Rationale = "Fewer moving parts.",
            DecidedBy = "alice", DecidedAt = at, RecordPath = "operations/decisions/AGT-1.md",
            History = [new DecisionHistoryEntry(DecisionStatuses.Decided, "b", "Fewer moving parts.", "alice", at, null)],
        };

        var block = DecisionPromptBlock.Render("AGT-1", "Stable release contract", decided);
        var marker = DecisionPromptBlock.Marker("AGT-1", decided);
        var once = DecisionPromptBlock.Append("# Work\n\nDo it.\n", block, marker, "AGT-1");
        var twice = DecisionPromptBlock.Append(once, block, marker, "AGT-1");

        Assert.Contains("- Question: Ship the Stable release with a lock file?", block);
        Assert.Contains("- Chosen option: b · No lock file", block);
        Assert.Contains("- Rationale: Fewer moving parts.", block);
        Assert.Contains("project wiki `operations/decisions/AGT-1.md`", block);
        Assert.StartsWith("# Work", once);
        Assert.Equal(once, twice);
        Assert.Equal(1, CountOf(twice, marker));
    }

    [Fact]
    public void PromptBlock_RechoiceAtSameInstant_ReplacesPreviousChoice()
    {
        var at = new DateTime(2026, 10, 3, 1, 0, 0, DateTimeKind.Utc);
        var firstEntry = new DecisionHistoryEntry(DecisionStatuses.Decided, "a", "Use the lock file.", "alice", at, null);
        var first = LockFileDecision() with
        {
            Status = DecisionStatuses.Decided, ChosenOptionId = "a", Rationale = firstEntry.Rationale,
            DecidedAt = at, History = [firstEntry],
        };
        var second = first with
        {
            ChosenOptionId = "b", Rationale = "Use the manifest.",
            History = [firstEntry,
                new DecisionHistoryEntry(DecisionStatuses.Reopened, null, null, "alice", at, "Revisit"),
                new DecisionHistoryEntry(DecisionStatuses.Decided, "b", "Use the manifest.", "alice", at, null)],
        };
        var firstMarker = DecisionPromptBlock.Marker("AGT-1", first);
        var secondMarker = DecisionPromptBlock.Marker("AGT-1", second);
        Assert.NotEqual(firstMarker, secondMarker);

        var original = DecisionPromptBlock.Append("# Work\n\nDo it.\n",
            DecisionPromptBlock.Render("AGT-1", "Release contract", first), firstMarker, "AGT-1");
        var updated = DecisionPromptBlock.Append(original,
            DecisionPromptBlock.Render("AGT-1", "Release contract", second), secondMarker, "AGT-1");

        Assert.StartsWith("# Work\n\nDo it.", updated);
        Assert.DoesNotContain(firstMarker, updated);
        Assert.DoesNotContain("- Chosen option: a · Lock file", updated);
        Assert.Contains("- Chosen option: b · No lock file", updated);
        Assert.Contains("- Rationale: Use the manifest.", updated);
        Assert.Equal(1, CountOf(updated, "## Decision AGT-1:"));
        Assert.Equal(updated, DecisionPromptBlock.Append(updated,
            DecisionPromptBlock.Render("AGT-1", "Release contract", second), secondMarker, "AGT-1"));

        var legacy = original.Replace(firstMarker,
            "<!-- agent-studio:decision-apply AGT-1 2026-10-03T01:00:00Z -->");
        var upgraded = DecisionPromptBlock.Append(legacy,
            DecisionPromptBlock.Render("AGT-1", "Release contract", second), secondMarker, "AGT-1");
        Assert.DoesNotContain("- Chosen option: a · Lock file", upgraded);
        Assert.Equal(1, CountOf(upgraded, "## Decision AGT-1:"));
    }

    // ---- apply on decide: linked implementation card ----

    [Fact]
    public async Task Decide_WithLinkedCard_AppendsDecisionBlock_MovesItToReady_AndRecordLinksIt()
    {
        var h = Build();
        var implId = h.Mutations.CreateJob(new CreateTaskRequest
        {
            Title = "Stable release gate", WatchPath = _watchPath, TargetState = TaskStates.Preparation,
            PromptMarkdown = "# Stable release gate\n\nImplement the release contract.\n",
        })!;
        var impl = h.Scanner.FindJob(implId, _watchPath)!;
        var decisionId = h.Mutations.CreateJob(new CreateTaskRequest
        {
            Title = "Stable release contract", WatchPath = _watchPath, Kind = TaskKinds.Decision,
            Decision = LockFileDecision(impl.Key!),
        })!;
        var decisionKey = h.Scanner.FindJob(decisionId, _watchPath)!.Key!;
        h.Mutations.SetTaskReferences(implId,
            new TaskReferences { DependsOn = [new TaskDependencyReference(decisionKey)] }, _watchPath);
        Assert.Equal(MoveJobStatus.Failure, h.States.MoveJob(implId, TaskStates.Ready, _watchPath).Status);

        var outcome = await h.Decisions.DecideAsync(decisionId, _watchPath,
            new DecideCardRequest { OptionId = "b", Rationale = "Fewer moving parts." }, "alice");

        Assert.Equal(DecisionCardStatus.Success, outcome.Status);
        var moved = h.Scanner.FindJob(implId, _watchPath)!;
        Assert.Equal(TaskStates.Ready, moved.State);
        var prompt = File.ReadAllText(Path.Combine(moved.FolderPath, "prompt.md"));
        Assert.StartsWith("# Stable release gate", prompt);
        Assert.Contains($"## Decision {decisionKey}: Stable release contract", prompt);
        Assert.Contains("- Question: Ship the Stable release with a lock file?", prompt);
        Assert.Contains("- Chosen option: b · No lock file", prompt);
        Assert.Contains("- Rationale: Fewer moving parts.", prompt);
        Assert.Contains($"operations/decisions/{decisionKey}.md", prompt);

        var decision = h.Scanner.FindJob(decisionId, _watchPath)!.Decision!;
        var entry = decision.History[^1];
        Assert.Equal(DecisionApplyOutcomes.LinkedCards, entry.ApplyOutcome);
        Assert.Equal([impl.Key!], entry.AppliedTaskKeys);
        var record = ReadRecord(decisionKey);
        Assert.Contains("- Applied: linked-cards", record);
        Assert.Contains($"- Applied to: {impl.Key}", record);
        Assert.Contains($"\"{impl.Key}\"", record); // receipt spawnedTaskKeys
        Assert.Contains(h.ActivityFeed.Read(_watchPath),
            e => e.Summary == $"Decision applied: {decisionKey} updated {impl.Key}");
        Assert.Contains(new TimelineLog(NullLogger<TimelineLog>.Instance)
            .ReadAll(h.Scanner.FindJob(decisionId, _watchPath)!.FolderPath),
            e => e.Kind == TimelineEventKinds.DecisionApplied);
    }

    [Fact]
    public async Task Reopen_ThenChooseAgain_UpdatesLinkedCardPromptToCurrentChoice()
    {
        var h = Build();
        var implId = h.Mutations.CreateJob(new CreateTaskRequest
        {
            Title = "Stable release gate", WatchPath = _watchPath, TargetState = TaskStates.Preparation,
            PromptMarkdown = "# Stable release gate\n\nImplement the release contract.\n",
        })!;
        var impl = h.Scanner.FindJob(implId, _watchPath)!;
        var decisionId = h.Mutations.CreateJob(new CreateTaskRequest
        {
            Title = "Stable release contract", WatchPath = _watchPath, Kind = TaskKinds.Decision,
            Decision = LockFileDecision(impl.Key!),
        })!;

        Assert.Equal(DecisionCardStatus.Success, (await h.Decisions.DecideAsync(decisionId, _watchPath,
            new DecideCardRequest { OptionId = "a", Rationale = "Keep installs reproducible." }, "alice")).Status);
        Assert.Equal(DecisionCardStatus.Success, (await h.Decisions.ReopenAsync(decisionId, _watchPath,
            new ReopenDecisionRequest { Note = "The release contract changed." }, "alice")).Status);
        Assert.Equal(DecisionCardStatus.Success, (await h.Decisions.DecideAsync(decisionId, _watchPath,
            new DecideCardRequest { OptionId = "b", Rationale = "Use the manifest instead." }, "alice")).Status);

        var prompt = File.ReadAllText(Path.Combine(impl.FolderPath, "prompt.md"));
        Assert.DoesNotContain("- Chosen option: a · Lock file", prompt);
        Assert.Contains("- Chosen option: b · No lock file", prompt);
        Assert.Contains("- Rationale: Use the manifest instead.", prompt);
        Assert.Equal(1, CountOf(prompt, $"## Decision {h.Scanner.FindJob(decisionId, _watchPath)!.Key}:"));
        Assert.Equal(TaskStates.Ready, h.Scanner.FindJob(implId, _watchPath)!.State);
    }

    [Fact]
    public async Task Decide_WithLinkedCardAlreadyRunning_LeavesItUntouched_AndSaysSo()
    {
        var h = Build();
        var implId = h.Mutations.CreateJob(new CreateTaskRequest
        {
            Title = "Already running", WatchPath = _watchPath, TargetState = TaskStates.Progress,
            PromptMarkdown = "Original prompt.\n",
        })!;
        var impl = h.Scanner.FindJob(implId, _watchPath)!;
        var decisionId = h.Mutations.CreateJob(new CreateTaskRequest
        {
            Title = "Late decision", WatchPath = _watchPath, Kind = TaskKinds.Decision,
            Decision = LockFileDecision(impl.Key!),
        })!;

        await h.Decisions.DecideAsync(decisionId, _watchPath, new DecideCardRequest { OptionId = "a" }, "alice");

        var after = h.Scanner.FindJob(implId, _watchPath)!;
        Assert.Equal(TaskStates.Progress, after.State);
        Assert.Equal("Original prompt.\n", File.ReadAllText(Path.Combine(after.FolderPath, "prompt.md")));
        Assert.Equal(DecisionApplyOutcomes.Failed,
            h.Scanner.FindJob(decisionId, _watchPath)!.Decision!.History[^1].ApplyOutcome);
        Assert.Contains(h.ActivityFeed.Read(_watchPath), e =>
            e.Kind == OrchestratorLogKinds.Alert && e.Reasoning!.Contains("already in 3-progress"));
    }

    // ---- apply on decide: cards from the chosen option ----

    [Fact]
    public async Task Decide_WithoutLinkedCard_CreatesReadyCardsFromChosenOption_ThroughPromotionLedger()
    {
        var h = Build();
        var decisionId = h.Mutations.CreateJob(new CreateTaskRequest
        {
            Title = "Stable release contract", WatchPath = _watchPath, Kind = TaskKinds.Decision,
            Decision = LockFileDecision(),
        })!;
        var decisionCard = h.Scanner.FindJob(decisionId, _watchPath)!;

        var outcome = await h.Decisions.DecideAsync(decisionId, _watchPath,
            new DecideCardRequest { OptionId = "b", Rationale = "Fewer moving parts." }, "alice");

        Assert.Equal(DecisionCardStatus.Success, outcome.Status);
        var entry = h.Scanner.FindJob(decisionId, _watchPath)!.Decision!.History[^1];
        Assert.Equal(DecisionApplyOutcomes.CreatedCards, entry.ApplyOutcome);
        Assert.Equal(2, entry.AppliedTaskKeys.Count);
        var created = entry.AppliedTaskKeys.Select(key => h.Scanner.FindJob(key, _watchPath)!).ToList();
        Assert.Equal(["Release identity without lock file", "Drop the lock file from the release gate"],
            created.Select(card => card.Title));
        Assert.All(created, card =>
        {
            Assert.Equal(TaskStates.Ready, card.State);
            Assert.Equal(TaskKinds.Task, card.Kind);
            Assert.Equal(TaskAcceptanceDeliveryModes.BoundedSlice, card.AcceptanceScope!.DeliveryMode);
            Assert.Contains(decisionCard.Key!, card.References.RelatedTo);
            var prompt = File.ReadAllText(Path.Combine(card.FolderPath, "prompt.md"));
            Assert.Contains($"## Decision {decisionCard.Key}", prompt);
            Assert.Contains("- Chosen option: b · No lock file", prompt);
        });
        Assert.Contains("Derive the release identity",
            File.ReadAllText(Path.Combine(created[0].FolderPath, "prompt.md")));
        var ledger = AgentStudio.Pipeline.SpawnedTaskLedger.Read(
            h.Scanner.FindJob(decisionId, _watchPath)!.FolderPath);
        Assert.Equal(2, ledger.Count);
        Assert.All(ledger, row => Assert.StartsWith($"decision-apply:{decisionCard.Key}:", row.Reason));
        var record = ReadRecord(decisionCard.Key!);
        Assert.Contains("- Applied: created-cards", record);
        Assert.Contains($"- Applied to: {string.Join(", ", entry.AppliedTaskKeys)}", record);
    }

    [Fact]
    public void ApplyPolicy_NoResolvableLink_FallsBackToRequirements()
    {
        // The service passes the linked cards without the decision's own key.
        var decided = LockFileDecision("AGT-1") with { Status = DecisionStatuses.Decided, ChosenOptionId = "b" };

        var plan = DecisionApplyPolicy.Plan(decided, []);

        Assert.Equal(DecisionApplyOutcomes.CreatedCards, plan.Outcome);
        Assert.Equal(2, plan.Requirements.Count);
    }

    [Fact]
    public async Task Decide_AppliesToNamingOnlyTheDecisionItself_CreatesCardsFromTheChosenOption()
    {
        var h = Build();
        var decisionId = h.Mutations.CreateJob(new CreateTaskRequest
        {
            Title = "Stable release contract", WatchPath = _watchPath, Kind = TaskKinds.Decision,
            Decision = LockFileDecision(),
        })!;
        var decisionCard = h.Scanner.FindJob(decisionId, _watchPath)!;
        Assert.True(h.Mutations.SetDecisionContent(decisionId,
            decisionCard.Decision! with { AppliesTo = [decisionCard.Key!, " "] }, _watchPath));

        await h.Decisions.DecideAsync(decisionId, _watchPath, new DecideCardRequest { OptionId = "b" }, "alice");

        var entry = h.Scanner.FindJob(decisionId, _watchPath)!.Decision!.History[^1];
        Assert.Equal(DecisionApplyOutcomes.CreatedCards, entry.ApplyOutcome);
        Assert.Equal(2, entry.AppliedTaskKeys.Count);
    }

    [Fact]
    public async Task Decide_OptionWithoutRequirements_RecordsNothingToApply()
    {
        var h = Build();
        var decisionId = h.Mutations.CreateJob(new CreateTaskRequest
        {
            Title = "Stable release contract", WatchPath = _watchPath, Kind = TaskKinds.Decision,
            Decision = LockFileDecision(),
        })!;
        var before = h.Scanner.ScanAllJobs().Count;

        await h.Decisions.DecideAsync(decisionId, _watchPath, new DecideCardRequest { OptionId = "a" }, "alice");

        Assert.Equal(before, h.Scanner.ScanAllJobs().Count);
        Assert.Equal(DecisionApplyOutcomes.Nothing,
            h.Scanner.FindJob(decisionId, _watchPath)!.Decision!.History[^1].ApplyOutcome);
    }

    [Fact]
    public void CreateDecision_RejectsRequirementWithoutPrompt()
    {
        var content = LockFileDecision() with
        {
            Options =
            [
                new DecisionOption { Id = "a", Label = "A" },
                new DecisionOption { Id = "b", Label = "B", Requirements = [new ConceptImplementationTask { Title = "No prompt" }] },
            ],
            RecommendedOptionId = null, RecommendationReason = null,
        };
        Assert.Contains(DecisionCardPolicy.ValidateContent(content),
            error => error.Code == DecisionCardErrorCode.RequirementShape);
    }

    // ---- creation path shared by the runner and failure intervention ----

    [Fact]
    public async Task Request_ForBlockedCard_LinksDependant_BlocksClaim_AndDecideReturnsItToReady()
    {
        var h = Build();
        var originId = h.Mutations.CreateJob(new CreateTaskRequest
        {
            Title = "Blocked release card", WatchPath = _watchPath, TargetState = TaskStates.Escalated,
            PromptMarkdown = "Ship the release.\n",
        })!;
        var origin = h.Scanner.FindJob(originId, _watchPath)!;

        var first = h.Requests.Request(new DecisionCardRequest
        {
            Title = "Decision: Blocked release card", WatchPath = _watchPath,
            Content = LockFileDecision(), BlockedCard = origin,
        })!;
        var again = h.Requests.Request(new DecisionCardRequest
        {
            Title = "Decision: Blocked release card", WatchPath = _watchPath,
            Content = LockFileDecision(), BlockedCard = origin,
        })!;

        Assert.True(first.Created);
        Assert.False(again.Created);
        Assert.Equal(first.Key, again.Key);
        var decision = h.Scanner.FindJob(first.JobId, _watchPath)!;
        Assert.Equal(TaskKinds.Decision, decision.Kind);
        Assert.Equal(TaskStates.Preparation, decision.State);
        Assert.Equal(TimelineActors.Orchestrator, decision.CreationSource);
        Assert.Equal([origin.Key!], decision.Decision!.Dependants);
        Assert.Equal([origin.Key!], decision.Decision.AppliesTo);
        var waiting = h.Scanner.FindJob(originId, _watchPath)!;
        Assert.Contains(waiting.References.DependsOn, edge => edge.Key == first.Key);
        var refused = h.States.MoveJob(originId, TaskStates.Ready, _watchPath);
        Assert.Equal(MoveJobStatus.Failure, refused.Status);
        Assert.Contains($"blocked by pending decision {first.Key}", refused.Message);

        await h.Decisions.DecideAsync(first.JobId, _watchPath, new DecideCardRequest { OptionId = "a" }, "operator");

        var resumed = h.Scanner.FindJob(originId, _watchPath)!;
        Assert.Equal(TaskStates.Ready, resumed.State);
        Assert.Contains("- Chosen option: a · Lock file", File.ReadAllText(Path.Combine(resumed.FolderPath, "prompt.md")));
    }

    // ---- creation path: Dossier promotion ----

    [Fact]
    public void DossierPromotion_DecisionItem_CreatesDecisionCardWithOptions_Idempotently()
    {
        var h = Build();
        var sourceId = h.Mutations.CreateJob(new CreateTaskRequest
        {
            Title = "Release concept", WatchPath = _watchPath, Mode = TaskModes.Concept,
        })!;
        var source = h.Scanner.FindJob(sourceId, _watchPath)!;
        var plan = new PromoteConceptResponse
        {
            Source = new ConceptSourceDocument { RepoRelativePath = "docs/release/index.html", Title = "Release" },
            Items =
            [
                new ConceptImplementationTask { Title = "Implement release API", PromptMarkdown = "Add the release endpoint." },
                new ConceptImplementationTask
                {
                    Title = "Lock file or not", PromptMarkdown = "Surfaced in §4.", Decision = LockFileDecision(),
                },
            ],
        };

        var first = h.Promotion.Promote(source, plan, new PromoteConceptRequest());
        var repeated = h.Promotion.Promote(source, plan, new PromoteConceptRequest());

        Assert.Equal(first.Created.Select(c => c.JobId), repeated.Created.Select(c => c.JobId));
        var coding = h.Scanner.FindJob(first.Created[0].JobId, _watchPath)!;
        Assert.Equal(TaskKinds.Task, coding.Kind);
        var decision = h.Scanner.FindJob(first.Created[1].JobId, _watchPath)!;
        Assert.Equal(TaskKinds.Decision, decision.Kind);
        Assert.Equal(TaskStates.Preparation, decision.State);
        Assert.Equal("Ship the Stable release with a lock file?", decision.Decision!.Question);
        Assert.Equal(["a", "b"], decision.Decision.Options.Select(o => o.Id));
        Assert.Equal(2, decision.Decision.Options[1].Requirements.Count);
        Assert.Contains(source.Key!, decision.References.RelatedTo);
        Assert.Contains("docs/release/index.html", File.ReadAllText(Path.Combine(decision.FolderPath, "prompt.md")));
    }

    // ---- reminder ----

    [Fact]
    public void ReminderPolicy_DefaultsToThreeDays_HonoursDueDate_AndRemindsOncePerCycle()
    {
        var requested = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        var pending = LockFileDecision();

        Assert.Equal(requested.AddDays(3), DecisionReminderPolicy.DueAt(pending, requested));
        Assert.False(DecisionReminderPolicy.IsDue(pending, requested, requested.AddDays(3).AddMinutes(-1)));
        Assert.True(DecisionReminderPolicy.IsDue(pending, requested, requested.AddDays(3)));

        var explicitDue = pending with { DueDate = requested.AddDays(1) };
        Assert.True(DecisionReminderPolicy.IsDue(explicitDue, requested, requested.AddDays(1)));
        Assert.False(DecisionReminderPolicy.IsDue(explicitDue with { RemindedAt = requested.AddDays(1) },
            requested, requested.AddDays(5)));
        Assert.False(DecisionReminderPolicy.IsDue(pending with { Status = DecisionStatuses.Decided },
            requested, requested.AddDays(9)));

        var reopenedAt = requested.AddDays(10);
        var reopened = DecisionCardPolicy.Reopen(explicitDue with
        {
            Status = DecisionStatuses.Decided, RemindedAt = requested.AddDays(1),
            History = [new DecisionHistoryEntry(DecisionStatuses.Reopened, null, null, "bob", reopenedAt, "Revisit")],
        }, "Revisit");
        Assert.Null(reopened.RemindedAt);
        Assert.Equal(reopenedAt.AddDays(3), DecisionReminderPolicy.DueAt(reopened, requested));
    }

    [Fact]
    public void ReminderPolicy_BlockedCards_NamesDeclaredAndDependingCards_ButNotFinishedOnes()
    {
        var content = LockFileDecision("AGT-2") with { Dependants = ["AGT-3", "AGT-4"] };
        var blocked = DecisionReminderPolicy.BlockedCards(content,
        [
            new DecisionWaitingCard("AGT-2", TaskStates.Escalated, false),
            new DecisionWaitingCard("AGT-4", TaskStates.Completed, true),
            new DecisionWaitingCard("AGT-5", TaskStates.Preparation, true),
            new DecisionWaitingCard("AGT-6", TaskStates.Preparation, false),
        ]);
        Assert.Equal(["AGT-3", "AGT-2", "AGT-5"], blocked);
    }

    [Fact]
    public void ReminderSweep_OverdueDecision_PostsInboxEntryAndFeedLine_NamingBlockedCards_Once()
    {
        var h = Build();
        var implId = h.Mutations.CreateJob(new CreateTaskRequest
        {
            Title = "Stable release gate", WatchPath = _watchPath, TargetState = TaskStates.Preparation,
        })!;
        var impl = h.Scanner.FindJob(implId, _watchPath)!;
        var decisionId = h.Mutations.CreateJob(new CreateTaskRequest
        {
            Title = "Stable release contract", WatchPath = _watchPath, Kind = TaskKinds.Decision,
            Decision = LockFileDecision() with { Decider = "role:owner" },
        })!;
        var decision = h.Scanner.FindJob(decisionId, _watchPath)!;
        h.Mutations.SetTaskReferences(implId,
            new TaskReferences { DependsOn = [new TaskDependencyReference(decision.Key!)] }, _watchPath);

        var clock = new FakeTimeProvider(new DateTimeOffset(decision.CreatedAt.ToUniversalTime().AddDays(2)));
        var sweep = h.Reminders(clock);
        Assert.Empty(sweep.Sweep());

        clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        var reminder = Assert.Single(sweep.Sweep());
        Assert.Equal(decision.Key, reminder.Key);
        Assert.Equal([impl.Key!], reminder.BlockedCards);

        var feed = Assert.Single(h.ActivityFeed.Read(_watchPath), e => e.Summary.StartsWith("Decision overdue:"));
        Assert.Equal(OrchestratorLogKinds.Alert, feed.Kind);
        Assert.Equal(OrchestratorLogTopics.DecisionCard, feed.Topic);
        Assert.Contains($"{decision.Key} waits on role:owner", feed.Summary);
        Assert.EndsWith($"blocks {impl.Key}", feed.Summary);

        var inbox = ReadRecord(decision.Key!);
        Assert.StartsWith("---\nlifecycleSchema: wiki-page-lifecycle/v1\npageKind: decision\nlifecycleState: review-requested\n", inbox);
        Assert.Contains("lifecycleHistory:\n  - state: review-requested\n    editedBy: orchestrator\n", inbox);
        Assert.Contains($"- Blocked cards: {impl.Key}", inbox);
        Assert.Contains("## Question", inbox);

        var stamped = h.Scanner.FindJob(decisionId, _watchPath)!;
        Assert.NotNull(stamped.Decision!.RemindedAt);
        Assert.Equal(TaskStates.Preparation, stamped.State);
        Assert.Contains(new TimelineLog(NullLogger<TimelineLog>.Instance).ReadAll(stamped.FolderPath),
            e => e.Kind == TimelineEventKinds.DecisionReminded && e.Details!["blockedCards"] == impl.Key);

        clock.Advance(TimeSpan.FromDays(2));
        Assert.Empty(sweep.Sweep());
        Assert.Single(h.ActivityFeed.Read(_watchPath), e => e.Summary.StartsWith("Decision overdue:"));
    }

    [Fact]
    public async Task ReminderSweep_DecidedRecordLeavesTheInbox()
    {
        var h = Build();
        var decisionId = h.Mutations.CreateJob(new CreateTaskRequest
        {
            Title = "Stable release contract", WatchPath = _watchPath, Kind = TaskKinds.Decision,
            Decision = LockFileDecision(),
        })!;
        var decision = h.Scanner.FindJob(decisionId, _watchPath)!;
        var clock = new FakeTimeProvider(new DateTimeOffset(decision.CreatedAt.ToUniversalTime().AddDays(4)));
        Assert.Single(h.Reminders(clock).Sweep());

        await h.Decisions.DecideAsync(decisionId, _watchPath, new DecideCardRequest { OptionId = "a" }, "alice");

        var record = ReadRecord(decision.Key!);
        Assert.StartsWith("# Decision", record);
        Assert.DoesNotContain("lifecycleState", record);
        Assert.Empty(h.Reminders(clock).Sweep());
    }

    [Fact]
    public async Task ReminderSweep_StaleScan_DoesNotOverwriteADecisionTakenAfterTheScan()
    {
        var h = Build();
        var decisionId = h.Mutations.CreateJob(new CreateTaskRequest
        {
            Title = "Stable release contract", WatchPath = _watchPath, Kind = TaskKinds.Decision,
            Decision = LockFileDecision(),
        })!;
        var decision = h.Scanner.FindJob(decisionId, _watchPath)!;
        var now = decision.CreatedAt.ToUniversalTime().AddDays(4);
        var sweep = h.Reminders(new FakeTimeProvider(new DateTimeOffset(now)));

        // The sweep scanned the card while it was pending and overdue ...
        var scan = h.Scanner.ScanAllAutomationJobs();
        var scanned = scan.Single(card => card.Id == decisionId);
        Assert.True(DecisionReminderPolicy.IsDue(scanned.Decision!, scanned.CreatedAt, now));
        // ... and the operator decides before the sweep reaches it.
        await h.Decisions.DecideAsync(decisionId, _watchPath,
            new DecideCardRequest { OptionId = "a", Rationale = "Reproducible." }, "alice");

        Assert.Null(sweep.RemindIfStillDue(scanned, scan, now));

        var stored = h.Scanner.FindJob(decisionId, _watchPath)!;
        Assert.Equal(TaskStates.Completed, stored.State);
        Assert.Equal(DecisionStatuses.Decided, stored.Decision!.Status);
        Assert.Equal("a", stored.Decision.ChosenOptionId);
        Assert.Equal("Reproducible.", stored.Decision.Rationale);
        Assert.Null(stored.Decision.RemindedAt);
        Assert.DoesNotContain("lifecycleState", ReadRecord(decision.Key!));
        Assert.DoesNotContain(h.ActivityFeed.Read(_watchPath), e => e.Summary.StartsWith("Decision overdue:"));
    }

    [Fact]
    public async Task ReminderSweep_WaitsForAnInFlightDecide_ThenSeesTheDecision()
    {
        var h = Build();
        var decisionId = h.Mutations.CreateJob(new CreateTaskRequest
        {
            Title = "Stable release contract", WatchPath = _watchPath, Kind = TaskKinds.Decision,
            Decision = LockFileDecision(),
        })!;
        var decision = h.Scanner.FindJob(decisionId, _watchPath)!;
        var now = decision.CreatedAt.ToUniversalTime().AddDays(4);
        var sweep = h.Reminders(new FakeTimeProvider(new DateTimeOffset(now)));
        var scan = h.Scanner.ScanAllAutomationJobs();
        var scanned = scan.Single(card => card.Id == decisionId);

        // Hold the gate as a decide in flight would; the reminder must wait for it.
        await DecisionCardService.WriteGate.WaitAsync();
        var reminder = Task.Run(() => sweep.RemindIfStillDue(scanned, scan, now));
        await Task.Delay(100);
        Assert.False(reminder.IsCompleted);
        DecisionCardService.WriteGate.Release();
        Assert.NotNull(await reminder);

        await h.Decisions.DecideAsync(decisionId, _watchPath, new DecideCardRequest { OptionId = "a" }, "alice");
        Assert.Equal(DecisionStatuses.Decided, h.Scanner.FindJob(decisionId, _watchPath)!.Decision!.Status);
    }

    // ---- harness ----

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private string ReadRecord(string key) => File.ReadAllText(Path.Combine(
        _watchPath, "docs", "operations", "decisions", key + ".md")).Replace("\r\n", "\n");

    private sealed record Harness(
        TaskScannerService Scanner,
        TaskMutationService Mutations,
        DecisionCardService Decisions,
        DecisionCardRequests Requests,
        AgentStudio.Pipeline.ConceptPromotionService Promotion,
        TaskStateMachine States,
        OrchestratorLog ActivityFeed,
        Func<TimeProvider, DecisionReminderSweep> Reminders);

    private Harness Build()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        states.EnsureStateFoldersAndMigrate();
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        var activityFeed = new OrchestratorLog(NullLogger<OrchestratorLog>.Instance);
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            registry,
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance,
            timeline, activityFeed: activityFeed);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var transitions = new TaskTransitionService(
            scanner, states, mutations,
            new GitService(NullLogger<GitService>.Instance, scanner, config, prompts),
            new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config),
            NullLogger<TaskTransitionService>.Instance);
        var docs = new ProjectDocsService(scanner, registry, NullLogger<ProjectDocsService>.Instance);
        var records = new DecisionRecordService(docs);
        var promotion = new AgentStudio.Pipeline.ConceptPromotionService(
            scanner, mutations, NullLogger<AgentStudio.Pipeline.ConceptPromotionService>.Instance);
        var apply = new DecisionApplyService(scanner, mutations, transitions, promotion,
            NullLogger<DecisionApplyService>.Instance);
        var decisions = new DecisionCardService(scanner, mutations, transitions, timeline,
            NullLogger<DecisionCardService>.Instance, records, activityFeed, apply);
        var requests = new DecisionCardRequests(scanner, mutations, NullLogger<DecisionCardRequests>.Instance);
        return new Harness(scanner, mutations, decisions, requests, promotion, states, activityFeed,
            clock => new DecisionReminderSweep(scanner, mutations, records, activityFeed, timeline,
                NullLogger<DecisionReminderSweep>.Instance, clock));
    }
}
