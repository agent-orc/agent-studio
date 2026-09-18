

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the pure aggregation that turns session-events.jsonl + cli-output.log
/// into the run timeline that drives the protocol-pane redesign. The
/// builder is the only piece that touches both data sources, so a regression
/// here breaks the entire run-list UI; pinning the matrix keeps the
/// drift-prone parts (line-spans, status pairing, user-followup capture)
/// honest.
/// </summary>
public class RunTimelineBuilderTests
{
    private static readonly DateTime T0 = new(2026, 5, 3, 10, 0, 0, DateTimeKind.Utc);

    private static CliOutputLine Sys(int sec, string text) =>
        new() { Timestamp = T0.AddSeconds(sec), Stream = "system", Text = text };

    private static CliOutputLine User(int sec, string text) =>
        new() { Timestamp = T0.AddSeconds(sec), Stream = "user", Text = text };

    private static CliOutputLine StdOut(int sec, string text) =>
        new() { Timestamp = T0.AddSeconds(sec), Stream = "stdout", Text = text };

    [Fact]
    public void EmptyInputs_ReturnEmptyTimeline()
    {
        var t = RunTimelineBuilder.Build(events: [], lines: [], nowUtc: T0);
        Assert.Equal(0, t.RunCount);
        Assert.Empty(t.Runs);
        Assert.Null(t.FirstStartedAt);
        Assert.Null(t.LastActivityAt);
        Assert.False(t.HasActiveRun);
    }

    [Fact]
    public void SingleCompletedRun_PairsEventWithStartedAndExitedMarkers()
    {
        var events = new List<SessionEvent>
        {
            new() { Ts = T0, Kind = "start", Cli = "claude", Resumed = false, CapturedSessionId = "uuid-1" }
        };
        var lines = new List<CliOutputLine>
        {
            Sys(0, "[taskboard] Started claude CLI (PID 1234)"),
            StdOut(2, "Hello"),
            Sys(60, "[taskboard] claude CLI exited: status=completed, exitCode=0, duration=58.4s")
        };

        var t = RunTimelineBuilder.Build(events, lines, T0.AddSeconds(120));

        Assert.Equal(1, t.RunCount);
        Assert.True(t.FirstStartedAt.HasValue);
        var r = Assert.Single(t.Runs);
        Assert.Equal(1, r.Index);
        Assert.Equal("start", r.Intent);
        Assert.Equal("completed", r.Status);
        Assert.Equal("claude", r.Cli);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(58.4, r.DurationSeconds);
        Assert.Equal("uuid-1", r.CapturedSessionId);
        Assert.Equal(1, r.LineStart);
        Assert.Equal(3, r.LineEnd);
        Assert.False(t.HasActiveRun);
    }

    [Fact]
    public void RunningRun_HasNullEndAndRunningStatus()
    {
        var events = new List<SessionEvent>
        {
            new() { Ts = T0, Kind = "continue", Cli = "claude", Resumed = true, InputSessionId = "uuid-1" }
        };
        var lines = new List<CliOutputLine>
        {
            Sys(0, "[taskboard] Started claude CLI (PID 1234)"),
            StdOut(2, "Working")
        };

        var t = RunTimelineBuilder.Build(events, lines, T0.AddSeconds(30));

        var r = Assert.Single(t.Runs);
        Assert.Equal("running", r.Status);
        Assert.Null(r.EndedAt);
        Assert.Null(r.ExitCode);
        Assert.True(t.HasActiveRun);
        Assert.Equal(1, r.LineStart);
        Assert.Equal(2, r.LineEnd);
    }

    [Fact]
    public void TwoRuns_UserFollowupCapturedFromBetweenLines()
    {
        var events = new List<SessionEvent>
        {
            new() { Ts = T0, Kind = "start", Cli = "claude" },
            new() { Ts = T0.AddSeconds(120), Kind = "continue", Cli = "claude", Resumed = true, InputSessionId = "uuid-1" }
        };
        var lines = new List<CliOutputLine>
        {
            Sys(0, "[taskboard] Started claude CLI (PID 1234)"),
            StdOut(10, "first run output"),
            Sys(50, "[taskboard] claude CLI exited: status=completed, exitCode=0, duration=50s"),
            User(110, "Keep going and add tests"),
            Sys(120, "[taskboard] Started claude CLI (PID 1235)"),
            StdOut(125, "ok"),
            Sys(180, "[taskboard] claude CLI exited: status=completed, exitCode=0, duration=60s")
        };

        var t = RunTimelineBuilder.Build(events, lines, T0.AddSeconds(200));

        Assert.Equal(2, t.RunCount);

        Assert.Null(t.Runs[0].UserFollowup); // fresh start - no preceding user line
        Assert.Equal("Keep going and add tests", t.Runs[1].UserFollowup);

        // Spans: run 1 covers lines 1..3, run 2 covers lines 5..7.
        Assert.Equal(1, t.Runs[0].LineStart);
        Assert.Equal(3, t.Runs[0].LineEnd);
        Assert.Equal(5, t.Runs[1].LineStart);
        Assert.Equal(7, t.Runs[1].LineEnd);
    }

