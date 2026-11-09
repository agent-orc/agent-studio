using System.Globalization;
using System.Text;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.TaskServer.Contracts.BatchGate;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: historical export dates only enter pure prior-day comparisons.
/// <summary>Offline outcome replay of the operator's 3 and 4 November formation export.</summary>
public sealed class BatchFormationReplayTests
{
    private sealed record Formation(DateTimeOffset At, string Id, int Size,
        int[] Members, int Phase, string Reason)
    {
        public bool Evaluable => Size >= 2 && Phase is 4 or 6 or 7;
        public bool Red => Phase == 7;
    }

    [Fact]
    public void Report_predicted_and_actual_red_rates_without_future_outcomes()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "batch-policy",
            "formations-2026-11-03-04.txt");
        var rows = File.ReadAllLines(path)
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(Parse).ToArray();
        Assert.Equal(115, rows.Length);
        Assert.Equal(115, rows.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(rows, row => Assert.Equal(row.Size, row.Members.Length));

        var report = new StringBuilder();
        report.AppendLine("# Offline batch formation replay, 3 and 4 November 2026 UTC")
            .AppendLine()
            .AppendLine("Source: operator export of 115 formations. The score, changed areas, overlap group, parent batch ID, and gate start are absent from the export. This replay evaluates the red-rate prior in `EmpiricalBatchPolicy.RedRate`, by actual formed size, using only earlier UTC days. It cannot reconstruct admission decisions, target changes, or a causal policy counterfactual.")
            .AppendLine()
            .AppendLine("Phase 7 is red; phases 4 and 6 are observed green. Abandoned and still-open formations are censored. One-member isolation and per-task runs are excluded. Published phase is used as evidence of a green gate, not as a separate outcome.")
            .AppendLine()
            .AppendLine("| Day | Formations | Evaluable batches | Red | Predicted red rate | Actual red rate |")
            .AppendLine("|---|---:|---:|---:|---:|---:|");
        var evaluated = rows.Where(row => row.Evaluable).ToArray();
        foreach (var group in rows.GroupBy(row => row.At.UtcDateTime.Date).OrderBy(group => group.Key))
        {
            var sample = group.Where(row => row.Evaluable).ToArray();
            var predicted = sample.Average(row => Predict(row, evaluated));
            var actual = sample.Count(row => row.Red) / (double)sample.Length;
            report.AppendLine($"| {group.Key:yyyy-MM-dd} | {group.Count()} | {sample.Length} | {sample.Count(row => row.Red)} | {predicted:P1} | {actual:P1} |");
        }
        report.AppendLine()
            .AppendLine("| Formed size | Evaluable batches | Red | Predicted red rate | Actual red rate |")
            .AppendLine("|---:|---:|---:|---:|---:|");
        foreach (var group in evaluated.GroupBy(row => row.Size).OrderBy(group => group.Key))
            report.AppendLine($"| {group.Key} | {group.Count()} | {group.Count(row => row.Red)} | {group.Average(row => Predict(row, evaluated)):P1} | {group.Count(row => row.Red) / (double)group.Count():P1} |");
        report.AppendLine()
            .AppendLine($"Evaluable batches: {evaluated.Length}; censored or one-member formations: {rows.Length - evaluated.Length}. The export cannot identify every successor manifest, so this table must not be used to train the production first-candidate policy. A full replay needs the recorded risk, paths, overlap group, parent ID, and verdict-time facts.");

        var output = Environment.GetEnvironmentVariable("JOB_RESULTS_DIR");
        if (!string.IsNullOrWhiteSpace(output))
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "batch-formation-replay.md"), report.ToString());
        }
        Assert.Contains("Predicted red rate", report.ToString());
    }

    private static double Predict(Formation row, IReadOnlyList<Formation> all)
    {
        var prior = all.Where(other => other.Size == row.Size
                && other.At.UtcDateTime.Date < row.At.UtcDateTime.Date)
            .Select(other => new BatchPlanObservation(other.Id, other.At, other.Size,
                0, [], "unknown", other.Red))
            .ToArray();
        return EmpiricalBatchPolicy.RedRate(prior, row.Size);
    }

    private static Formation Parse(string line)
    {
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(7, fields.Length);
        var at = DateTimeOffset.ParseExact($"2026-11-{fields[0]} {fields[1]} +00:00",
            "yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture);
        return new(at, fields[2], int.Parse(fields[3], CultureInfo.InvariantCulture),
            fields[4].Split(',').Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray(),
            int.Parse(fields[5], CultureInfo.InvariantCulture), fields[6]);
    }
}
