using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using AgentStudio.Registry;
using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Tokens;

using Xunit;

namespace AgentStudio.Tests;

// clock-independent: fixed timestamps are ledger data used by pure readers and pricing.

/// <summary>
/// AGT-3012: AGT-3004's receipt held one Claude session's usage 22 times
/// (5.6 billion tokens over 23 calls instead of about 260 million over two).
/// The Claude result frame restates the session's cumulative
/// <c>modelUsage</c> on every frame and the remote receipt summed each one.
/// These tests pin the write path (one row per session snapshot, completion
/// replay is a no-op), every reader (collapse already-written duplicates),
/// the one-off repair, and the AGT-2882 Codex cache correction on the
/// receipt and bus read paths.
/// </summary>
public sealed class TokenLedgerDuplicateTests : IDisposable
{
    private const string ProjectName = "Agent Studio";
    private const string JobId = "agt-3004";
    private const string Attempt = "run_3fe269e9166f41248f987acc7011ee5c";
    private const string LaterAttempt = "run_a131d6c417b2465ea656914cde1969a2";

    // AGT-3004 production values (task.json receipt, 2026-10-04).
    private static readonly DateTime SessionAt = new(2026, 9, 29, 13, 29, 54, 50, DateTimeKind.Utc);
    private static readonly DateTime CodexAt = new(2026, 10, 3, 4, 32, 56, 381, DateTimeKind.Utc);
    private const long SessionInput = 4_096;
    private const long SessionOutput = 983_563;
    private const long SessionCacheRead = 248_769_893;
    private const long SessionCacheWrite = 4_987_546;
    private const long SessionTotal = 254_745_098;
    private const long CodexTotal = 6_129_017;
    private const int Copies = 22;

    private readonly string _workspace;
    private readonly string _watchPath;

    public TokenLedgerDuplicateTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "token-ledger-duplicates-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "watched");
        Directory.CreateDirectory(_watchPath);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void Receipt_collapse_preserves_input_order_without_duplicates()
    {
        TaskTokenCall[] calls =
        [
            new() { Ts = CodexAt, InputTokens = 100 },
            new() { Ts = SessionAt, InputTokens = 200 },
        ];

        var kept = TokenLedgerDuplicates.CollapseCalls(calls, out var collapsed);

        Assert.Same(calls, kept);
        Assert.Equal(TokenLedgerCollapseCount.None, collapsed);
    }

    [Fact]
    public void Receipt_collapse_keeps_earliest_attribution_without_sorting_unrelated_calls()
    {
        var original = new TaskTokenCall
        {
            Ts = SessionAt, Model = "gpt-6-sol", InputTokens = 100,
            ParticipantId = $"agent:remote-runner:{Attempt}:usage:turn:00000000:00",
        };
        var replay = original with
        {
            Ts = SessionAt.AddDays(1), Model = "claude-opus-5-5",
            ParticipantId = $"agent:remote-runner:{LaterAttempt}:usage:turn:00000000:00",
        };
        var unrelated = new TaskTokenCall { Ts = CodexAt, InputTokens = 200 };

        var kept = TokenLedgerDuplicates.CollapseCalls([replay, unrelated, original], out var collapsed);

        Assert.Equal(new[] { unrelated, original }, kept);
        Assert.Equal(1, collapsed.Entries);
        Assert.Equal(100, collapsed.Tokens);
    }

    [Theory]
    [InlineData("AGT-3897", 23_140_000, 9, 31)]
    [InlineData("AGT-3891", 15_340_000, 9, 16)]
    [InlineData("AGT-3830", 7_160_000, 3, 26)]
    public void Reimport_does_not_redate_or_reattribute_an_earlier_Codex_turn(
        string card, int input, int hour, int minute)
    {
        var (scanner, _, receipts) = BuildWriteServices();
        WriteTask(null);
        var folder = Path.GetDirectoryName(TaskJsonPath())!;
        var at = new DateTime(2026, 11, 17, hour, minute, 0, DateTimeKind.Utc);
        var oldCall = new TaskTokenCall
        {
            Ts = at, Model = "gpt-6-sol", InputTokens = input, OutputTokens = 100,
            InputIncludesCached = true,
            ParticipantId = $"agent:remote-runner:{Attempt}:usage:turn:00000000:00",
        };
        WriteTask(Summary(oldCall));
        var path = TaskPaths.CliOutputLog(folder);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var frame = JsonSerializer.Serialize(new
        {
            type = "turn.completed",
            usage = new { input_tokens = input, cached_input_tokens = 0, output_tokens = 100 },
        });
        File.WriteAllText(path, $"[{at:HH:mm:ss.fff}] [stdout] {frame}\n");
        File.SetLastWriteTimeUtc(path, at.AddDays(1));
        new TaskSessionLog(scanner, NullLogger<TaskSessionLog>.Instance).AppendSessionEvent(JobId,
            new SessionEvent
            {
                Ts = at.AddDays(1).AddMinutes(-1), FinishedAt = at.AddDays(1).AddMinutes(1),
                RunAttemptId = LaterAttempt, Cli = "claude", Model = "claude-opus-5-5",
            }, _watchPath);

        var result = receipts.PersistFromLog(scanner.FindJob(JobId, _watchPath)!, LaterAttempt, "host");

        Assert.False(result.Persisted);
        var read = new ProjectTokenReceiptReader().Read(_watchPath);
        var only = Assert.Single(read.Entries);
        Assert.Equal(at, only.Ts);
        Assert.Equal("gpt-6-sol", only.TokenUsage!.Model);
        Assert.Equal(input, only.TokenUsage.InputTokens);
        Assert.False(string.IsNullOrEmpty(card));
    }