    [Fact]
    public void FailedRun_PreservesExitCodeAndStatus()
    {
        var events = new List<SessionEvent>
        {
            new() { Ts = T0, Kind = "continue", Cli = "claude", Resumed = true, InputSessionId = "uuid-dead" }
        };
        var lines = new List<CliOutputLine>
        {
            Sys(0, "[taskboard] Started claude CLI (PID 1234)"),
            new() { Timestamp = T0.AddSeconds(1), Stream = "stderr", Text = "No conversation found with session ID: uuid-dead" },
            Sys(2, "[taskboard] claude CLI exited: status=failed, exitCode=1, duration=1.8s")
        };

        var t = RunTimelineBuilder.Build(events, lines, T0.AddSeconds(10));

        var r = Assert.Single(t.Runs);
        Assert.Equal("failed", r.Status);
        Assert.Equal(1, r.ExitCode);
        Assert.Equal("uuid-dead", r.InputSessionId);
        Assert.False(t.HasActiveRun);
    }

    [Fact]
    public void Trigger_provenance_is_projected_without_deriving_it_from_session_kind()
    {
        var events = new List<SessionEvent>
        {
            new()
            {
                Ts = T0,
                Kind = "start",
                Trigger = RunTriggers.ReviewConcern,
                TriggeredBy = "pipeline",
                TriggerReason = "Code quality concern requires a fix.",
                TriggerSource = "review=review_01;aspects=code-quality",
            },
        };

        var run = Assert.Single(RunTimelineBuilder.Build(events, [], T0.AddSeconds(1)).Runs);
        Assert.Equal("start", run.Intent);
        Assert.Equal(RunTriggers.ReviewConcern, run.Trigger);
        Assert.Equal("pipeline", run.TriggeredBy);
        Assert.Equal("Code quality concern requires a fix.", run.TriggerReason);
        Assert.Equal("review=review_01;aspects=code-quality", run.TriggerSource);
    }

    [Fact]
    public void LegacyRemoteRun_DerivesTerminalResultAndDurationFromAttemptAuthority()
    {
        var events = new List<SessionEvent>
        {
            new()
            {
                Ts = T0,
                Kind = "start",
                Cli = "remote-runner",
                RunAttemptId = "run-54"
            }
        };
        var attempts = new List<RunAttemptDto>
        {
            new(
                "run-54",
                "QS-54",
                "PROJ-016",
                null,
                AttemptLifecycleState.Completed,
                null,
                1,
                1,
                T0,
                T0.AddSeconds(1_679),
                "result-sha",
                "done",
                null,
                [])
        };

        var run = Assert.Single(RunTimelineBuilder.Build(
            events,
            lines: [],
            nowUtc: T0.AddHours(1),
            fallback: new RunTimelineFallbackContext(attempts, [], TaskMayHaveActiveRun: false)).Runs);

        Assert.Equal("completed", run.Status);
        Assert.Equal("done", run.Result);
        Assert.Equal(T0.AddSeconds(1_679), run.EndedAt);
        Assert.Equal(1_679, run.DurationSeconds);
        Assert.Equal(RunCloseoutSources.AttemptAuthority, run.CloseoutSource);
    }

