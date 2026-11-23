using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>One same-tip repeat for every failed test suite, independent of its runner.</summary>
internal static class PostMergeTestRetry
{
    internal static async Task<PostMergeCommandVerdict> RepeatAsync(PostMergeCommandVerdict verdict,
        Func<IReadOnlyList<string>, IReadOnlyList<string>> blockingTests,
        Func<string, Task<(int ExitCode, bool TimedOut, string Output)>> run)
    {
        var observed = verdict.FailedTests ?? PostMergeAttributor.FailedTests(verdict.OutputTail ?? "");
        var blocking = blockingTests(observed);
        if (observed.Count > 0 && blocking.Count == 0)
            return verdict with { ExitCode = 0 };

        var flaky = new List<string>();
        var failed = new List<string>();
        var timedOut = false;
        // Repeating each .NET method separately records partial recovery accurately.
        // Unrecognized test output and other suite kinds repeat the original command.
        if (blocking.Count > 0 && blocking.All(test =>
                PostMergeAttributor.FocusedCommand(verdict.Command, [test]) is not null))
        {
            foreach (var test in blocking)
            {
                var result = await run(PostMergeAttributor.FocusedCommand(verdict.Command, [test])!);
                timedOut |= result.TimedOut;
                if (result.ExitCode == 0 && !result.TimedOut) flaky.Add(test);
                else failed.Add(test);
            }
            return verdict with { ExitCode = failed.Count == 0 ? 0 : verdict.ExitCode,
                TimedOut = timedOut, FailedTests = failed, FlakyTests = flaky, Retried = true };
        }

        var repeated = await run(verdict.Command);
        var passed = repeated.ExitCode == 0 && !repeated.TimedOut;
        return verdict with { ExitCode = passed ? 0 : verdict.ExitCode, TimedOut = repeated.TimedOut,
            FlakyTests = passed ? observed : [], Retried = true };
    }
}