    [Theory]
    [InlineData(23_140_000)]
    [InlineData(15_340_000)]
    [InlineData(7_160_000)]
    public void Receipt_reader_collapses_replayed_source_turn_despite_changed_day_and_model(int input)
    {
        var real = new TaskTokenCall
        {
            Ts = new DateTime(2026, 11, 17, 9, 31, 0, DateTimeKind.Utc),
            Model = "gpt-6-sol", InputTokens = input, OutputTokens = 100, InputIncludesCached = true,
            UsageNormalization = ProviderUsageNormalization.OpenAiInputIncludesCachedV1,
            ParticipantId = $"agent:remote-runner:{Attempt}:usage:turn:00000000:00",
        };
        var phantom = real with
        {
            Ts = real.Ts.AddDays(1), Model = "claude-opus-5-5",
            ParticipantId = $"agent:remote-runner:{LaterAttempt}:usage:turn:00000000:00",
        };
        WriteTask(Summary(real, phantom));

        var read = new ProjectTokenReceiptReader().Read(_watchPath);

        Assert.Equal(real.Ts, Assert.Single(read.Entries).Ts);
        Assert.Equal("gpt-6-sol", read.Entries[0].TokenUsage!.Model);
        Assert.Equal(1, read.CollapsedDuplicates.Entries);
        Assert.Equal(input + 100, read.Summaries[JobId].TotalTokens);
        var events = TokenAnalysisFold.Events(TokenAnalysisFold.Items(TokenTestServices.Pricing(),
            new TokenAnalysisJobInput(JobId, null, null, read.Entries)));
        var coding = Assert.Single(events);
        Assert.Equal(real.Ts, coding.At);
        Assert.Equal(ModelIdentity.Label(real.Model), coding.Model);
        Assert.Equal(TokenTestServices.Pricing().Estimate(real.Model, input, 100, 0, 0, real.Ts).Total, coding.CostUsd);
        Assert.Empty(TokenAnalysisFold.DuplicateCodingEvents(events));
    }

    [Fact]
    public void Cumulative_Claude_receipt_stays_on_its_original_day_and_reasoning_level()
    {
        var real = new TaskTokenCall
        {
            Ts = new DateTime(2026, 11, 16, 3, 24, 0, DateTimeKind.Utc),
            Model = "claude-opus-5-5", ThinkingLevel = "medium",
            CacheReadTokens = 41_700_000, OutputTokens = 111_000,
            ParticipantId = $"agent:remote-runner:{Attempt}:usage:scope:claude-session-1",
        };
        WriteTask(Summary(real, real with
        {
            Ts = real.Ts.AddDays(1), ThinkingLevel = "high",
            ParticipantId = $"agent:remote-runner:{LaterAttempt}:usage:scope:claude-session-1",
        }));
        var read = new ProjectTokenReceiptReader().Read(_watchPath);
        var entry = Assert.Single(read.Entries);
        Assert.Equal(real.Ts, entry.Ts);
        Assert.Equal("medium", entry.TokenUsage!.ThinkingLevel);
        Assert.Equal(41_700_000, entry.TokenUsage.CacheReadTokens);
        Assert.Equal(111_000, entry.TokenUsage.OutputTokens);
    }

    [Fact]
    public void Recorded_run_dimensions_and_absolute_log_time_win_over_current_card_settings()
    {
        var (scanner, _, receipts) = BuildWriteServices();
        WriteTask(null); // The current card is Claude; the completed run was Codex.
        var folder = Path.GetDirectoryName(TaskJsonPath())!;
        var at = new DateTime(2026, 11, 17, 9, 31, 0, DateTimeKind.Utc);
        var sessions = new TaskSessionLog(scanner, NullLogger<TaskSessionLog>.Instance);
        sessions.AppendSessionEvent(JobId, new SessionEvent
        {
            Ts = at.AddMinutes(-1), FinishedAt = at.AddMinutes(1), RunAttemptId = Attempt,
            Cli = "codex", Model = "gpt-6-sol", ThinkingLevel = "high",
        }, _watchPath);
        var path = TaskPaths.CliOutputLog(folder);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var frame = JsonSerializer.Serialize(new
        {
            type = "turn.completed",
            usage = new { input_tokens = 44_300_000, cached_input_tokens = 44_000_000, output_tokens = 111_000 },
        });
        File.WriteAllText(path, $"[{at:yyyy-MM-ddTHH:mm:ss.fffZ}] [stdout] {frame}\n");
        File.SetLastWriteTimeUtc(path, at.AddDays(1));
        Assert.True(receipts.PersistFromLog(scanner.FindJob(JobId, _watchPath)!, Attempt, "host").Persisted);
        var call = Assert.Single(ReadReceipt().Entries);
        Assert.Equal(at, call.Ts);
        Assert.Equal("gpt-6-sol", call.Model);
        Assert.Equal("codex", call.CliType);
        Assert.Equal("high", call.ThinkingLevel);
        Assert.Equal(300_000, call.InputTokens);
        Assert.Equal(44_000_000, call.CacheReadTokens);
        var before = File.ReadAllText(TaskJsonPath());
        File.SetLastWriteTimeUtc(path, at.AddDays(2));
        Assert.True(receipts.PersistFromLog(scanner.FindJob(JobId, _watchPath)!, Attempt, "host").Persisted);
        Assert.Equal(before, File.ReadAllText(TaskJsonPath()));
    }

