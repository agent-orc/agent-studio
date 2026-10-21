using AgentStudio.Cli;
using AgentStudio.Shared;
using AgentStudio.Tokens;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: every timestamp is fold input; the folds take the range explicitly and never read now.
public sealed class TokenAnalysisFoldTests
{
    private static readonly DateTime T0 = new(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T1 = new(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc);
    private static readonly ITokenPricing Pricing = TokenTestServices.Pricing();

    private static OrchestratorLogEntry Call(DateTime at, string participant, string? topic, string model,
        int input, int output, int cacheRead = 0, int? reasoning = null, string? cli = null, string? host = null)
        => new()
        {
            Ts = at,
            JobId = "job-1",
            ParticipantId = participant,
            Topic = topic ?? "",
            TokenUsage = new OrchestratorTokenUsage
            {
                Model = model, InputTokens = input, OutputTokens = output, CacheReadTokens = cacheRead,
                ReasoningTokens = reasoning, CliType = cli, Host = host, ThinkingLevel = "high",
            },
        };

    private static PipelineExecutionRecord Log()
    {
        var first = new PipelineExecutionRecord
        {
            JobId = "job-1", Attempt = 1, StartedAt = T0, CompletedAt = T0.AddHours(1),
            Steps =
            [
                new PipelineStepExecution
                {
                    StepId = "core-agent-run", Kind = StepKind.Core, Status = PipelineStepStatus.Passed,
                    StartedAt = T0, CompletedAt = T0.AddMinutes(10), DurationMs = 600_000,
                    Model = "gpt-5.6-sol", ThinkingLevel = "high", InputTokens = 9_999, OutputTokens = 999,
                },
                new PipelineStepExecution
                {
                    StepId = "aspect-code-quality", Kind = StepKind.Aspect, Status = PipelineStepStatus.Passed,
                    StartedAt = T0.AddMinutes(12), CompletedAt = T0.AddMinutes(14), DurationMs = 120_000,
                    Model = "claude-sonnet-5", ThinkingLevel = "medium", InputTokens = 300, OutputTokens = 50,
                    Verdict = "pass",
                },
            ],
        };
        return new PipelineExecutionRecord
        {
            JobId = "job-1", Attempt = 2, StartedAt = T1, CompletedAt = T1.AddHours(1),
            PreviousAttempts = [first],
            Steps =
            [
                new PipelineStepExecution
                {
                    StepId = "core-agent-run", Kind = StepKind.Core, Status = PipelineStepStatus.Passed,
                    StartedAt = T1, CompletedAt = T1.AddMinutes(5), DurationMs = 300_000,
                    Model = "gpt-5.6-sol", ThinkingLevel = "low", InputTokens = 500, OutputTokens = 100,
                },
                new PipelineStepExecution
                {
                    StepId = "post-build-test-gate", Kind = StepKind.Tool, Status = PipelineStepStatus.Failed,
                    StartedAt = T1.AddMinutes(6), CompletedAt = T1.AddMinutes(7), DurationMs = 60_000,
                    Verdict = "fail",
                },
            ],
        };
    }

    private static TokenAnalysisJobInput Job(params OrchestratorLogEntry[] ledger)
        => new("job-1",
            new TaskInfo
            {
                Id = "job-1", Key = "AGT-1", Title = "Card one", State = TaskStates.HumanReview,
                TaskType = "feature", Tags = ["tokens"],
            },
            Log(), ledger);

    private static TokenAnalysisJobInput StandardJob() => Job(
        // Prices the first coding run; the logged 9,999 / 999 become structure.
        Call(T0.AddMinutes(5), "agent:codex", "task-token-receipt", "gpt-5.6-sol", 1_200, 300, 400, reasoning: 80),
        // Equals the measured aspect execution, so it is counted once, by the step.
        Call(T0.AddMinutes(13), "support:aspect", null, "claude-sonnet-5", 300, 50),
        Call(T0.AddMinutes(20), "orchestrator:P", "orchestrator-decision", "claude-sonnet-5", 400, 40),
        Call(T1.AddMinutes(30), "orchestrator:P", "orchestrator-chat", "claude-sonnet-5", 100, 10));

    [Fact]
    public void Every_token_lands_in_one_kind_and_the_card_total_is_the_sum_of_its_slices()
    {
        var job = StandardJob();
        var items = TokenAnalysisFold.Items(Pricing, job);
        var card = TokenAnalysisFold.Card("Studio", job, items, DateTime.MinValue, DateTime.MaxValue);

        Assert.NotNull(card);
        long Kind(string kind) => card!.Slices.Where(slice => slice.Kind == kind).Sum(TokenAnalysisFold.SliceTotal);
        Assert.Equal(1_900, Kind(TokenUsageKinds.Coding));
        Assert.Equal(350, Kind(TokenUsageKinds.ReviewAspect));
        Assert.Equal(440, Kind(TokenUsageKinds.OrchestratorDecision));
        Assert.Equal(600, Kind(TokenUsageKinds.FixRound));
        Assert.Equal(110, Kind(TokenUsageKinds.Chat));
        Assert.Equal(0, Kind(TokenUsageKinds.Gate));
        Assert.Equal(3_400, card!.TotalTokens);
        Assert.Equal(card.Slices.Sum(TokenAnalysisFold.SliceTotal), card.TotalTokens);
        Assert.Equal(card.Slices.Sum(slice => slice.CostUsd), card.CostUsd);
        Assert.Equal(2, card.Rounds);
        Assert.Equal(CardOutcomes.Delivered, card.Outcome);
        Assert.False(card.InFlight);
        Assert.Equal("AGT-1", card.Key);
        Assert.Equal(new[] { "tokens" }, card.Tags);
    }

    [Fact]
    public void Slices_carry_model_level_cli_host_day_and_reported_reasoning()
    {
        var job = StandardJob();
        var card = TokenAnalysisFold.Card("Studio", job, TokenAnalysisFold.Items(Pricing, job),
            DateTime.MinValue, DateTime.MaxValue)!;

        var coding = Assert.Single(card.Slices, slice => slice.Kind == TokenUsageKinds.Coding);
        Assert.Equal("codex", coding.Cli);
        Assert.Equal(TokenUsageHost.Local, coding.Host);
        Assert.Equal("high", coding.Level);
        Assert.Equal("2026-10-19", coding.Day);
        Assert.Equal(400, coding.CacheRead);
        Assert.Equal(80, coding.Reasoning);
        var fix = Assert.Single(card.Slices, slice => slice.Kind == TokenUsageKinds.FixRound);
        Assert.Equal("low", fix.Level);
        Assert.Equal("2026-10-20", fix.Day);
        Assert.Null(fix.Reasoning);
        var aspect = Assert.Single(card.Slices, slice => slice.Kind == TokenUsageKinds.ReviewAspect);
        Assert.Equal("medium", aspect.Level);
        Assert.Equal("claude", aspect.Cli);
    }

    [Fact]
    public void Range_keeps_only_the_items_inside_it_and_a_card_without_any_is_absent()
    {
        var job = StandardJob();
        var items = TokenAnalysisFold.Items(Pricing, job);

        var secondDay = TokenAnalysisFold.Card("Studio", job, items, T1, T1.AddDays(1))!;
        Assert.Equal(710, secondDay.TotalTokens);
        Assert.All(secondDay.Slices, slice => Assert.Equal("2026-10-20", slice.Day));
        Assert.Null(TokenAnalysisFold.Card("Studio", job, items, T1.AddDays(2), T1.AddDays(3)));
    }

    [Fact]
    public void Without_a_receipt_the_logged_coding_run_prices_itself()
    {
        var job = Job();
        var card = TokenAnalysisFold.Card("Studio", job, TokenAnalysisFold.Items(Pricing, job),
            DateTime.MinValue, DateTime.MaxValue)!;

        Assert.Equal(10_998, card.Slices.Where(slice => slice.Kind == TokenUsageKinds.Coding)
            .Sum(TokenAnalysisFold.SliceTotal));
        Assert.Equal(350, card.Slices.Where(slice => slice.Kind == TokenUsageKinds.ReviewAspect)
            .Sum(TokenAnalysisFold.SliceTotal));
    }

    [Fact]
    public void A_card_without_a_pipeline_log_books_agent_calls_as_one_coding_run()
    {
        var job = new TokenAnalysisJobInput("job-1", null, null,
        [
            Call(T0, "agent:claude", "task-token-receipt", "claude-sonnet-5", 100, 10),
            Call(T0.AddHours(2), "agent:claude", "task-token-receipt", "claude-sonnet-5", 200, 20),
        ]);
        var card = TokenAnalysisFold.Card("Studio", job, TokenAnalysisFold.Items(Pricing, job),
            DateTime.MinValue, DateTime.MaxValue)!;

        Assert.Equal(330, card.TotalTokens);
        Assert.All(card.Slices, slice => Assert.Equal(TokenUsageKinds.Coding, slice.Kind));
        Assert.Equal(1, card.Rounds);
        Assert.Equal("job-1", card.Title);
        Assert.Equal(CardOutcomes.Open, card.Outcome);
    }

    [Fact]
    public void Unpriced_models_mark_the_cost_incomplete()
    {
        var job = new TokenAnalysisJobInput("job-1", null, null,
            [Call(T0, "agent:claude", null, "mystery-model-9", 100, 10)]);
        var card = TokenAnalysisFold.Card("Studio", job, TokenAnalysisFold.Items(Pricing, job),
            DateTime.MinValue, DateTime.MaxValue)!;

        Assert.False(card.CostComplete);
        Assert.Equal(0m, card.CostUsd);
        Assert.Equal(110, card.TotalTokens);
    }

    [Fact]
    public void A_running_step_marks_the_card_in_flight()
    {
        var log = Log() with
        {
            CompletedAt = null,
            Steps = [new PipelineStepExecution
            {
                StepId = "core-agent-run", Kind = StepKind.Core, Status = PipelineStepStatus.Running,
                StartedAt = T1, Model = "gpt-5.6-sol", InputTokens = 50, OutputTokens = 5,
            }],
        };
        var job = new TokenAnalysisJobInput("job-1", new TaskInfo { Id = "job-1", State = TaskStates.Ready }, log, []);
        var card = TokenAnalysisFold.Card("Studio", job, TokenAnalysisFold.Items(Pricing, job),
            DateTime.MinValue, DateTime.MaxValue)!;

        Assert.True(card.InFlight);
    }

    [Fact]
    public void Drill_down_folds_run_calls_into_the_run_and_lists_every_other_step()
    {
        var job = StandardJob();
        var events = TokenAnalysisFold.Events(TokenAnalysisFold.Items(Pricing, job));

        Assert.Equal(
            new[] { TokenUsageKinds.Coding, TokenUsageKinds.ReviewAspect, TokenUsageKinds.OrchestratorDecision,
             TokenUsageKinds.FixRound, TokenUsageKinds.Gate, TokenUsageKinds.Chat },
            events.Select(e => e.Kind));
        var coding = events[0];
        Assert.Equal("core-agent-run", coding.Label);
        Assert.Equal(1, coding.Attempt);
        Assert.Equal(1_900, coding.Total);
        Assert.Equal(600_000, coding.DurationMs);
        Assert.Equal("passed", coding.Result);
        Assert.Equal(80, coding.Reasoning);
        var gate = events[4];
        Assert.Equal(2, gate.Attempt);
        Assert.Equal(0, gate.Total);
        Assert.Equal(60_000, gate.DurationMs);
        Assert.Equal("fail", gate.Result);
        Assert.Equal(TokenAnalysisFold.SourceLedger, events[5].Source);
        Assert.Equal(3_400, events.Sum(e => e.Total));
    }

    [Fact]
    public void Ledger_rows_without_a_card_are_a_classified_remainder()
    {
        var rows = new[]
        {
            Call(T0, "orchestrator:P", "orchestrator-chat", "claude-sonnet-5", 100, 10) with { JobId = null },
            Call(T0, "orchestrator:P", "orchestrator-boot", "claude-sonnet-5", 50, 5) with { JobId = null },
        };
        var slices = TokenAnalysisFold.Slices(TokenAnalysisFold.RemainderItems(Pricing, rows));

        Assert.Equal(110, slices.Single(slice => slice.Kind == TokenUsageKinds.Chat).Input
            + slices.Single(slice => slice.Kind == TokenUsageKinds.Chat).Output);
        Assert.Equal(55, TokenAnalysisFold.SliceTotal(
            slices.Single(slice => slice.Kind == TokenUsageKinds.OrchestratorDecision)));
    }

    [Theory]
    [InlineData("codex", "agent:claude", "claude-sonnet-5", "codex")]
    [InlineData(null, "agent:claude", "gpt-5.6-sol", "claude")]
    [InlineData(null, "agent:remote-runner:host-1", "gpt-5.6-sol", "codex")]
    [InlineData(null, "orchestrator:P", "claude-sonnet-5", "claude")]
    [InlineData(null, null, "mystery-model-9", TokenAnalysisFold.Unknown)]
    public void Cli_comes_from_the_record_then_the_participant_then_the_model(
        string? cli, string? participant, string model, string expected)
        => Assert.Equal(expected, TokenAnalysisFold.Cli(cli, participant, model));

    [Fact]
    public void Quota_windows_split_at_resets_and_divide_tokens_by_consumed_points()
    {
        var reset1 = T0.AddDays(7);
        var reset2 = T0.AddDays(14);
        var history = new List<QuotaHistoryPoint>
        {
            new(T0, "Weekly", 10, reset1),
            new(T0.AddHours(10), "Weekly", 30, reset1),
            new(T0.AddHours(20), "Weekly", 50, reset1),
            // Reset: the percentage drops and the reset time moves a week.
            new(T0.AddHours(30), "Weekly", 2, reset2),
            new(T0.AddHours(40), "Weekly", 2.5, reset2),
            new(T0.AddHours(1), "5h", 5, null),
        };
        var usage = new List<QuotaUsageSample>
        {
            new(T0.AddHours(5), "codex", 1_000_000, 100_000),
            new(T0.AddHours(15), "codex", 1_000_000, 100_000),
            new(T0.AddHours(15), "claude", 7_777, 7_777),
            new(T0.AddHours(35), "codex", 500_000, 50_000),
        };

        var windows = QuotaEfficiencyFold.Fold("codex", history, usage, T0.AddDays(-1), T0.AddDays(3));

        var weekly = Assert.Single(windows, window => window.Window == "Weekly");
        Assert.Equal(2, weekly.Segments.Count);
        var closed = weekly.Segments[0];
        Assert.Equal(40, closed.EndPct - closed.StartPct);
        Assert.Equal(2_000_000, closed.Tokens);
        Assert.Equal(50_000, closed.TokensPerPct);
        Assert.Equal(5_000, closed.UncachedTokensPerPct);
        Assert.False(closed.Open);
        var open = weekly.Segments[1];
        Assert.True(open.Open);
        Assert.Equal(500_000, open.Tokens);
        Assert.Null(open.TokensPerPct);
        Assert.Equal(5, weekly.Points.Count);
        Assert.Contains(windows, window => window.Window == "5h");
    }
}
