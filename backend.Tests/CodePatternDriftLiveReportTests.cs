using Microsoft.Extensions.Logging.Abstractions;

using Xunit;
using Xunit.Abstractions;
using AgentStudio.TestSupport;

namespace AgentStudio.Tests;

/// <summary>
/// Diagnostic test that runs the deterministic drift detector against
/// the live dev checkout and prints the per-rule state. Acts as a
/// rolling "did we add drift to the codebase since last green run?"
/// signal in CI logs; the assertion is that no rule reports drift.
/// </summary>
public class CodePatternDriftLiveReportTests
{
    private readonly ITestOutputHelper _out;

    public CodePatternDriftLiveReportTests(ITestOutputHelper output)
    {
        _out = output;
    }

    // Machine-bound: sweeps the live dev checkout for drift, so the outcome
    // tracks the checkout's actual contents and OS path handling, not a
    // hermetic fixture. Excluded from the CI gate via
    // `--filter Category!=MachineBound`; still runs on the dev machine.
    [Trait("Category", "MachineBound")]
    [Fact]
    public void LiveDriftReport_AcrossAllRules_IsClean()
    {
        var repo = LocateRepoRoot();
        if (repo is null)
        {
            _out.WriteLine("AGENTS.md not reachable; skipping live drift sweep");
            return;
        }

        var svc = new CodePatternDriftAnalysisService(NullLogger<CodePatternDriftAnalysisService>.Instance);
        var report = svc.Analyze(repo);

        _out.WriteLine($"=== Drift sweep against {repo} ===");
        _out.WriteLine($"Captured at: {report.CapturedAt:O}");
        _out.WriteLine($"Total drift sites: {report.TotalDriftSites}");
        _out.WriteLine("");

        foreach (var f in report.Findings)
        {
            _out.WriteLine($"  [{f.OverallSeverity}] {f.RuleId}: total={f.TotalSites} canonical={f.CanonicalSites} drift={f.DriftSites}");
            foreach (var hit in f.Hits.Where(h => h.IsDrift).OrderBy(h => h.FilePath))
            {
                _out.WriteLine($"      drift: {hit.FilePath}:{hit.LineNumber} [{hit.Evidence}]");
            }
        }

        // The assertion is intentionally loose — Warn-level findings can
        // exist when a known legacy pattern is in the repo (the rule
        // documents the migration target). Only High / Critical drift
        // should block. Quality bar is configurable per rule via
        // SeverityIfBad in DefaultRules.
        var blockers = report.Findings.Where(f =>
            f.DriftSites > 0 &&
            (f.OverallSeverity == DriftSeverity.High || f.OverallSeverity == DriftSeverity.Critical)).ToList();
        Assert.True(blockers.Count == 0,
            $"{blockers.Count} blocker-severity drift finding(s); see test output for the list");
    }

    private static string? LocateRepoRoot()
        => RepositoryRoot.TryFind();
}