    [Fact]
    public void Codex_frame_without_cached_input_field_is_stored_as_unpriced_evidence()
    {
        var (scanner, _, receipts) = BuildWriteServices();
        WriteTask(null);
        var folder = Path.GetDirectoryName(TaskJsonPath())!;
        var at = new DateTime(2026, 11, 17, 9, 31, 0, DateTimeKind.Utc);
        // Older imports marked missing cache fields as an explicit zero.
        // Re-import must revise that evidence while preserving attribution.
        WriteTask(Summary(new TaskTokenCall
        {
            Ts = at, Model = "gpt-6-sol", InputTokens = 61_600_000, OutputTokens = 111_000,
            InputIncludesCached = true, ModelPriced = true, EstimatedApiCostUsd = 124m,
            ParticipantId = $"agent:remote-runner:{Attempt}:usage:turn:00000000:00",
        }));
        new TaskSessionLog(scanner, NullLogger<TaskSessionLog>.Instance).AppendSessionEvent(JobId,
            new SessionEvent
            {
                Ts = at.AddMinutes(-1), FinishedAt = at.AddMinutes(1), RunAttemptId = Attempt,
                Cli = "codex", Model = "gpt-6-sol",
            }, _watchPath);
        var path = TaskPaths.CliOutputLog(folder);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var frame = JsonSerializer.Serialize(new
        {
            type = "turn.completed", usage = new { input_tokens = 61_600_000, output_tokens = 111_000 },
        });
        File.WriteAllText(path, $"[{at:yyyy-MM-ddTHH:mm:ss.fffZ}] [stdout] {frame}\n");
        Assert.True(receipts.PersistFromLog(scanner.FindJob(JobId, _watchPath)!, Attempt, "host").Persisted);
        var call = Assert.Single(ReadReceipt().Entries);
        Assert.Null(call.InputIncludesCached);
        Assert.False(call.ModelPriced);
        Assert.Equal(0m, call.EstimatedApiCostUsd);
        Assert.Equal(61_600_000, call.InputTokens);
    }

    [Theory]
    [InlineData(61_600_000)]
    [InlineData(52_200_000)]
    public void Remote_Codex_zero_cache_without_field_provenance_is_unpriced_on_read(int input)
    {
        WriteTask(Summary(new TaskTokenCall
        {
            Ts = CodexAt, Model = "gpt-6-sol", InputTokens = input, OutputTokens = 111_000,
            InputIncludesCached = true, ModelPriced = true, EstimatedApiCostUsd = input == 61_600_000 ? 124m : 105m,
            ParticipantId = $"agent:remote-runner:{Attempt}:usage:turn:00000000:00",
        }));
        var entry = Assert.Single(new ProjectTokenReceiptReader().Read(_watchPath).Entries);
        Assert.Null(entry.TokenUsage!.InputIncludesCached);
        Assert.Equal(input, entry.TokenUsage.InputTokens);
        var estimate = Assert.Single(TokenTestServices.Pricing().Price([entry])).Cost!;
        Assert.False(estimate.ModelKnown);
        Assert.Equal(0m, estimate.Total);
        Assert.True(Assert.Single(ReadReceipt().Entries).InputIncludesCached); // Read-only marking.
    }

    // ---- write path -------------------------------------------------------

    [Fact]
    public void Claude_modelUsage_is_marked_as_a_session_cumulative_snapshot()
    {
        using var frame = JsonDocument.Parse(ResultFrame(turnInput: 54, session: "s-1"));

        var usage = Assert.Single(new ClaudeUsageParser().ParseAll(frame.RootElement, "claude-opus-5", new CliModelRegistry()));

        Assert.Equal("claude-session:s-1", usage.CumulativeScope);
        Assert.Equal(SessionCacheRead, usage.CacheRead);
    }

    [Fact]
    public void Claude_modelUsage_without_session_id_has_no_cumulative_scope()
    {
        using var frame = JsonDocument.Parse(ResultFrame(turnInput: 54, session: "s-1")
            .Replace("\"session_id\":\"s-1\",", string.Empty, StringComparison.Ordinal));

        var usage = Assert.Single(new ClaudeUsageParser().ParseAll(frame.RootElement, "claude-opus-5", new CliModelRegistry()));

        Assert.Null(usage.CumulativeScope);
    }