    [Fact]
    public void Qs54LegacyShape_DerivesCloseoutFromAgentRunFinishedTimeline()
    {
        var events = new List<SessionEvent>
        {
            new() { Ts = T0, Kind = "start", Cli = "remote-runner" }
        };
        var ledger = new List<TimelineEvent>
        {
            new()
            {
                Ts = T0.AddSeconds(1_679),
                Kind = TimelineEventKinds.AgentRunFinished,
                Actor = TimelineActors.Agent,
                RunId = "run-54",
                Summary = "remote run done on remote-runner",
                Details = new Dictionary<string, string>
                {
                    ["status"] = "done",
                    ["runAttemptId"] = "run-54"
                }
            }
        };

        var run = Assert.Single(RunTimelineBuilder.Build(
            events,
            lines: [],
            nowUtc: T0.AddHours(1),
            fallback: new RunTimelineFallbackContext([], ledger, TaskMayHaveActiveRun: false)).Runs);

        Assert.Equal("completed", run.Status);
        Assert.Equal("done", run.Result);
        Assert.Equal(T0.AddSeconds(1_679), run.EndedAt);
        Assert.Equal(1_679, run.DurationSeconds);
        Assert.Equal(RunCloseoutSources.Timeline, run.CloseoutSource);
    }

    [Fact]
    public void LegacyTerminalRunWithoutCloseout_IsMarkedHonestly()
    {
        var events = new List<SessionEvent>
        {
            new() { Ts = T0, Kind = "start", Cli = "remote-runner" }
        };

        var run = Assert.Single(RunTimelineBuilder.Build(
            events,
            lines: [],
            nowUtc: T0.AddHours(1),
            fallback: new RunTimelineFallbackContext([], [], TaskMayHaveActiveRun: false)).Runs);

        Assert.Equal("unknown", run.Status);
        Assert.Null(run.Result);
        Assert.Null(run.EndedAt);
        Assert.Null(run.DurationSeconds);
        Assert.Equal(RunCloseoutSources.LegacyMissing, run.CloseoutSource);
    }

    [Fact]
    public void LegacyTerminalRun_DerivesDurationFromLastBoundedCliActivity()
    {
        var events = new List<SessionEvent>
        {
            new() { Ts = T0, Kind = "start", Cli = "remote-runner" }
        };
        var lines = new List<CliOutputLine>
        {
            StdOut(120, "remote runner is working"),
            StdOut(600, "remote runner emitted its last line")
        };

        var run = Assert.Single(RunTimelineBuilder.Build(
            events,
            lines,
            nowUtc: T0.AddHours(1),
            fallback: new RunTimelineFallbackContext([], [], TaskMayHaveActiveRun: false)).Runs);

        Assert.Equal("unknown", run.Status);
        Assert.Null(run.Result);
        Assert.Equal(T0.AddSeconds(600), run.EndedAt);
        Assert.Equal(600, run.DurationSeconds);
        Assert.Equal(RunCloseoutSources.LegacyActivity, run.CloseoutSource);
    }

    [Fact]
    public void Mkt21LegacyShape_SuccessorStartsCloseOrphanedRunsAsSuperseded()
    {
        var secondStart = T0.AddMinutes(26).AddSeconds(14);
        var thirdStart = secondStart.AddSeconds(27);
        var events = new List<SessionEvent>
        {
            new() { Ts = T0, Kind = "start", Cli = "codex" },
            new() { Ts = secondStart, Kind = "continue", Cli = "codex" },
            new()
            {
                Ts = thirdStart,
                Kind = "continue",
                Cli = "codex",
                FinishedAt = thirdStart.AddMinutes(9),
                Result = "done",
                Status = "completed"
            }
        };

        var timeline = RunTimelineBuilder.Build(
            events,
            lines: [],
            nowUtc: thirdStart.AddHours(1),
            fallback: new RunTimelineFallbackContext([], [], TaskMayHaveActiveRun: false));

        Assert.False(timeline.HasActiveRun);
        Assert.Equal(3, timeline.Runs.Count);
        Assert.Equal("superseded", timeline.Runs[0].Status);
        Assert.Equal("superseded", timeline.Runs[0].Result);
        Assert.Equal(secondStart, timeline.Runs[0].EndedAt);
        Assert.Equal(1_574, timeline.Runs[0].DurationSeconds);
        Assert.Equal(RunCloseoutSources.SuccessorStart, timeline.Runs[0].CloseoutSource);
        Assert.Equal("superseded", timeline.Runs[1].Status);
        Assert.Equal(27, timeline.Runs[1].DurationSeconds);
        Assert.Equal("completed", timeline.Runs[2].Status);
        Assert.Equal(540, timeline.Runs[2].DurationSeconds);
        Assert.DoesNotContain(timeline.Runs, run => run.Status == "running");
    }

