using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the Codex behavior's session-UUID capture path. The
/// fixtures are real <c>codex exec --experimental-json</c> frame shapes; without this
/// regression the per-job session store stays empty and every follow-up
/// rebuilds context from disk via Recovery, discarding Codex's own
/// prompt-cache (see bug-codex-session-id-not-captured-from-thread-started).
/// </summary>
[Collection(CodexDetectedDefaultCollection.Name)]
public class CodexCliServiceTests : IDisposable
{
    public CodexCliServiceTests() => ModelMetadataRegistry.SetDetectedCodexDefault(null);

    public void Dispose() => ModelMetadataRegistry.SetDetectedCodexDefault(null);

    [Fact]
    public void CanResume_CleanContextRejectsEvenWhenSharedRolloutExists()
    {
        // A clean run never sees the operator's shared sessions/ dir, so a
        // rollout that exists only in the SHARED home proves nothing. Without
        // a live per-task clean home there is nothing to resume.
        var home = CreateCodexHomeWithRollout("019dee65-7a9b-7843-bfd9-06e555fff02b");
        try
        {
            Assert.False(CodexRolloutStore.CanResume(
                "019dee65-7a9b-7843-bfd9-06e555fff02b", "clean", home));
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [Fact]
    public void CanResume_CleanContextAcceptsRolloutInPersistentCleanHome()
    {
        // MKT-8 / WEB-14 session-stability fix: attempts of one task share one
        // clean home, so a resume is viable exactly when THAT home carries the
        // rollout a previous attempt wrote - and stays non-viable when it does
        // not (first attempt / evicted home).
        const string id = "019dee65-7a9b-7843-bfd9-06e555fff02b";
        var cleanHome = CreateCodexHomeWithRollout(id);
        try
        {
            Assert.True(CodexRolloutStore.CanResume(id, "clean", sharedHome: null, cleanHome: cleanHome));
            Assert.False(CodexRolloutStore.CanResume(
                "11111111-2222-4333-8444-555555555555", "clean", sharedHome: null, cleanHome: cleanHome));
            Assert.False(CodexRolloutStore.CanResume(id, "clean", sharedHome: null, cleanHome: null));
        }
        finally { Directory.Delete(cleanHome, recursive: true); }
    }

    [Fact]
    public void CanResume_SharedContextRequiresMatchingRollout()
    {
        var home = CreateCodexHomeWithRollout("019dee65-7a9b-7843-bfd9-06e555fff02b");
        try
        {
            Assert.True(CodexRolloutStore.CanResume(
                "019dee65-7a9b-7843-bfd9-06e555fff02b", "shared", home));
            Assert.False(CodexRolloutStore.CanResume(
                "11111111-2222-4333-8444-555555555555", "shared", home));
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [Fact]
    public void StillbornIndexEntry_IsPrunableOnlyAfterGrace_AndWithoutRollout()
    {
        const string id = "019dee65-7a9b-7843-bfd9-06e555fff02b";
        var now = DateTime.UtcNow;
        var old = $$"""{"id":"{{id}}","updated_at":"{{now.AddMinutes(-10):O}}"}""";
        var recent = $$"""{"id":"{{id}}","updated_at":"{{now.AddMinutes(-1):O}}"}""";

        Assert.True(SessionRegistry.TryReadStaleIndexOnlyCodexId(
            old, new HashSet<string>(), now, out var parsed));
        Assert.Equal(id, parsed);
        Assert.False(SessionRegistry.TryReadStaleIndexOnlyCodexId(
            recent, new HashSet<string>(), now, out _));
        Assert.False(SessionRegistry.TryReadStaleIndexOnlyCodexId(
            old, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id }, now, out _));
    }

    [Fact]
    public void StillbornCleanup_CompactsOldIndexOnlyRows_ButKeepsLiveAndRecentRows()
    {
        const string staleId = "019dee65-7a9b-7843-bfd9-06e555fff02b";
        const string recentId = "11111111-2222-4333-8444-555555555555";
        const string liveId = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";
        var now = DateTime.UtcNow;
        var dir = Path.Combine(Path.GetTempPath(), $"codex-index-cleanup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var index = Path.Combine(dir, "session_index.jsonl");
        File.WriteAllLines(index,
        [
            $$"""{"id":"{{staleId}}","updated_at":"{{now.AddMinutes(-10):O}}"}""",
            $$"""{"id":"{{recentId}}","updated_at":"{{now.AddMinutes(-1):O}}"}""",
            $$"""{"id":"{{liveId}}","updated_at":"{{now.AddMinutes(-10):O}}"}""",
            "not-json",
        ]);

        try
        {
            var registry = new SessionRegistry(NullLogger<SessionRegistry>.Instance, null!);
            registry.PruneStaleCodexIndexEntries(
                index,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { liveId },
                now);

            var kept = File.ReadAllLines(index);
            Assert.DoesNotContain(kept, line => line.Contains(staleId, StringComparison.Ordinal));
            Assert.Contains(kept, line => line.Contains(recentId, StringComparison.Ordinal));
            Assert.Contains(kept, line => line.Contains(liveId, StringComparison.Ordinal));
            Assert.Contains("not-json", kept);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void TryExtractSessionId_ThreadStartedFrame_ReturnsThreadId()
    {
        // codex-cli >= 0.128 (real frame shape).
        const string frame = """{"type":"thread.started","thread_id":"019dee65-7a9b-7843-bfd9-06e555fff02b"}""";
        Assert.Equal("019dee65-7a9b-7843-bfd9-06e555fff02b",
            BuiltInCliBehaviors.TryExtractSessionId(frame));
    }

    [Fact]
    public void TryExtractSessionId_LegacySessionMetaPayloadId_StillReturnsId()
    {
        // Older codex-cli builds wrapped the id under payload.id.
        const string frame = """{"type":"session_meta","payload":{"id":"019dee65-7a9b-7843-bfd9-06e555fff02b"}}""";
        Assert.Equal("019dee65-7a9b-7843-bfd9-06e555fff02b",
            BuiltInCliBehaviors.TryExtractSessionId(frame));
    }

    [Fact]
    public void TryExtractSessionId_LegacySessionMetaRootId_StillReturnsId()
    {
        // Some builds put the id at session_meta.session_id (root).
        const string frame = """{"type":"session_meta","session_id":"019dee65-7a9b-7843-bfd9-06e555fff02b"}""";
        Assert.Equal("019dee65-7a9b-7843-bfd9-06e555fff02b",
            BuiltInCliBehaviors.TryExtractSessionId(frame));
    }

    [Fact]
    public void TryExtractSessionId_NonUuidThreadId_ReturnsNull()
    {
        // Guard: a non-UUID would break `codex exec resume`, so reject it
        // here rather than persisting a value the CLI cannot consume.
        const string frame = """{"type":"thread.started","thread_id":"not-a-uuid"}""";
        Assert.Null(BuiltInCliBehaviors.TryExtractSessionId(frame));
    }

    [Fact]
    public void TryExtractSessionId_OtherFrameTypes_ReturnNull()
    {
        Assert.Null(BuiltInCliBehaviors.TryExtractSessionId(
            """{"type":"turn.started"}"""));
        Assert.Null(BuiltInCliBehaviors.TryExtractSessionId(
            """{"type":"item.completed","item":{"type":"agent_message","text":"hi"}}"""));
        Assert.Null(BuiltInCliBehaviors.TryExtractSessionId(
            """{"type":"turn.completed","usage":{"input_tokens":10}}"""));
    }

    [Fact]
    public void TryExtractSessionId_NonJsonOrEmpty_ReturnNull()
    {
        Assert.Null(BuiltInCliBehaviors.TryExtractSessionId(null));
        Assert.Null(BuiltInCliBehaviors.TryExtractSessionId(""));
        Assert.Null(BuiltInCliBehaviors.TryExtractSessionId("not-json"));
        Assert.Null(BuiltInCliBehaviors.TryExtractSessionId("[1,2,3]"));
    }

    [Fact]
    public void TryExtractSessionId_MalformedJson_ReturnsNullDoesNotThrow()
    {
        Assert.Null(BuiltInCliBehaviors.TryExtractSessionId(
            """{"type":"thread.started","thread_id":"""));
    }

    [Fact]
    public void BuildSystemPromptPrefix_NonWindows_HasSentinelHintOnly()
    {
        var prefix = BuiltInCliBehaviors.BuildSystemPromptPrefix(isWindows: false);

        Assert.Contains("[[TASK_DONE]]", prefix);
        Assert.Contains("[[TASK_BLOCKED:", prefix);
        Assert.Contains("[[TASK_NEEDS_INPUT:", prefix);
        Assert.Contains("[[TASK_NOOP]]", prefix);
        Assert.Contains("Replace the example reason", prefix);
        Assert.DoesNotContain("<reason>", prefix);
        Assert.Contains("Time-box investigation", prefix);
        Assert.DoesNotContain("windows", prefix, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CreateProcessAsUserW", prefix);
        Assert.EndsWith("\n\n", prefix);
    }

    [Fact]
    public void BuildSystemPromptPrefix_Windows_AppendsNoShellHint()
    {
        var prefix = BuiltInCliBehaviors.BuildSystemPromptPrefix(isWindows: true);

        Assert.Contains("[[TASK_DONE]]", prefix);
        Assert.Contains("windows sandbox: runner error", prefix);
        Assert.Contains("CreateProcessAsUserW failed", prefix);
        Assert.Contains("[[TASK_BLOCKED:windows-sandbox]]", prefix);
        Assert.EndsWith("\n\n", prefix);
    }

    [Fact]
    public void BuildSystemPromptPrefix_StaysShort()
    {
        // The prefix is paid on every invocation including resumes whose
        // user prompt is one sentence. Lock the upper bound so a future
        // edit can't bloat into a multi-paragraph essay.
        var win = BuiltInCliBehaviors.BuildSystemPromptPrefix(isWindows: true);
        var posix = BuiltInCliBehaviors.BuildSystemPromptPrefix(isWindows: false);

        Assert.True(win.Length < 1100, $"Windows prefix grew to {win.Length} chars");
        Assert.True(posix.Length < 700, $"Non-Windows prefix grew to {posix.Length} chars");
    }

    [Fact]
    public void ResolveInvocationModel_ReplacesForeignClaudeModelWithCodexDefault()
    {
        var cfg = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodexCli:Model"] = "gpt-5-codex"
            })
            .Build();

        Assert.Equal("gpt-5-codex",
            BuiltInCliBehaviors.ResolveInvocationModel("claude-opus-4-7", cfg));
    }

    [Fact]
    public void ResolveInvocationModel_PreservesExplicitCodexModel()
    {
        var cfg = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();

        Assert.Equal("gpt-5.5",
            BuiltInCliBehaviors.ResolveInvocationModel("gpt-5.5", cfg));
    }

    [Fact]
    public void ResolveInvocationModel_DefaultsEmptyModelToFallback()
    {
        var cfg = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();

        Assert.Equal(BuiltInCliBehaviors.CodexFallbackModel,
            BuiltInCliBehaviors.ResolveInvocationModel(null, cfg));
    }

    [Fact]
    public void StartedLine_UsesNormalizedCodexModel()
    {
        var cfg = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var model = BuiltInCliBehaviors.ResolveInvocationModel("claude-opus-4-7", cfg);

        var line = GenericCliExecutionService.BuildStartedLineText(
            CliTypes.Codex,
            processId: 1234,
            model,
            thinkingLevel: null,
            sessionName: null,
            resumeSession: false);

        Assert.Contains("model=gpt-5.5", line);
        Assert.DoesNotContain("claude-opus-4-7", line);
    }

    [Fact]
    public void TryExtractCommandExecution_ParsesCanonicalFrame()
    {
        // Real Codex frame shape from the 2026-05-12 Lotta-dashboard bug.
        const string frame = """{"type":"item.completed","item":{"id":"item66","type":"command_execution","command":"pwsh.exe -Command \".\\serve.ps1 status\"","aggregated_output":"ng serve laeuft nicht.\r\nFalse\r\n","exit_code":0,"status":"completed"}}""";
        var cap = BuiltInCliBehaviors.TryExtractCommandExecution(frame);
        Assert.NotNull(cap);
        Assert.Equal(0, cap!.Value.ExitCode);
        Assert.Contains("serve.ps1", cap.Value.Command);
        Assert.Contains("ng serve laeuft nicht", cap.Value.OutputTail);
    }

    [Fact]
    public void TryExtractCommandExecution_TailTruncatedForLongOutput()
    {
        var bigOutput = new string('y', 1000);
        var frame = "{\"type\":\"item.completed\",\"item\":{\"type\":\"command_execution\",\"command\":\"x\",\"exit_code\":0,\"aggregated_output\":\""
                  + bigOutput + "\"}}";
        var cap = BuiltInCliBehaviors.TryExtractCommandExecution(frame);
        Assert.NotNull(cap);
        Assert.True(cap!.Value.OutputTail!.Length <= 400);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("{\"type\":\"turn.started\"}")]
    [InlineData("{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"hi\"}}")]
    [InlineData("{\"type\":\"item.completed\",\"item\":{")]
    public void TryExtractCommandExecution_ReturnsNullForUnrelatedOrBrokenFrames(string? line)
    {
        Assert.Null(BuiltInCliBehaviors.TryExtractCommandExecution(line));
    }

    [Fact]
    public void IsCompatibleSessionName_AcceptsUuidsRejectsSlugs()
    {
        var svc = BuildService();

        Assert.True(svc.IsCompatibleSessionName("019dee65-7a9b-7843-bfd9-06e555fff02b"));
        Assert.False(svc.IsCompatibleSessionName(null));
        Assert.False(svc.IsCompatibleSessionName(""));
        Assert.False(svc.IsCompatibleSessionName("taskboard-fix-bug-202604282114"));
    }

    private static GenericCliExecutionService BuildService()
    {
        var cfg = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        return GenericCliExecutionService.ForCodex(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GenericCliExecutionService>.Instance,
            cfg,
            new AgentStudio.Cli.CodexModelDiscovery(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentStudio.Cli.CodexModelDiscovery>.Instance,
                cfg),
            new CliUsageParserRegistry(new ICliUsageParser[] { new CodexUsageParser() }),
            new CliModelRegistry());
    }

    private static string CreateCodexHomeWithRollout(string id)
    {
        var home = Path.Combine(Path.GetTempPath(), $"codex-rollout-test-{Guid.NewGuid():N}");
        var day = Path.Combine(home, "sessions", "2026", "07", "11");
        Directory.CreateDirectory(day);
        File.WriteAllText(Path.Combine(day, $"rollout-2026-07-11T18-41-00-{id}.jsonl"), "{}\n");
        return home;
    }
}