    [Fact]
    public void Remote_receipt_from_the_AGT3004_log_records_the_session_once_and_replay_is_a_noop()
    {
        var (scanner, mutations, receipts) = BuildWriteServices();
        WriteTask(tokenSummary: null);
        WriteCliLog(Enumerable.Range(0, Copies).Select(index => ResultFrame(turnInput: 10 + index, session: "b2cb04c3")));
        var task = scanner.FindJob(JobId, _watchPath)!;

        var first = receipts.PersistFromLog(task, Attempt, "agent-runner-01");

        Assert.True(first.Persisted);
        Assert.Equal(SessionTotal, first.TotalTokens);
        var receipt = ReadReceipt();
        var call = Assert.Single(receipt.Entries);
        Assert.Equal(SessionCacheRead, call.CacheReadTokens);
        Assert.Equal(SessionTotal, receipt.TotalTokens);
        var totalsBefore = ProjectTotals();

        // A completion retry of the same attempt and a re-attach that reports
        // the same log under a new attempt id must leave the ledger unchanged.
        var bytesBefore = File.ReadAllText(TaskJsonPath());
        receipts.PersistFromLog(scanner.FindJob(JobId, _watchPath)!, Attempt, "agent-runner-01");
        Assert.Equal(bytesBefore, File.ReadAllText(TaskJsonPath()));
        receipts.PersistFromLog(scanner.FindJob(JobId, _watchPath)!, LaterAttempt, "agent-runner-01");
        Assert.Equal(bytesBefore, File.ReadAllText(TaskJsonPath()));
        Assert.Equal(totalsBefore, ProjectTotals());
        Assert.NotNull(mutations);
    }

    [Theory]
    [InlineData(null, "fresh")]
    [InlineData("prior-session", "resumed")]
    public void Remote_receipt_preserves_launch_mode_through_the_merged_ledger(
        string? inputSessionId, string mode)
    {
        var (scanner, _, receipts) = BuildWriteServices();
        WriteTask(tokenSummary: null);
        WriteCliLog([ResultFrame(turnInput: 10, session: "s-1")]);
        var task = scanner.FindJob(JobId, _watchPath)!;
        var sessions = new TaskSessionLog(scanner, NullLogger<TaskSessionLog>.Instance);
        sessions.AppendSessionEvent(JobId, new SessionEvent
        {
            Ts = SessionAt.AddSeconds(-1), RunAttemptId = Attempt, Cli = "claude",
        }, _watchPath);
        sessions.AppendSessionEvent(JobId, new SessionEvent
        {
            Ts = SessionAt.AddMinutes(10), RunAttemptId = LaterAttempt,
            CapturedSessionId = "newer-session", Cli = "claude",
        }, _watchPath);
        var continuation = new SessionContinuationLedgerEntry(
            Attempt, JobId, "claude", "repo", "/work", "branch",
            "result", new string('a', 40), "host|home", inputSessionId,
            inputSessionId ?? "fresh-session", mode, null, inputSessionId is not null,
            inputSessionId is null ? 0 : 1, null, null, null, null, 1, SessionAt);

        Assert.True(receipts.PersistFromLog(task, Attempt, "agent-runner-01",
            sessionContinuation: continuation).Persisted);

        Assert.Equal(mode, Assert.Single(ReadReceipt().Entries).SessionMode);
        var ledger = Assert.Single(new ProjectTokenReceiptReader().Read(_watchPath).Entries);
        Assert.Equal(mode, ledger.TokenUsage!.SessionMode);
        Assert.Equal(mode, Assert.Single(TaskTokenSummaryFold.PerJob(
            TokenTestServices.Pricing().Price([ledger]))[JobId].Entries).SessionMode);
        var events = sessions.ReadSessionEvents(JobId, _watchPath);
        var completed = Assert.Single(events, entry => entry.RunAttemptId == Attempt);
        Assert.Equal(inputSessionId is not null, completed.Resumed);
        Assert.Equal(inputSessionId, completed.InputSessionId);
        Assert.Equal("newer-session", Assert.Single(events, entry => entry.RunAttemptId == LaterAttempt).CapturedSessionId);
    }

    [Fact]
    public void Receipt_store_appends_new_usage_and_ignores_usage_already_recorded_by_another_attempt()
    {
        var (_, mutations, _) = BuildWriteServices();
        WriteTask(tokenSummary: null);
        var folder = Path.GetDirectoryName(TaskJsonPath())!;
        var session = SessionCall($"agent:remote-runner:{Attempt}:usage:scope:session-a");
        var repeatedSession = session with { ParticipantId = $"agent:remote-runner:{LaterAttempt}:usage:scope:session-a" };
        var codex = CodexCall($"agent:remote-runner:{LaterAttempt}:usage:turn:00000001:00");

        Assert.True(mutations.SetRemoteTokenSummaryOnFolder(folder, Attempt, Summary(session)));
        Assert.True(mutations.SetRemoteTokenSummaryOnFolder(folder, LaterAttempt, Summary(repeatedSession, repeatedSession, codex)));

        var receipt = ReadReceipt();
        Assert.Equal(2, receipt.Calls);
        Assert.Equal(SessionTotal + CodexTotal, receipt.TotalTokens);
        Assert.Equal(session.ParticipantId, receipt.Entries[0].ParticipantId);
        Assert.Equal(codex.ParticipantId, receipt.Entries[1].ParticipantId);

        var bytes = File.ReadAllText(TaskJsonPath());
        Assert.True(mutations.SetRemoteTokenSummaryOnFolder(folder, LaterAttempt, Summary(repeatedSession, codex)));
        Assert.Equal(bytes, File.ReadAllText(TaskJsonPath()));
    }