    [Fact]
    public void Mkt21LegacyShape_PrefersRecordedTerminalEventsOverSuccessorFallback()
    {
        var secondStart = new DateTime(2026, 8, 8, 16, 27, 9, DateTimeKind.Utc);
        var thirdStart = new DateTime(2026, 8, 8, 16, 27, 36, DateTimeKind.Utc);
        var events = new List<SessionEvent>
        {
            new() { Ts = new DateTime(2026, 8, 8, 16, 0, 55, DateTimeKind.Utc), Kind = "start", Cli = "codex" },
            new() { Ts = secondStart, Kind = "continue", Cli = "codex" },
            new() { Ts = thirdStart, Kind = "continue", Cli = "codex" }
        };
        var ledger = new List<TimelineEvent>
        {
            Finished(events[0].Ts.AddSeconds(632.6), 632.6),
            Finished(secondStart.AddSeconds(18.2), 18.2),
            Finished(thirdStart.AddSeconds(11.1), 11.1)
        };

        var timeline = RunTimelineBuilder.Build(
            events,
            lines: [],
            nowUtc: thirdStart.AddHours(1),
            fallback: new RunTimelineFallbackContext([], ledger, TaskMayHaveActiveRun: false));

        Assert.False(timeline.HasActiveRun);
        Assert.All(timeline.Runs, run =>
        {
            Assert.Equal("completed", run.Status);
            Assert.Equal("completed", run.Result);
            Assert.Equal(RunCloseoutSources.Timeline, run.CloseoutSource);
            Assert.NotNull(run.EndedAt);
            Assert.NotNull(run.DurationSeconds);
        });
        Assert.Equal(632.6, timeline.Runs[0].DurationSeconds);
        Assert.Equal(18.2, timeline.Runs[1].DurationSeconds);
        Assert.Equal(11.1, timeline.Runs[2].DurationSeconds);
    }

    [Theory]
    [InlineData("done", "completed")]
    [InlineData("success", "completed")]
    [InlineData("noop", "completed")]
    [InlineData("failed", "failed")]
    [InlineData("unverified", "failed")]
    [InlineData("superseded", "superseded")]
    [InlineData("blocked", "blocked")]
    [InlineData("needsinput", "needs-input")]
    [InlineData("cancelled", "cancelled")]
    public void RunCloseoutPolicy_MapsTerminalOutcomeToDisplayStatus(string outcome, string expected)
    {
        Assert.Equal(expected, RunCloseoutPolicy.StatusFor(outcome, recordedStatus: null));
    }

    private static TimelineEvent Finished(DateTime at, double durationSeconds) => new()
    {
        Ts = at,
        Kind = TimelineEventKinds.AgentRunFinished,
        Actor = TimelineActors.Agent,
        Summary = "Run completed",
        Details = new Dictionary<string, string>
        {
            ["status"] = "completed",
            ["durationSeconds"] = durationSeconds.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
        }
    };

    [Fact]
    public void ContextRef_IsCarriedFromEventOntoRunRecord()
    {
        // The per-run passed-context pointer travels on the SessionEvent so it
        // stays 1:1 with the run even under torn writes. The builder must copy
        // it verbatim onto the RunRecord that the /runs/{index}/context
        // endpoint later dereferences; a null event ref stays null.
        var events = new List<SessionEvent>
        {
            new() { Ts = T0, Kind = "start", Cli = "claude", ContextRef = "logs/run-context/run-20260503-100000-000.md" },
            new() { Ts = T0.AddSeconds(120), Kind = "continue", Cli = "claude", Resumed = true, InputSessionId = "uuid-1" }
        };
        var lines = new List<CliOutputLine>
        {
            Sys(0, "[taskboard] Started claude CLI (PID 1234)"),
            Sys(50, "[taskboard] claude CLI exited: status=completed, exitCode=0, duration=50s"),
            User(110, "Keep going"),
            Sys(120, "[taskboard] Started claude CLI (PID 1235)"),
            Sys(180, "[taskboard] claude CLI exited: status=completed, exitCode=0, duration=60s")
        };

        var t = RunTimelineBuilder.Build(events, lines, T0.AddSeconds(200));

        Assert.Equal(2, t.RunCount);
        Assert.Equal("logs/run-context/run-20260503-100000-000.md", t.Runs[0].ContextRef);
        Assert.Null(t.Runs[1].ContextRef);
    }

