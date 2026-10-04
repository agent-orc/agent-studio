using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStudio.TestSupport;

namespace AgentStudio.Tests;

internal sealed record PerfStats(
    int Iterations,
    double MinMs,
    double P50Ms,
    double P95Ms,
    double P99Ms,
    double MaxMs,
    double MeanMs)
{
    public static PerfStats From(IReadOnlyList<double> samples)
    {
        if (samples.Count == 0)
            return new PerfStats(0, 0, 0, 0, 0, 0, 0);
        var sorted = samples.OrderBy(x => x).ToArray();
        return new PerfStats(
            Iterations: samples.Count,
            MinMs: sorted[0],
            P50Ms: Quantile(sorted, 0.50),
            P95Ms: Quantile(sorted, 0.95),
            P99Ms: Quantile(sorted, 0.99),
            MaxMs: sorted[^1],
            MeanMs: sorted.Average());
    }

    private static double Quantile(double[] sorted, double q)
    {
        if (sorted.Length == 1) return sorted[0];
        var pos = q * (sorted.Length - 1);
        var lo = (int)Math.Floor(pos);
        var hi = (int)Math.Ceiling(pos);
        if (lo == hi) return sorted[lo];
        var frac = pos - lo;
        return sorted[lo] + (sorted[hi] - sorted[lo]) * frac;
    }
}

internal sealed record PerfMetric(
    string Name,
    string Fixture,
    int TaskCount,
    PerfStats Stats,
    string? Notes = null);

internal sealed record PerfReport(
    int SchemaVersion,
    DateTime GeneratedAt,
    string Scenario,
    string MachineOs,
    string MachineCpu,
    int LogicalCores,
    string GitHead,
    string GitBranch,
    List<PerfMetric> Backend,
    List<PerfMetric> Frontend);

internal static class PerfReportSink
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Writes the report JSON under &lt;repo&gt;/logs/perf/.
    /// Repo root is located by walking up from the test assembly until a
    /// folder containing AGENTS.md is found - works regardless of where
    /// xUnit decided to drop the test bin.
    /// </summary>
    public static string Write(string scenario, IEnumerable<PerfMetric> backend, IEnumerable<PerfMetric>? frontend = null)
    {
        var repoRoot = FindRepoRoot();
        var perfDir = Path.Combine(repoRoot, "logs", "perf");
        Directory.CreateDirectory(perfDir);

        var report = new PerfReport(
            SchemaVersion: 1,
            GeneratedAt: DateTime.UtcNow,
            Scenario: scenario,
            MachineOs: Environment.OSVersion.ToString(),
            MachineCpu: Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown",
            LogicalCores: Environment.ProcessorCount,
            GitHead: TryGitOutput("rev-parse HEAD", repoRoot) ?? "unknown",
            GitBranch: TryGitOutput("rev-parse --abbrev-ref HEAD", repoRoot) ?? "unknown",
            Backend: backend.ToList(),
            Frontend: (frontend ?? Array.Empty<PerfMetric>()).ToList());

        var stamp = report.GeneratedAt.ToString("yyyyMMdd-HHmmss");
        var path = Path.Combine(perfDir, $"backend-{scenario}-{stamp}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report, Opts));

        // Also write/update a "latest" pointer for the generator.
        var latest = Path.Combine(perfDir, $"backend-{scenario}-latest.json");
        File.WriteAllText(latest, JsonSerializer.Serialize(report, Opts));
        return path;
    }

    private static string FindRepoRoot()
        => RepositoryRoot.TryFind() ?? AppContext.BaseDirectory;

    private static string? TryGitOutput(string args, string cwd)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", args)
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(2000);
            return string.IsNullOrEmpty(output) ? null : output;
        }
        catch { return null; }
    }
}