    [Fact]
    public void Command_counters_survive_completion_replay_and_appear_in_card_usage_without_token_frames()
    {
        var (scanner, _, receipts) = BuildWriteServices();
        WriteTask(tokenSummary: null);
        var folder = Path.GetDirectoryName(TaskJsonPath())!;
        var log = TaskPaths.CliOutputLog(folder);
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        var first = new StatusCommandSummary(10, 3, 3);
        var second = new StatusCommandSummary(20, 4, 5);
        var timestamp = SessionAt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllLines(log,
        [
            $"[{timestamp}] [system] {new StatusCommandRunSummary(Attempt, first).ToLogLine()}",
            $"[{timestamp}] [system] {new StatusCommandRunSummary(LaterAttempt, second).ToLogLine()}",
        ]);

        var task = scanner.FindJob(JobId, _watchPath)!;
        Assert.Equal(first, receipts.PersistFromLog(task, Attempt, "host").StatusCommands);
        Assert.Equal(second, receipts.PersistFromLog(task, LaterAttempt, "host").StatusCommands);
        var beforeReplay = File.ReadAllText(TaskJsonPath());
        receipts.PersistFromLog(task, Attempt, "host");
        Assert.Equal(beforeReplay, File.ReadAllText(TaskJsonPath()));

        var summary = BuildProjectReader().WorkspacePerJob(ProjectName, _watchPath)[JobId];
        Assert.Equal(0, summary.TotalTokens);
        Assert.Equal(first, summary.StatusCommandsByRun![Attempt]);
        Assert.Equal(second, summary.StatusCommandsByRun[LaterAttempt]);
        Assert.Equal(new StatusCommandSummary(30, 7, 8), summary.StatusCommands);
        Assert.Equal(8d / 30, summary.StatusCommands!.RepeatedStatusShare);
    }

    [Fact]
    public void Receipt_store_preserves_distinct_equal_turns_and_replay_is_a_noop()
    {
        var (_, mutations, _) = BuildWriteServices();
        WriteTask(tokenSummary: null);
        var folder = Path.GetDirectoryName(TaskJsonPath())!;
        var first = SessionCall($"agent:remote-runner:{Attempt}:usage:turn:00000001:00");
        var second = first with { ParticipantId = $"agent:remote-runner:{Attempt}:usage:turn:00000002:00" };

        Assert.True(mutations.SetRemoteTokenSummaryOnFolder(folder, Attempt, Summary(first, second)));
        Assert.Equal(2, ReadReceipt().Calls);
        var bytes = File.ReadAllText(TaskJsonPath());
        Assert.True(mutations.SetRemoteTokenSummaryOnFolder(folder, Attempt, Summary(first, second)));
        Assert.Equal(bytes, File.ReadAllText(TaskJsonPath()));

        var reattached = first with { ParticipantId = $"agent:remote-runner:{LaterAttempt}:usage:turn:00000001:00" };
        Assert.True(mutations.SetRemoteTokenSummaryOnFolder(folder, LaterAttempt, Summary(reattached)));
        Assert.Equal(bytes, File.ReadAllText(TaskJsonPath()));

        var third = first with { ParticipantId = $"agent:remote-runner:{LaterAttempt}:usage:turn:00000003:00" };
        Assert.True(mutations.SetRemoteTokenSummaryOnFolder(folder, LaterAttempt, Summary(reattached, third)));
        Assert.Equal(3, ReadReceipt().Calls);
        Assert.Equal(3 * SessionTotal, ReadReceipt().TotalTokens);
        var read = new ProjectTokenReceiptReader().Read(_watchPath);
        Assert.Equal(3, read.Entries.Count);
        Assert.Equal(0, read.CollapsedDuplicates.Entries);
        Assert.Equal(3, BuildProjectReader().LifetimeSummary(ProjectName, _watchPath).OrchestratorLlmCalls);
    }

    [Fact]
    public void Remote_receipt_preserves_equal_modelUsage_frames_without_session_id()
    {
        var (scanner, _, receipts) = BuildWriteServices();
        WriteTask(tokenSummary: null);
        var frame = ResultFrame(turnInput: 54, session: "s-1")
            .Replace("\"session_id\":\"s-1\",", string.Empty, StringComparison.Ordinal);
        WriteCliLog([frame, frame]);

        var task = scanner.FindJob(JobId, _watchPath)!;
        Assert.True(receipts.PersistFromLog(task, Attempt, "agent-runner-01").Persisted);
        var receipt = ReadReceipt();
        Assert.Equal(2, receipt.Calls);
        Assert.Equal(2 * SessionTotal, receipt.TotalTokens);
        Assert.NotEqual(receipt.Entries[0].ParticipantId, receipt.Entries[1].ParticipantId);

        var bytes = File.ReadAllText(TaskJsonPath());
        Assert.True(receipts.PersistFromLog(task, Attempt, "agent-runner-01").Persisted);
        Assert.Equal(bytes, File.ReadAllText(TaskJsonPath()));
    }