    [Fact]
    public void ResolvedModelAndThinkingLevel_AreCarriedFromEventOntoRunRecord()
    {
        var events = new List<SessionEvent>
        {
            new()
            {
                Ts = T0,
                Kind = "start",
                Cli = "codex",
                Model = "gpt-5.6-sol",
                ThinkingLevel = "xhigh"
            }
        };
        var lines = new List<CliOutputLine>
        {
            Sys(0, "[taskboard] Started codex CLI (PID 1234)"),
            Sys(50, "[taskboard] codex CLI exited: status=completed, exitCode=0, duration=50s")
        };

        var run = Assert.Single(RunTimelineBuilder.Build(events, lines, T0.AddSeconds(60)).Runs);

        Assert.Equal("gpt-5.6-sol", run.Model);
        Assert.Equal("xhigh", run.ThinkingLevel);
        Assert.Null(run.ExecutionContext);
    }

    [Fact]
    public void ExecutionContext_IsCarriedFromEventOntoRunRecord()
    {
        // The per-run execution-context surface (ASS-1739 / T1a) is backfilled
        // onto the SessionEvent at run finish; the builder must copy it verbatim
        // onto the RunRecord so the protocol-pane "Execution Context" panel can
        // read it. A null event context stays null.
        var ctx = new AgentStudio.Shared.CliExecutionContext
        {
            Cli = "claude",
            Model = "claude-opus-4-8",
            PermissionMode = "bypassPermissions",
            Cwd = "C:/work/repo",
            CapturedAt = T0.AddSeconds(60),
            Source = "init-frame",
            Sources =
            [
                new() { Kind = AgentStudio.Shared.CliContextSourceKinds.Memory, Label = "Project memory", Path = "C:/work/repo/CLAUDE.md", Exists = true },
                new() { Kind = AgentStudio.Shared.CliContextSourceKinds.Mcp, Label = "gmail", Detail = "connected" },
            ],
        };
        var events = new List<SessionEvent>
        {
            new() { Ts = T0, Kind = "start", Cli = "claude", ExecutionContext = ctx },
            new() { Ts = T0.AddSeconds(120), Kind = "continue", Cli = "claude", Resumed = true, InputSessionId = "uuid-1" }
        };
        var lines = new List<CliOutputLine>
        {
            Sys(0, "[taskboard] Started claude CLI (PID 1234)"),
            Sys(50, "[taskboard] claude CLI exited: status=completed, exitCode=0, duration=50s"),
            User(110, "Keep going"),
            Sys(120, "[taskboard] Started claude CLI (PID 1235)"),
            Sys(180, "[taskboard] claude CLI exited: status=completed, exitCode=0, duration=60s")
        };

        var t = RunTimelineBuilder.Build(events, lines, T0.AddSeconds(200));

        Assert.Equal(2, t.RunCount);
        var first = t.Runs[0].ExecutionContext;
        Assert.NotNull(first);
        Assert.Equal("init-frame", first!.Source);
        Assert.Equal("claude-opus-4-8", first.Model);
        Assert.Equal("bypassPermissions", first.PermissionMode);
        Assert.Equal(2, first.Sources.Count);
        Assert.Contains(first.Sources, s => s.Kind == AgentStudio.Shared.CliContextSourceKinds.Mcp && s.Label == "gmail");
        Assert.Null(t.Runs[1].ExecutionContext);
    }

