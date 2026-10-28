using System.Text.RegularExpressions;
using Xunit;

namespace AgentStudio.Tests.Architecture;

/// <summary>
/// Every backend Git process goes through <c>GitProcessBoundary</c> (AGT-3321,
/// Git facts S1), the one place that applies the read-request guard, registers
/// the child for the stale-lock guard and records <c>GitProcessTelemetry</c>.
/// Before it, six call sites started Git with their own <c>Process.Start</c>,
/// so their spawns were invisible to telemetry and no guard could stop them on
/// a read request.
/// </summary>
[Trait("Category", "Guard")]
public sealed partial class GitProcessBoundaryGuardTests
{
    private const string Boundary = "backend/Features/Git/GitProcessBoundary.cs";
    private const string Runner = "backend/Features/Git/GitNetworkProcessRunner.cs";

    /// <summary>
    /// Files that start a non-Git tool themselves next to a Git start. Their
    /// Git start must still pass the boundary; the reason is the review record.
    /// </summary>
    private static readonly Dictionary<string, string> OwnsProcessForOtherTools = new(StringComparer.Ordinal)
    {
        ["backend/Features/Pipeline/RemoteGateTransport.cs"] =
            "RemoteGateProcess streams ssh/scp and Git lines; its Git start opens GitProcessBoundary.Begin.",
        ["backend/Features/Pipeline/BuildTestGateRunner.cs"] =
            "Starts the project's build and test commands; its Git calls use GitProcessBoundary.RunAsync.",
    };

    /// <summary>Files that may call the bounded runner directly for a tool that is not Git.</summary>
    private static readonly Dictionary<string, string> RunsNonGitToolsThroughRunner = new(StringComparer.Ordinal)
    {
        [Boundary] = "The boundary itself.",
        ["backend/Features/Publishing/PublishActionService.cs"] =
            "gh borrows the bounded runner; its Git calls choose GitProcessBoundary.Run.",
    };

    [Fact]
    public void Every_backend_git_start_passes_the_boundary()
    {
        var violations = new List<string>();
        foreach (var (file, code) in BackendSources())
        {
            var launches = GitLaunch().Matches(code).Count;
            if (launches == 0) continue;
            if (!code.Contains("GitProcessBoundary.", StringComparison.Ordinal))
                violations.Add($"{file}: {launches} Git start(s) without GitProcessBoundary");
            if (file != Runner && !OwnsProcessForOtherTools.ContainsKey(file) && ProcessStart().IsMatch(code))
                violations.Add($"{file}: starts a process itself next to a Git start; use GitProcessBoundary.Run");
        }

        Assert.True(violations.Count == 0,
            "Start Git only through GitProcessBoundary.Run/RunAsync/RunBinary, or a GitProcessBoundary.Begin span "
            + "when the caller must own the process:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void Only_the_boundary_runs_records_or_registers_git_children()
    {
        var violations = new List<string>();
        foreach (var (file, code) in BackendSources())
        {
            if (RunnerCall().IsMatch(code) && !RunsNonGitToolsThroughRunner.ContainsKey(file))
                violations.Add($"{file}: calls GitNetworkProcessRunner directly");
            if (code.Contains("GitChildProcessRegistry.Track(", StringComparison.Ordinal) && file != Boundary)
                violations.Add($"{file}: registers a Git child outside the boundary");
            if (code.Contains("GitProcessTelemetry.Record(", StringComparison.Ordinal) && file != Boundary)
                violations.Add($"{file}: records a Git spawn outside the boundary");
            if (code.Contains("HostGitProcessHardening.Apply(", StringComparison.Ordinal) && file != Boundary && file != Runner)
                violations.Add($"{file}: hardens a Git start outside the boundary");
            if (code.Contains("GitProcessBoundary.Begin(", StringComparison.Ordinal) && !OwnsProcessForOtherTools.ContainsKey(file))
                violations.Add($"{file}: opens a boundary span; prefer GitProcessBoundary.Run unless the caller streams output");
        }

        Assert.True(violations.Count == 0,
            "The Git process boundary owns guard, registry, budget and telemetry:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void Guard_recognizes_a_direct_git_start()
    {
        const string source = """
            var start = new ProcessStartInfo("git") { CreateNoWindow = true };
            using var process = Process.Start(start);
            """;
        var code = CSharpSourceScanner.MaskComments(source);

        Assert.Single(GitLaunch().Matches(code));
        Assert.Matches(ProcessStart(), code);
        Assert.DoesNotContain("GitProcessBoundary.", code, StringComparison.Ordinal);
    }

    private static IEnumerable<(string File, string Code)> BackendSources()
    {
        var root = CSharpSourceScanner.RepoRoot();
        return CSharpSourceScanner.SourceFiles(root, "backend")
            .Select(path => (CSharpSourceScanner.Relative(root, path),
                CSharpSourceScanner.MaskComments(CSharpSourceScanner.Read(path))));
    }

    // RemoteGateProcess.RunAsync("git", ...) is not matched: that helper spans its Git starts.
    [GeneratedRegex("""FileName\s*=\s*"git"|new\s+ProcessStartInfo\(\s*"git"|(?<!RemoteGateProcess\.)RunAsync\(\s*"git"|Run\(\s*\w+\s*,\s*"git"\s*,""")]
    private static partial Regex GitLaunch();

    [GeneratedRegex(@"\bProcess\.Start\(|\.Start\(\)|\bnew\s+Process\b(?!StartInfo)")]
    private static partial Regex ProcessStart();

    [GeneratedRegex(@"GitNetworkProcessRunner\.(Run|RunAsync|RunBinary)\(")]
    private static partial Regex RunnerCall();
}
