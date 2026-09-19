using AgentStudio.Pipeline;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class OpenAiUsageHistoryRepairTests : IDisposable
{
    private static readonly DateTime At = new(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _temp = Path.Combine(
        Path.GetTempPath(),
        "agent-studio-openai-history-repair-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Task_receipt_repair_is_idempotent_and_reprices_the_normalized_dimensions()
    {
        var legacy = new TaskTokenSummary
        {
            Calls = 1,
            InputTokens = 14_983_295,
            OutputTokens = 24_305,
            CacheReadTokens = 14_786_304,
            TotalTokens = 29_793_904,
            EstimatedApiCostUsd = 66.33m,
            AllModelsPriced = true,
            Entries =
            [
                new TaskTokenCall
                {
                    Ts = At,
                    Model = "gpt-5.6-sol",
                    ParticipantId = "agent:codex",
                    InputTokens = 14_983_295,
                    OutputTokens = 24_305,
                    CacheReadTokens = 14_786_304,
                    EstimatedApiCostUsd = 66.33m,
                    ModelPriced = true,
                },
            ],
        };

        var first = OpenAiUsageHistoryRepair.RepairSummary(legacy, "project", "AGT-2873");
        var second = OpenAiUsageHistoryRepair.RepairSummary(first.Summary, "project", "AGT-2873");

        Assert.True(first.Changed);
        Assert.Equal(1, first.Corrected);
        var entry = Assert.Single(first.Summary.Entries);
        Assert.Equal(196_991, entry.InputTokens);
        Assert.Equal(14_786_304, entry.CacheReadTokens);
        Assert.True(entry.InputIncludesCached);
        Assert.Equal(ProviderUsageNormalization.OpenAiInputIncludesCachedV1, entry.UsageNormalization);
        Assert.Equal(7.19m, decimal.Round(entry.EstimatedApiCostUsd, 2));
        Assert.Equal(14_983_295 + 24_305, first.Summary.TotalTokens);
        Assert.False(second.Changed);
        Assert.Equal(first.Summary, second.Summary);
    }

    [Fact]
    public void Pipeline_repair_recurses_previous_attempts_and_leaves_claude_unchanged()
    {
        var record = new PipelineExecutionRecord
        {
            PipelineId = "default",
            Project = "project",
            JobId = "AGT-2873",
            StartedAt = At,
            Steps =
            [
                Step("codex", "gpt-5.6-sol", 100, 80),
                Step("claude", "claude-opus-5", 10, 90),
            ],
            PreviousAttempts =
            [
                new PipelineExecutionRecord
                {
                    PipelineId = "default",
                    Project = "project",
                    JobId = "AGT-2873",
                    StartedAt = At.AddHours(-1),
                    Steps = [Step("prior-codex", "gpt-5.6-sol", 50, 40)],
                },
            ],
        };

        var first = OpenAiUsageHistoryRepair.RepairPipeline(record, "project", "AGT-2873");
        var second = OpenAiUsageHistoryRepair.RepairPipeline(first.Record, "project", "AGT-2873");

        Assert.True(first.Changed);
        Assert.Equal(2, first.Corrected);
        Assert.Equal(20, first.Record.Steps[0].InputTokens);
        Assert.Equal(10, first.Record.PreviousAttempts[0].Steps[0].InputTokens);
        Assert.Equal(10, first.Record.Steps[1].InputTokens);
        Assert.Equal(90, first.Record.Steps[1].CacheReadTokens);
        Assert.Null(first.Record.Steps[1].InputIncludesCached);
        Assert.False(second.Changed);
        Assert.Equal(first.Record, second.Record);
    }

    [Fact]
    public void Pipeline_cost_uses_the_repaired_dimensions()
    {
        var record = new PipelineExecutionRecord
        {
            PipelineId = "default",
            Project = "project",
            JobId = "AGT-2873",
            StartedAt = At,
            CompletedAt = At.AddMinutes(1),
            Steps =
            [
                new PipelineStepExecution
                {
                    StepId = "core-agent-run",
                    Kind = StepKind.Core,
                    Model = "gpt-5.6-sol",
                    StartedAt = At,
                    CompletedAt = At.AddMinutes(1),
                    InputTokens = 14_983_295,
                    OutputTokens = 24_305,
                    CacheReadTokens = 14_786_304,
                },
            ],
        };

        var repaired = OpenAiUsageHistoryRepair
            .RepairPipeline(record, "project", "AGT-2873").Record;
        var timeline = ProjectPipelineCostService.BuildFromRecords(
            "project", [repaired], 1, At.AddHours(1));

        Assert.Equal(7.19m, decimal.Round(timeline.TotalCostUsd, 2));
        Assert.Equal(14_983_295 + 24_305, timeline.TotalTokens);
    }

    [Fact]
    public void OpenAi_entry_outside_the_safe_pattern_is_listed_and_untouched()
    {
        var untouched = new List<OpenAiUsageHistoryUntouchedEntry>();
        var summary = new TaskTokenSummary
        {
            Entries =
            [
                new TaskTokenCall
                {
                    Ts = At,
                    Model = "gpt-5.6-sol",
                    InputTokens = 10,
                    CacheReadTokens = 20,
                },
            ],
        };

        var repair = OpenAiUsageHistoryRepair.RepairSummary(
            summary, "project", "AGT-2873", untouched);

        Assert.False(repair.Changed);
        Assert.Single(untouched);
        Assert.Equal(summary, repair.Summary);
    }

    [Fact]
    public void Failed_writes_withhold_completion_and_retry_only_pending_items()
    {
        Directory.CreateDirectory(_temp);
        var reportPath = Path.Combine(_temp, OpenAiUsageHistoryRepair.ReportFileName);
        var store = new FailingRepairStore();
        store.Add("task-write-fails", Summary(100, 80), Pipeline(50, 40));
        store.Add("pipeline-write-fails", Summary(200, 150), Pipeline(90, 70));
        store.FailNextTaskWrite("task-write-fails");
        store.FailNextPipelineWrite("pipeline-write-fails");
        var repair = new OpenAiUsageHistoryRepair(
            store,
            reportPath,
            NullLogger<OpenAiUsageHistoryRepair>.Instance);

        var first = repair.RunOnce();

        Assert.False(first.Completed);
        Assert.False(first.AlreadyCompleted);
        Assert.False(File.Exists(reportPath));
        Assert.Equal(2, first.RepairedCount);
        Assert.Equal(0, first.SkippedAlreadyCorrectCount);
        Assert.Equal(2, first.FailedCount);
        Assert.Equal(
            [
                "pipeline-write-fails:pipeline-execution.json",
                "task-write-fails:task.json tokenSummary",
            ],
            first.FailedKeys);

        var second = repair.RunOnce();

        Assert.True(second.Completed);
        Assert.False(second.AlreadyCompleted);
        Assert.True(File.Exists(reportPath));
        Assert.Equal(first.FailedKeys.Order(), second.RepairedKeys.Order());
        Assert.Equal(first.RepairedKeys.Order(), second.SkippedAlreadyCorrectKeys.Order());
        Assert.Empty(second.FailedKeys);
        Assert.Equal(1, second.CorrectedTaskEntries);
        Assert.Equal(1, second.CorrectedPipelineSteps);
        Assert.Equal(20, store.Summary("task-write-fails").Entries[0].InputTokens);
        Assert.Equal(50, store.Summary("pipeline-write-fails").Entries[0].InputTokens);
        Assert.Equal(10, store.Pipeline("task-write-fails").Steps[0].InputTokens);
        Assert.Equal(20, store.Pipeline("pipeline-write-fails").Steps[0].InputTokens);
        Assert.Equal(2, store.TaskWriteAttempts["task-write-fails"]);
        Assert.Equal(1, store.TaskWriteAttempts["pipeline-write-fails"]);
        Assert.Equal(1, store.PipelineWriteAttempts["task-write-fails"]);
        Assert.Equal(2, store.PipelineWriteAttempts["pipeline-write-fails"]);

        var third = repair.RunOnce();

        Assert.True(third.AlreadyCompleted);
        Assert.True(third.Completed);
        Assert.Equal(2, store.TaskWriteAttempts["task-write-fails"]);
        Assert.Equal(1, store.TaskWriteAttempts["pipeline-write-fails"]);
        Assert.Equal(1, store.PipelineWriteAttempts["task-write-fails"]);
        Assert.Equal(2, store.PipelineWriteAttempts["pipeline-write-fails"]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true);
    }

    private static TaskTokenSummary Summary(long input, long cacheRead)
        => new()
        {
            Entries =
            [
                new TaskTokenCall
                {
                    Ts = At,
                    Model = "gpt-5.6-sol",
                    InputTokens = input,
                    OutputTokens = 5,
                    CacheReadTokens = cacheRead,
                },
            ],
        };

    private static PipelineExecutionRecord Pipeline(long input, long cacheRead)
        => new()
        {
            PipelineId = "default",
            Project = "project",
            JobId = "job",
            StartedAt = At,
            Steps = [Step("core", "gpt-5.6-sol", input, cacheRead)],
        };

    private static PipelineStepExecution Step(string id, string model, long input, long cacheRead)
        => new()
        {
            StepId = id,
            Kind = StepKind.Aspect,
            Model = model,
            StartedAt = At,
            InputTokens = input,
            OutputTokens = 5,
            CacheReadTokens = cacheRead,
        };

    private sealed class FailingRepairStore : IOpenAiUsageHistoryRepairStore
    {
        private readonly Dictionary<string, TaskTokenSummary> _summaries = [];
        private readonly Dictionary<string, PipelineExecutionRecord> _pipelines = [];
        private readonly HashSet<string> _taskFailures = [];
        private readonly HashSet<string> _pipelineFailures = [];

        public Dictionary<string, int> TaskWriteAttempts { get; } = [];
        public Dictionary<string, int> PipelineWriteAttempts { get; } = [];

        public void Add(
            string key,
            TaskTokenSummary summary,
            PipelineExecutionRecord pipeline)
        {
            _summaries[key] = summary;
            _pipelines[key] = pipeline;
            TaskWriteAttempts[key] = 0;
            PipelineWriteAttempts[key] = 0;
        }

        public void FailNextTaskWrite(string key) => _taskFailures.Add(key);
        public void FailNextPipelineWrite(string key) => _pipelineFailures.Add(key);
        public TaskTokenSummary Summary(string key) => _summaries[key];
        public PipelineExecutionRecord Pipeline(string key) => _pipelines[key];

        public IReadOnlyList<OpenAiUsageHistoryRepairJob> ScanJobs()
            => _summaries.Keys
                .Order()
                .Select(key => new OpenAiUsageHistoryRepairJob(
                    key,
                    "project",
                    key,
                    _summaries[key]))
                .ToList();

        public bool ReplaceTokenSummary(string folderPath, TaskTokenSummary summary)
        {
            TaskWriteAttempts[folderPath]++;
            if (_taskFailures.Remove(folderPath)) return false;
            _summaries[folderPath] = summary;
            return true;
        }

        public PipelineExecutionRecord? ReadPipeline(string folderPath)
            => _pipelines.GetValueOrDefault(folderPath);

        public bool ReplacePipeline(string folderPath, PipelineExecutionRecord record)
        {
            PipelineWriteAttempts[folderPath]++;
            if (_pipelineFailures.Remove(folderPath)) return false;
            _pipelines[folderPath] = record;
            return true;
        }

        public void InvalidateCaches()
        {
        }
    }
}