    [Fact]
    public void PromptEntries_ListInitialAndExtensionPromptsWithTokenSnapshots()
    {
        var jobFolder = Path.Combine(Path.GetTempPath(), "run-prompt-timeline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(jobFolder, "logs", "run-context"));
        try
        {
            File.WriteAllText(Path.Combine(jobFolder, "logs", "run-context", "run-1.md"), "# Captured run 1\n\nFull startup prompt.");
            File.WriteAllText(Path.Combine(jobFolder, "logs", "run-context", "run-2.md"), "# Captured run 2\n\nExtended prompt.");

            var runs = new List<RunRecord>
            {
                new()
                {
                    Index = 1,
                    Intent = "start",
                    StartedAt = T0,
                    ContextRef = "logs/run-context/run-1.md"
                },
                new()
                {
                    Index = 2,
                    Intent = "continue",
                    StartedAt = T0.AddMinutes(5),
                    UserFollowup = "Add the token snapshot.",
                    ContextRef = "logs/run-context/run-2.md"
                }
            };

            var prompts = RunPromptTimelineBuilder.Build(
                runs,
                jobFolder,
                "# Initial task\n\nBuild the timeline.",
                [
                    new TaskPromptHistoryEntry
                    {
                        Index = 1,
                        FileName = "prompt-1.md",
                        Markdown = "Add the token snapshot.",
                        WrittenAt = T0.AddMinutes(4)
                    }
                ],
                new ContextUsageSnapshot
                {
                    At = T0.AddMinutes(3),
                    Status = "ok",
                    Metrics = [new ContextUsageMetric { Label = "Context", Value = "42%" }]
                });

            Assert.Equal(2, prompts.Count);
            Assert.Equal("Prompt #1", prompts[0].Label);
            Assert.Equal("prompt.md", prompts[0].FileName);
            Assert.Equal("task-prompt", prompts[0].PromptTokenSource);
            Assert.True(prompts[0].PromptTokenEstimate > 0);
            Assert.True(prompts[0].ContextTokenEstimate > 0);
            Assert.Equal("captured-context", prompts[0].ContextSnapshot?.Source);

            Assert.Equal("Prompt #2", prompts[1].Label);
            Assert.Equal("prompt-1.md", prompts[1].FileName);
            Assert.Equal("prompt-history", prompts[1].PromptTokenSource);
            Assert.Contains("token snapshot", prompts[1].PromptPreview);
            Assert.Equal("logs/run-context/run-2.md", prompts[1].ContextRef);
        }
        finally
        {
            Directory.Delete(jobFolder, recursive: true);
        }
    }

    [Fact]
    public void TaskRefinements_MergeOperatorAndSystemSourcesChronologically()
    {
        var jobFolder = Path.Combine(Path.GetTempPath(), $"task-refinements-{Guid.NewGuid():N}");
        var historyFolder = Path.Combine(jobFolder, "orchestrator-follow-up-history");
        Directory.CreateDirectory(historyFolder);
        try
        {
            File.WriteAllText(
                Path.Combine(historyFolder, "20260503-100700-000-review-gap.md"),
                """
                # Orchestrator steering step

                ## Context
                - timestamp: 2026-05-03T10:07:00.0000000Z
                - cause: review-gap
                - reason: Missing regression coverage

                ## Steering prompt (verbatim)

                Add the missing browser regression.
                """);

            var result = TaskRefinementTimelineBuilder.Build(
                jobFolder,
                [
                    new RunRecord
                    {
                        Index = 2,
                        Intent = "continue",
                        StartedAt = T0.AddMinutes(5),
                        Reason = "mode=steer",
                        UserFollowup = "Keep the layout calm."
                    },
                    new RunRecord
                    {
                        Index = 3,
                        Intent = "continue",
                        StartedAt = T0.AddMinutes(10),
                        UserFollowup = "Add the acceptance screenshot."
                    }
                ],
                [
                    new TaskPromptHistoryEntry
                    {
                        Index = 1,
                        FileName = "prompt-1.md",
                        Markdown = "Keep the layout calm.",
                        WrittenAt = T0.AddMinutes(4)
                    }
                ]);

            Assert.Equal(3, result.Count);
            Assert.Equal("prompt-history", result[0].Source);
            Assert.Equal("operator", result[0].Actor);
            Assert.Equal("Task extended", result[0].Reason);
            Assert.Equal("orchestrator-history", result[1].Source);
            Assert.Equal("system", result[1].Actor);
            Assert.Equal("Missing regression coverage", result[1].Reason);
            Assert.Equal("Add the missing browser regression.", result[1].Markdown);
            Assert.Equal("run-log", result[2].Source);
            Assert.Equal(3, result[2].RunIndex);
        }
        finally
        {
            Directory.Delete(jobFolder, recursive: true);
        }
    }

    [Fact]
    public void TaskRefinements_UsesRunReasonForSteerFollowup()
    {
        var result = TaskRefinementTimelineBuilder.Build(
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}"),
            [
                new RunRecord
                {
                    Index = 2,
                    Intent = "continue",
                    StartedAt = T0,
                    Reason = "mode=steer",
                    UserFollowup = "Change direction."
                }
            ],
            []);

        var entry = Assert.Single(result);
        Assert.Equal("operator", entry.Actor);
        Assert.Equal("steer follow-up", entry.Reason);
        Assert.Equal("Change direction.", entry.Markdown);
    }

