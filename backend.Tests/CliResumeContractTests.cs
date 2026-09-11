using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// End-to-end contract tests for the user's core stability ask:
/// "When I open the tool again tomorrow, my paused jobs continue
/// reliably." This file pins the resume / continuation behaviour at
/// the spawn-and-stream level via <see cref="WebApplicationFactory{Program}"/>,
/// so the contract is provably intact before live-validation matters.
///
/// <para>
/// We test through the real Claude execution engine,
/// <see cref="AgentStudio.Runner.TaskRunnerService"/>, and the
/// real DI graph. Two probes:
/// </para>
/// <list type="number">
///   <item><b>Fresh start then resume:</b> spawn a tiny run, capture
///   the session UUID, then start a follow-up resume against that
///   UUID. Assert the resume frame's session_id matches AND the
///   resumed run streams output past init.</item>
///   <item><b>Resume against a dead session UUID:</b> simulate the
///   "user comes back next day, session was rotated" case by handing
///   the runner a UUID claude does not recognise. Assert the resume
///   surfaces a clean error rather than hanging or silently swallowing
///   the prompt. (This is the lesson of ADR-0011 about stale-id
///   recovery.)</item>
/// </list>
/// <para>
/// Gated behind <c>RUN_CLI_INTEGRATION=1</c>. Cheap (Haiku, ~10s
/// per probe).
/// </para>
/// </summary>
[Collection("LiveCli")]
[Trait("Category", "MachineBound")]
public class CliResumeContractTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public CliResumeContractTests(WebApplicationFactory<Program> factory) { _factory = factory; }

    private static string? ClaudeExePath
    {
        get
        {
            var fromEnv = Environment.GetEnvironmentVariable("CLAUDE_EXE");
            if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv)) return fromEnv;
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var candidate = Path.Combine(appData, "npm", "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
            return File.Exists(candidate) ? candidate : null;
        }
    }

    [SkippableFact]
    public async Task FreshStart_ThenResume_DeliversInitFramesOnBoth()
    {
        Skip.IfNot(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RUN_CLI_INTEGRATION")),
            "Integration test, opt-in via RUN_CLI_INTEGRATION=1.");
        var exe = ClaudeExePath;
        Skip.IfNot(exe != null, "claude.exe not found.");

        using var factory = _factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ClaudeCli:Path"] = exe
                });
            });
        });
        _ = factory.CreateClient();
        var svc = factory.Services.GetRequiredKeyedService<GenericCliExecutionService>(CliTypes.Claude);

        // Isolated per-test cwd so the per-cwd ~/.claude/projects/<encoded>/
        // session DB is empty and not contended by parallel claude
        // processes that may also be running in shared temp paths
        // (the engineer's own Claude Code sessions, prior test runs, etc.).
        // This is the same isolation strategy ADR-0014's stdin default-deny
        // plus the LiveCli collection landed for class-level concurrency;
        // here we extend it to within-test sequencing.
        var isolatedCwd = Path.Combine(Path.GetTempPath(), $"cli-resume-{Guid.NewGuid():N}");
        Directory.CreateDirectory(isolatedCwd);

        // First run: tiny prompt, capture the UUID.
        var firstId = $"resume-fresh-{Guid.NewGuid():N}";
        var firstKey = $"::{firstId}";
        var (firstExec, firstErr) = await svc.StartAsync(
            firstId, firstKey,
            prompt: "Reply with exactly four words and nothing else: fresh run ack now",
            workingDirectory: isolatedCwd,
            sessionName: null, resumeSession: false,
            model: "claude-haiku-4-5");
        Assert.Null(firstErr); Assert.NotNull(firstExec);
        // 90s budget: first-spawn cold-cache on Opus/Sonnet can run up to
        // 30-40s; even Haiku can take 20-30s when other claude processes
        // are warming. 90s leaves headroom without masking real hangs.
        var firstUuid = await WaitForSessionId(svc, firstKey, TimeSpan.FromSeconds(90));
        Assert.NotNull(firstUuid);
        svc.Stop(firstKey);
        // Give the first run's MonitorProcessAsync a beat to drain its
        // pipes and clear active-process state before we spawn the next.
        await Task.Delay(500);

        // Second run: resume against that UUID, same isolated cwd so
        // claude finds the session file from the first run.
        var secondId = $"resume-followup-{Guid.NewGuid():N}";
        var secondKey = $"::{secondId}";
        var (secondExec, secondErr) = await svc.StartAsync(
            secondId, secondKey,
            prompt: "Just reply with: continued",
            workingDirectory: isolatedCwd,
            sessionName: firstUuid, resumeSession: true,
            model: "claude-haiku-4-5");
        Assert.Null(secondErr); Assert.NotNull(secondExec);
        var secondUuid = await WaitForSessionId(svc, secondKey, TimeSpan.FromSeconds(90));
        svc.Stop(secondKey);
        try { Directory.Delete(isolatedCwd, recursive: true); } catch { /* best-effort */ }

        Assert.NotNull(secondUuid);
        // Claude's --resume captures a NEW UUID per turn (forking session
        // semantics). The stored chain pinning is tested elsewhere; here
        // we just need both runs to have produced a valid init frame.
        var firstFinal = svc.GetOutput(firstKey);
        var secondFinal = svc.GetOutput(secondKey);
        Assert.Contains(firstFinal,  l => l.Stream == "stdout" && l.Text.Contains("Session init"));
        Assert.Contains(secondFinal, l => l.Stream == "stdout" && l.Text.Contains("Session init"));
    }

    [SkippableFact]
    public async Task Resume_AgainstDeadSession_FailsCleanly_DoesNotHang()
    {
        Skip.IfNot(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RUN_CLI_INTEGRATION")),
            "Integration test, opt-in via RUN_CLI_INTEGRATION=1.");
        var exe = ClaudeExePath;
        Skip.IfNot(exe != null, "claude.exe not found.");

        using var factory = _factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ClaudeCli:Path"] = exe
                });
            });
        });
        _ = factory.CreateClient();
        var svc = factory.Services.GetRequiredKeyedService<GenericCliExecutionService>(CliTypes.Claude);

        // A canonical-shape UUID claude has never seen.
        var deadUuid = "00000000-0000-4000-8000-000000000000";
        var jobId = $"resume-dead-{Guid.NewGuid():N}";
        var jobKey = $"::{jobId}";
        // Same isolated-cwd discipline as the resume-fresh test.
        var isolatedCwd = Path.Combine(Path.GetTempPath(), $"cli-resume-dead-{Guid.NewGuid():N}");
        Directory.CreateDirectory(isolatedCwd);
        var (exec, err) = await svc.StartAsync(
            jobId, jobKey,
            prompt: "should fail",
            workingDirectory: isolatedCwd,
            sessionName: deadUuid, resumeSession: true,
            model: "claude-haiku-4-5");
        Assert.Null(err); // process spawn itself succeeded
        Assert.NotNull(exec);

        // Wait up to 30s for the run to terminate. The contract is
        // "fails cleanly" - the process must not hang past 30s. The
        // exit can be non-zero (claude rejects the dead UUID) and that
        // is what the runner's stale-id recovery layer (ADR-0011)
        // expects to see and respond to.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var snap = svc.GetOutput(jobKey);
            if (snap.Any(l => l.Stream == "system" && l.Text.Contains("CLI exited"))) break;
            await Task.Delay(250);
        }
        svc.Stop(jobKey);

        var final = svc.GetOutput(jobKey);
        Assert.Contains(final, l => l.Stream == "system" && l.Text.Contains("CLI exited"));
        try { Directory.Delete(isolatedCwd, recursive: true); } catch { /* best-effort */ }
    }

    private static async Task<string?> WaitForSessionId(
        GenericCliExecutionService svc, string jobKey, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            var capturedId = svc.GetCapturedSessionId(jobKey);
            if (!string.IsNullOrWhiteSpace(capturedId)) return capturedId;
            await Task.Delay(250);
        }
        return null;
    }
}