    [Fact]
    public void Reattach_keeps_turn_identity_when_its_attempt_window_widens()
    {
        var (scanner, _, receipts) = BuildWriteServices();
        WriteTask(tokenSummary: null);
        var frame = ResultFrame(turnInput: 54, session: "s-1")
            .Replace("\"session_id\":\"s-1\",", string.Empty, StringComparison.Ordinal);
        var folder = Path.GetDirectoryName(TaskJsonPath())!;
        var log = TaskPaths.CliOutputLog(folder);
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllLines(log,
        [
            $"[13:29:40.000] [stdout] {frame}",
            $"[13:29:54.050] [stdout] {frame}",
        ]);
        File.SetLastWriteTimeUtc(log, SessionAt);
        var eventsPath = TaskPaths.SessionEventsLog(folder);
        void WriteEvents()
        {
            File.WriteAllLines(eventsPath,
            [
                JsonSerializer.Serialize(new SessionEvent
                {
                    Ts = SessionAt.AddSeconds(-1),
                    FinishedAt = SessionAt.AddSeconds(1),
                    RunAttemptId = Attempt,
                }),
                JsonSerializer.Serialize(new SessionEvent
                {
                    Ts = SessionAt.AddSeconds(-16),
                    FinishedAt = SessionAt.AddSeconds(1),
                    RunAttemptId = LaterAttempt,
                }),
            ]);
        }
        WriteEvents();
        var task = scanner.FindJob(JobId, _watchPath)!;

        Assert.True(receipts.PersistFromLog(task, Attempt, "agent-runner-01").Persisted);
        var first = Assert.Single(ReadReceipt().Entries);
        Assert.EndsWith(":usage:turn:00000001:00", first.ParticipantId);

        Assert.True(receipts.PersistFromLog(task, LaterAttempt, "agent-runner-01").Persisted);
        var receipt = ReadReceipt();
        Assert.Equal(2, receipt.Calls);
        Assert.Equal(2 * SessionTotal, receipt.TotalTokens);
    }

    [Fact]
    public void Per_turn_usages_are_kept_and_only_the_last_cumulative_snapshot_counts()
    {
        var at = SessionAt;
        var turn = new ParsedTurnUsage("gpt-6-sol", 10, 1, 0, 0, null, null);
        var early = new ParsedTurnUsage("claude-opus-5", 1, 1, 100, 0, null, null, CumulativeScope: "claude-session:a");
        var late = early with { CacheRead = 300 };
        var other = early with { CumulativeScope = "claude-session:b", CacheRead = 50 };

        var kept = RemoteTokenReceiptService.LatestCumulativeSnapshots(
        [
            new(at, turn, "turn:1"),
            new(at, early, "scope:a"),
            new(at.AddSeconds(1), turn, "turn:2"),
            new(at.AddSeconds(2), late, "scope:a"),
            new(at, other, "scope:b"),
        ]);

        Assert.Equal(4, kept.Count);
        Assert.Equal(2, kept.Count(item => item.Usage.CumulativeScope is null));
        Assert.Contains(kept, item => item.Usage.CacheRead == 300);
        Assert.DoesNotContain(kept, item => item.Usage.CacheRead == 100);
        Assert.Contains(kept, item => item.Usage.CacheRead == 50);
    }

    // ---- readers ----------------------------------------------------------

    [Fact]
    public void Receipt_reader_collapses_the_AGT3004_pattern()
    {
        WriteTask(Agt3004Receipt());

        var read = new ProjectTokenReceiptReader().Read(_watchPath);

        Assert.Equal(2, read.Entries.Count);
        Assert.Equal(Copies - 1, read.CollapsedDuplicates.Entries);
        Assert.Equal((Copies - 1) * SessionTotal, read.CollapsedDuplicates.Tokens);
        var summary = read.Summaries[JobId];
        Assert.Equal(2, summary.Calls);
        Assert.Equal(SessionTotal + CodexTotal, summary.TotalTokens);
        Assert.Equal(SessionTotal + CodexTotal, read.Entries.Sum(Total));
    }

    [Fact]
    public void Project_usage_readers_report_the_real_AGT3004_usage()
    {
        WriteTask(Agt3004Receipt());
        var reader = BuildProjectReader();

        var expensive = Assert.Single(reader.ProjectExpensiveJobs(ProjectName, _watchPath, 10));
        Assert.Equal(SessionTotal + CodexTotal, expensive.TotalTokens);
        Assert.Equal(2, expensive.Calls);

        var summary = reader.ProjectSummary(ProjectName, _watchPath, CodexAt.AddDays(1));
        Assert.Equal(SessionTotal + CodexTotal, summary.LifetimeTotalTokens);
        Assert.Equal(2, summary.LifetimeCalls);

        var detail = reader.ProjectJobDetail(ProjectName, _watchPath, JobId)!;
        Assert.Equal(2, detail.Calls);
        Assert.Equal(SessionTotal + CodexTotal, detail.TotalTokens);

        // runner/{project}/token-summary
        var lifetime = reader.LifetimeSummary(ProjectName, _watchPath);
        Assert.Equal(2, lifetime.OrchestratorLlmCalls);
        Assert.Equal(SessionCacheRead + 5_901_696, lifetime.TotalCacheReadTokens);
    }

    [Fact]
    public void Pipeline_cost_day_of_the_AGT3004_session_counts_it_once()
    {
        WriteTask(Agt3004Receipt());
        var (scanner, _, _) = BuildWriteServices();
        var service = BuildPipelineCost(scanner);

        var timeline = service.Build(ProjectName, _watchPath, 30, CodexAt.AddDays(1));

        var day = timeline.DayCosts.Single(cell => cell.Day == "2026-09-29");
        Assert.Equal(SessionTotal, day.TotalTokens);
        var once = TokenTestServices.Pricing().Estimate("claude-opus-5", SessionInput, SessionOutput, SessionCacheRead, SessionCacheWrite, SessionAt);
        Assert.Equal(decimal.Round(once.Total, 2), decimal.Round(day.CostUsd, 2));
    }

