using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Explicit per-CLI evidence (ASS-1739 / T1a) that the adapter contract
/// <see cref="GenericCliExecutionService.DescribeContextSources"/> resolves the
/// convention-derived execution context for the CLIs that emit no init frame
/// (Codex / Gemini), which inherit the base implementation verbatim. Claude's
/// richer init-frame path is covered by <c>ClaudeInitContextParserTests</c>, and
/// the pure filesystem convention builder by <c>CliContextConventionsTests</c>;
/// this class locks the <i>service wiring</i> between them: a tracked run -&gt;
/// the scalar header (model / cwd / <c>source=convention</c>) plus the right
/// per-CLI memory chain probed off the real working directory. Without this the
/// review can only see Claude + generic-convention coverage and not that Codex /
/// Gemini's own DescribeContextSources path produces honest sources.
/// </summary>
public sealed class DescribeContextSourcesTests : IDisposable
{
    private readonly List<string> _dirs = new();

    [Fact]
    public void Codex_DescribeContextSources_ReportsConventionHeaderAndProjectMemory()
    {
        var cwd = NewCwd("AGENTS.md");
        using var proc = new Process(); // unstarted: ProcInfo stores it without dereferencing.
        var svc = new StubCliService(Config(), "codex");
        svc.Seed("task-codex", proc, cwd, model: "gpt-5-codex");

        var ctx = svc.DescribeContextSources("task-codex");

        Assert.NotNull(ctx);
        Assert.Equal("codex", ctx!.Cli);
        Assert.Equal("convention", ctx.Source);
        Assert.Equal(cwd, ctx.Cwd);
        Assert.Equal("gpt-5-codex", ctx.Model);
        // The Codex adapter walks the AGENTS.md memory chain from the run's cwd.
        Assert.Contains(ctx.Sources, s =>
            s.Kind == CliContextSourceKinds.Memory &&
            s.Path == Path.Combine(cwd, "AGENTS.md") &&
            s.Exists == true);
    }

    [Fact]
    public void Gemini_DescribeContextSources_ReportsConventionHeaderAndProjectMemory()
    {
        var cwd = NewCwd("GEMINI.md");
        using var proc = new Process();
        var svc = new StubCliService(Config(), "gemini");
        svc.Seed("task-gemini", proc, cwd, model: "gemini-2.5-pro");

        var ctx = svc.DescribeContextSources("task-gemini");

        Assert.NotNull(ctx);
        Assert.Equal("gemini", ctx!.Cli);
        Assert.Equal("convention", ctx.Source);
        Assert.Equal(cwd, ctx.Cwd);
        Assert.Equal("gemini-2.5-pro", ctx.Model);
        // The Gemini adapter walks the GEMINI.md memory chain from the run's cwd.
        Assert.Contains(ctx.Sources, s =>
            s.Kind == CliContextSourceKinds.Memory &&
            s.Path == Path.Combine(cwd, "GEMINI.md") &&
            s.Exists == true);
    }

    [Fact]
    public void DescribeContextSources_UntrackedRun_ReturnsNull()
    {
        var svc = new StubCliService(Config(), "codex");
        Assert.Null(svc.DescribeContextSources("never-tracked"));
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private string NewCwd(string memoryFileName)
    {
        var dir = Path.Combine(Path.GetTempPath(), "atp-desc-ctx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, memoryFileName), "# project memory\n");
        _dirs.Add(dir);
        return dir;
    }

    private static IConfiguration Config() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = Path.GetTempPath() })
            .Build();

    public void Dispose()
    {
        foreach (var d in _dirs)
        {
            try { Directory.Delete(d, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Minimal concrete <see cref="GenericCliExecutionService"/> whose
    /// <see cref="GenericCliExecutionService.CliType"/> is parameterised, so the engine's
    /// DescribeContextSources runs through the Codex / Gemini convention branch.
    /// Spawn hooks throw because this path never starts a process.
    /// </summary>
    private sealed class StubCliService : GenericCliExecutionService
    {
        public StubCliService(IConfiguration config, string type)
            : base(BuildBehavior(type), NullLogger.Instance, config)
        {
        }

        private static CliBehavior BuildBehavior(string type) => new()
        {
            CliType = type,
            GetCliPath = _ => type,
        };

        /// <summary>Seed the in-memory live-process map so the run is "tracked" for DescribeContextSources.</summary>
        public void Seed(string jobKey, Process proc, string cwd, string? model)
        {
            var exec = new CliExecution
            {
                JobId = jobKey,
                TaskKey = jobKey,
                StartedAt = DateTime.UtcNow,
                Status = "running",
                Model = model,
            };
            _processes[jobKey] = new ProcInfo(proc, exec, cwd);
        }
    }
}
