using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

public static class PostMergeMetrics
{
    public static IReadOnlyList<double> RedMinutes(PostMergeProjectState state,
        DateTimeOffset since, DateTimeOffset now)
    {
        var samples = new List<double>();
        DateTimeOffset? redAt = null;
        foreach (var run in state.Runs.Where(run => run.FinishedAtUtc is not null)
                     .OrderBy(run => run.FinishedAtUtc))
        {
            if (run.Status == "red") redAt ??= run.FinishedAtUtc;
            if (run.Status == "green" && redAt is { } start)
            {
                Add(start, run.FinishedAtUtc!.Value);
                redAt = null;
            }
        }
        if (redAt is { } open) Add(open, now);
        return samples;

        void Add(DateTimeOffset start, DateTimeOffset end)
        {
            if (end <= since || start >= now) return;
            samples.Add(Math.Max(0, (end - (start < since ? since : start)).TotalMinutes));
        }
    }

    public static PipelineBaseline Decorate(PipelineBaseline baseline,
        IReadOnlyList<PostMergeProjectState> states, DateTimeOffset since, DateTimeOffset now)
    {
        var red = states.SelectMany(state => RedMinutes(state, since, now)).ToArray();
        var nightly = states.Sum(state => state.Runs.Count(run => run.Kind == "nightly"
            && run.Status is ("green" or "red")
            && run.Verdicts.Count >= run.Commands.Count
            && run.FinishedAtUtc >= since && run.FinishedAtUtc <= now));
        int? full = baseline.FullSuiteRuns is null && nightly == 0 ? null
            : (baseline.FullSuiteRuns ?? 0) + nightly;
        return baseline with
        {
            RedDevelopMedianMinutes = red.Length == 0 ? null : PipelineBaselineMetrics.Percentile(red, .5),
            RedDevelopP90Minutes = red.Length == 0 ? null : PipelineBaselineMetrics.Percentile(red, .9),
            FullSuiteRuns = full,
            FullSuiteRunsPerDay = full is null ? null : full / Math.Max((now - since).TotalDays, 1d / 24),
        };
    }
}