    [Fact]
    public async Task Bus_readers_collapse_identical_token_usage_messages()
    {
        var store = new AgentMessageBusStore();
        for (var copy = 0; copy < Copies; copy++)
            await store.AppendAsync(_workspace, BusMessage($"duplicate-{copy:D2}", "run-1", SessionAt, SessionCacheRead));
        await store.AppendAsync(_workspace, BusMessage("distinct", "run-1", CodexAt, 5_901_696));
        // Same instant and counts from a different run is a different call.
        await store.AppendAsync(_workspace, BusMessage("other-run", "run-2", SessionAt, SessionCacheRead));

        var entries = BusTokenEntryConverter.LoadTokenUsageEntries(store, _workspace, ProjectName);
        Assert.Equal(3, entries.Count);

        var projectReader = BuildProjectReader(store);
        var summary = projectReader.LifetimeSummary(ProjectName, _watchPath);
        Assert.Equal(3, summary.OrchestratorLlmCalls);
        Assert.Equal(2 * SessionCacheRead + 5_901_696, summary.TotalCacheReadTokens);

        var row = Assert.Single(projectReader.ProjectExpensiveJobs(ProjectName, _watchPath, 10));
        Assert.Equal(3, row.Calls);
    }

    [Fact]
    public void Collapse_returns_the_same_list_when_there_are_no_duplicates()
    {
        var messages = new List<AgentMessage>
        {
            BusMessage("a", "run-1", SessionAt, 1),
            BusMessage("b", "run-1", SessionAt, 2),
        };

        var kept = TokenLedgerDuplicates.CollapseMessages(messages, out var collapsed);

        Assert.Same(messages, kept);
        Assert.Equal(TokenLedgerCollapseCount.None, collapsed);
    }

    // ---- AGT-2882 Codex cache correction ----------------------------------

    [Fact]
    public void Legacy_codex_receipt_is_normalized_before_pricing_in_every_receipt_reader()
    {
        var legacy = new TaskTokenCall
        {
            Ts = CodexAt,
            Model = "gpt-5.6-sol",
            ParticipantId = "agent:remote-runner:run_1",
            InputTokens = 1_000,
            OutputTokens = 100,
            CacheReadTokens = 400,
        };
        WriteTask(Summary(legacy));

        var entry = Assert.Single(new ProjectTokenReceiptReader().Read(_watchPath).Entries);
        Assert.Equal(600, entry.TokenUsage!.InputTokens);
        Assert.Equal(400, entry.TokenUsage.CacheReadTokens);
        Assert.True(entry.TokenUsage.InputIncludesCached);

        var expected = TokenTestServices.Pricing().Estimate("gpt-5.6-sol", 600, 100, 400, 0, CodexAt);
        Assert.True(expected.ModelKnown);
        var lifetime = BuildProjectReader().LifetimeSummary(ProjectName, _watchPath);
        Assert.Equal(600, lifetime.TotalInputTokens);
        Assert.Equal(expected.Total, lifetime.EstimatedApiCostUsd);

        var (scanner, _, _) = BuildWriteServices();
        var timeline = BuildPipelineCost(scanner)
            .Build(ProjectName, _watchPath, 30, CodexAt.AddDays(1));
        Assert.Equal(1_100, timeline.TotalTokens);
        Assert.Equal(decimal.Round(expected.Total, 2), decimal.Round(timeline.TotalCostUsd, 2));
    }

    [Fact]
    public async Task Legacy_codex_bus_row_is_normalized_before_pricing_in_the_project_reader()
    {
        var store = new AgentMessageBusStore();
        await store.AppendAsync(_workspace, new AgentMessage
        {
            Id = "codex-legacy",
            CreatedAt = CodexAt,
            ParticipantId = "agent:codex",
            Role = "evidence",
            Kind = "token-usage",
            Project = ProjectName,
            JobId = JobId,
            RunId = "run-1",
            Tokens = new AgentMessageTokens(1_000, 100, CacheRead: 400, Model: "gpt-5.6-sol"),
        });

        var lifetime = BuildProjectReader(store).LifetimeSummary(ProjectName, _watchPath);

        Assert.Equal(600, lifetime.TotalInputTokens);
        Assert.Equal(400, lifetime.TotalCacheReadTokens);
        Assert.Equal(TokenTestServices.Pricing().Estimate("gpt-5.6-sol", 600, 100, 400, 0, CodexAt).Total, lifetime.EstimatedApiCostUsd);
    }

    // ---- fixtures ---------------------------------------------------------

    private static long Total(OrchestratorLogEntry entry)
        => (long)entry.TokenUsage!.InputTokens + entry.TokenUsage.OutputTokens
           + entry.TokenUsage.CacheReadTokens + entry.TokenUsage.CacheCreationTokens;

    private static TaskTokenCall SessionCall(string? participant) => new()
    {
        Ts = SessionAt,
        Model = "claude-opus-5",
        ParticipantId = participant,
        InputTokens = SessionInput,
        OutputTokens = SessionOutput,
        CacheReadTokens = SessionCacheRead,
        CacheCreationTokens = SessionCacheWrite,
        ModelPriced = true,
    };

    private static TaskTokenCall CodexCall(string? participant) => new()
    {
        Ts = CodexAt,
        Model = "gpt-6-sol",
        ParticipantId = participant,
        InputTokens = 195_722,
        OutputTokens = 31_599,
        CacheReadTokens = 5_901_696,
        InputIncludesCached = true,
    };