    [Fact]
    public void ReviewAttemptCycles_ProjectCurrentEpochAndClosedOperatorHistory()
    {
        var firstRun = T0;
        var firstRequeue = T0.AddHours(2);
        var secondRequeue = T0.AddHours(5);
        var events = new List<TimelineEvent>
        {
            new()
            {
                Ts = firstRequeue,
                Kind = TimelineEventKinds.OperatorRequeued,
                Actor = "human:operator@example.com",
                Summary = "Operator reopened the task.",
                Details = new Dictionary<string, string>
                {
                    ["attemptEpoch"] = "1",
                    ["reason"] = "Infrastructure repaired.",
                    ["from"] = TaskStates.Escalated,
                    ["to"] = TaskStates.AutoReview,
                    ["rotatedArtifacts"] = "4",
                },
            },
            new()
            {
                Ts = secondRequeue,
                Kind = TimelineEventKinds.OperatorRequeued,
                Actor = "human:operator@example.com",
                Summary = "Operator reopened the task again.",
                Details = new Dictionary<string, string>
                {
                    ["attemptEpoch"] = "2",
                    ["reason"] = "Reassess after runner recovery.",
                    ["from"] = TaskStates.HumanReview,
                    ["to"] = TaskStates.AutoReview,
                    ["rotatedArtifacts"] = "2",
                },
            },
        };

        var cycles = ReviewAttemptTimelineBuilder.Build(2, events, firstRun);

        Assert.Collection(
            cycles,
            current =>
            {
                Assert.Equal(2, current.Epoch);
                Assert.True(current.IsCurrent);
                Assert.Equal(secondRequeue, current.StartedAt);
                Assert.Null(current.EndedAt);
                Assert.Equal("Reassess after runner recovery.", current.Reason);
                Assert.Equal(2, current.RotatedArtifacts);
            },
            previous =>
            {
                Assert.Equal(1, previous.Epoch);
                Assert.False(previous.IsCurrent);
                Assert.Equal(firstRequeue, previous.StartedAt);
                Assert.Equal(secondRequeue, previous.EndedAt);
                Assert.Equal(TaskStates.Escalated, previous.FromState);
                Assert.Equal(TaskStates.AutoReview, previous.ToState);
            },
            initial =>
            {
                Assert.Equal(0, initial.Epoch);
                Assert.Equal(firstRun, initial.StartedAt);
                Assert.Equal(firstRequeue, initial.EndedAt);
                Assert.Equal("Initial review cycle.", initial.Reason);
            });
    }

    [Fact]
    public void ReviewAttemptCycles_KeepDurableCurrentEpochVisibleWhenTimelineWriteIsMissing()
    {
        var cycles = ReviewAttemptTimelineBuilder.Build(1, events: [], initialStartedAt: T0);

        Assert.Equal(2, cycles.Count);
        Assert.Equal(1, cycles[0].Epoch);
        Assert.True(cycles[0].IsCurrent);
        Assert.Null(cycles[0].StartedAt);
        Assert.Equal(0, cycles[1].Epoch);
        Assert.Equal(T0, cycles[1].StartedAt);
    }

    [Fact]
    public void ExitMarkerAfterNextEvent_IsNotMisattributed()
    {
        // Defensive: even though the product is sequential per project,
        // a torn write order could place an exit marker after the next
        // run's start. The builder must not pull a future exit into the
        // earlier run.
        var events = new List<SessionEvent>
        {
            new() { Ts = T0, Kind = "start", Cli = "claude" },
            new() { Ts = T0.AddSeconds(50), Kind = "continue", Cli = "claude", Resumed = true }
        };
        var lines = new List<CliOutputLine>
        {
            Sys(0, "[taskboard] Started claude CLI (PID 1234)"),
            // First exit marker is missing entirely (torn line).
            Sys(50, "[taskboard] Started claude CLI (PID 1235)"),
            Sys(120, "[taskboard] claude CLI exited: status=completed, exitCode=0, duration=70s")
        };

        var t = RunTimelineBuilder.Build(events, lines, T0.AddSeconds(150));

        Assert.Equal(2, t.RunCount);
        // Run 1 has no exit marker before run 2's start - status falls
        // back to "running" (or "unknown" if no started marker either).
        Assert.NotEqual("completed", t.Runs[0].Status);
        Assert.Equal("completed", t.Runs[1].Status);
        Assert.Equal(70.0, t.Runs[1].DurationSeconds);
    }
}