    private static TaskTokenSummary Summary(params TaskTokenCall[] calls) => new()
    {
        Calls = calls.Length,
        InputTokens = calls.Sum(call => call.InputTokens),
        OutputTokens = calls.Sum(call => call.OutputTokens),
        CacheReadTokens = calls.Sum(call => call.CacheReadTokens),
        CacheCreationTokens = calls.Sum(call => call.CacheCreationTokens),
        TotalTokens = calls.Sum(TokenLedgerDuplicates.TotalTokens),
        LastUpdate = calls.Max(call => call.Ts),
        Entries = calls.ToList(),
    };

    /// <summary>22 identical session rows plus one distinct call, as persisted for AGT-3004.</summary>
    private static TaskTokenSummary Agt3004Summary()
        => Summary(Enumerable.Repeat(SessionCall($"agent:remote-runner:{Attempt}"), Copies)
            .Append(CodexCall($"agent:remote-runner:{LaterAttempt}"))
            .ToArray());

    private static object Agt3004Receipt() => Agt3004Summary();

    private static string ResultFrame(long turnInput, string session)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "result",
            ["subtype"] = "success",
            ["session_id"] = session,
            ["total_cost_usd"] = 94.86,
            ["usage"] = new { input_tokens = turnInput, output_tokens = 33, cache_read_input_tokens = 86_854 },
            ["modelUsage"] = new Dictionary<string, object>
            {
                ["claude-opus-5"] = new
                {
                    inputTokens = SessionInput,
                    outputTokens = SessionOutput,
                    cacheReadInputTokens = SessionCacheRead,
                    cacheCreationInputTokens = SessionCacheWrite,
                },
            },
        });

    private static AgentMessage BusMessage(string id, string runId, DateTime at, long cacheRead) => new()
    {
        Id = id,
        CreatedAt = at,
        ParticipantId = "agent:claude",
        Role = "evidence",
        Kind = "token-usage",
        Project = ProjectName,
        JobId = JobId,
        RunId = runId,
        Tokens = new AgentMessageTokens(SessionInput, SessionOutput, CacheRead: cacheRead, CacheWrite: SessionCacheWrite, Model: "claude-opus-5"),
    };

    private string TaskJsonPath() => Path.Combine(_watchPath, "6-completed", JobId, "task.json");

    private void WriteTask(object? tokenSummary)
    {
        var dir = Path.GetDirectoryName(TaskJsonPath())!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(TaskJsonPath(), JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = JobId,
            ["title"] = "Documentation drift audit",
            ["state"] = "6-completed",
            ["order"] = 1,
            ["agent"] = "claude",
            ["cliType"] = "claude",
            ["model"] = "claude-opus-5",
            ["tokenSummary"] = tokenSummary,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private void WriteCliLog(IEnumerable<string> frames)
    {
        var path = TaskPaths.CliOutputLog(Path.GetDirectoryName(TaskJsonPath())!);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Every result frame was flushed in the same millisecond.
        File.WriteAllLines(path, frames.Select(frame => $"[13:29:54.050] [stdout] {frame}"));
        File.SetLastWriteTimeUtc(path, SessionAt);
    }

    private TaskTokenSummary ReadReceipt()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(TaskJsonPath()));
        return document.RootElement.GetProperty("tokenSummary")
            .Deserialize<TaskTokenSummary>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private (long Tokens, int Calls) ProjectTotals()
    {
        var summary = BuildProjectReader().LifetimeSummary(ProjectName, _watchPath);
        return (summary.TotalInputTokens + summary.TotalOutputTokens + summary.TotalCacheReadTokens
                + summary.TotalCacheCreationTokens, summary.OrchestratorLlmCalls);
    }

    private IConfiguration BuildConfig()
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["WatchPaths:0:Name"] = ProjectName,
                ["WatchPaths:0:Path"] = _watchPath,
            })
            .Build();

    private ProjectPipelineCostService BuildPipelineCost(TaskScannerService scanner)
    {
        var config = BuildConfig();
        var pricing = TokenTestServices.Pricing();
        var store = new AgentMessageBusStore();
        var analysis = new TokenAnalysisService(pricing, scanner,
            new PipelineExecutionLog(pricing, NullLogger<PipelineExecutionLog>.Instance),
            new ProjectTokenLedger(store, config, new ProjectTokenReceiptReader()), store, config,
            new FakeTimeProvider(new DateTimeOffset(CodexAt.AddDays(1))));
        return new ProjectPipelineCostService(pricing, analysis);
    }

    private TokenAggregationService BuildProjectReader(AgentMessageBusStore? store = null)
    {
        var config = BuildConfig();
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var stats = new JobStatsMetadataCache(scanner, config, NullLogger<JobStatsMetadataCache>.Instance);
        return TokenTestServices.Aggregator(
            store ?? new AgentMessageBusStore(), config, stats, new ProjectTokenReceiptReader());
    }

    private (TaskScannerService Scanner, TaskMutationService Mutations, RemoteTokenReceiptService Receipts) BuildWriteServices()
    {
        var config = BuildConfig();
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        var receipts = new RemoteTokenReceiptService(TokenTestServices.Pricing(),
            new CliUsageParserRegistry([new ClaudeUsageParser(), new CodexUsageParser()]),
            new CliModelRegistry(),
            mutations,
            new TaskSessionLog(scanner, NullLogger<TaskSessionLog>.Instance),
            NullLogger<RemoteTokenReceiptService>.Instance);
        return (scanner, mutations, receipts);
    }

}
