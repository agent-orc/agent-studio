using System.Diagnostics;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// One focused run or repeat of a post-merge test command (RT-3). <see cref="Tests"/>
/// are the tests the run covered; <see cref="Output"/> names the ones that failed.
/// </summary>
internal sealed record PostMergeTestRun(string Phase, string Sha, IReadOnlyList<string> Tests,
    int ExitCode, bool TimedOut, string Output, long DurationMs)
{
    public bool Passed => ExitCode == 0 && !TimedOut;

    /// <summary>
    /// The result of one test in this run. A passing run passes every test. A
    /// failing run fails the tests its output names, and passes the others when
    /// it names at least one; a failing run of one test fails it; otherwise the
    /// result is unknown.
    /// </summary>
    public string ResultOf(string test)
    {
        if (Passed) return PostMergeTestResults.Pass;
        var failed = PostMergeAttributor.FailedTests(Output);
        if (failed.Count > 0)
            return failed.Any(name => PostMergeAttributor.SameTest(name, test))
                ? PostMergeTestResults.Fail : PostMergeTestResults.Pass;
        return Tests.Count == 1 ? PostMergeTestResults.Fail : PostMergeTestResults.Unknown;
    }

    public PostMergeFocusedRun ToReceipt() => new(Phase, Sha, Tests.Count, ExitCode, TimedOut, DurationMs);
}

/// <summary>One same-tip repeat for every failed test suite, independent of its runner.</summary>
internal static class PostMergeTestRetry
{
    internal static async Task<PostMergeCommandVerdict> RepeatAsync(PostMergeCommandVerdict verdict,
        Func<IReadOnlyList<string>, IReadOnlyList<string>> blockingTests,
        Func<string, Task<(int ExitCode, bool TimedOut, string Output)>> run,
        Action<PostMergeTestRun>? record = null, string sha = "",
        Func<string, Task<(int ExitCode, bool TimedOut, string Output)>>? referenceRun = null)
    {
        var observed = verdict.FailedTests ?? PostMergeAttributor.FailedTests(verdict.OutputTail ?? "");
        var blocking = blockingTests(observed);
        if (observed.Count > 0 && blocking.Count == 0)
            return verdict with { ExitCode = 0 };

        async Task<(int ExitCode, bool TimedOut, string Output)> Timed(string command, IReadOnlyList<string> tests)
        {
            var started = Stopwatch.GetTimestamp();
            var result = await run(command);
            record?.Invoke(new(PostMergeRunPhases.Repeat, sha, tests, result.ExitCode, result.TimedOut,
                result.Output, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds));
            return result;
        }

        async Task<bool> Reference(string command, IReadOnlyList<string> tests)
        {
            if (referenceRun is null) return false;
            var started = Stopwatch.GetTimestamp();
            var result = await referenceRun(command);
            record?.Invoke(new(PostMergeRunPhases.Reference, sha, tests, result.ExitCode, result.TimedOut,
                result.Output, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds));
            return result.ExitCode == 0 && !result.TimedOut;
        }

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
                var result = await Timed(PostMergeAttributor.FocusedCommand(verdict.Command, [test])!, [test]);
                timedOut |= result.TimedOut;
                if (result.ExitCode == 0 && !result.TimedOut) flaky.Add(test);
                else
                {
                    if (await Reference(PostMergeAttributor.FocusedCommand(verdict.Command, [test])!, [test])) flaky.Add(test);
                    else failed.Add(test);
                }
            }
            return verdict with { ExitCode = failed.Count == 0 ? 0 : verdict.ExitCode,
                TimedOut = failed.Count > 0 && timedOut, FailedTests = failed, FlakyTests = flaky, Retried = true };
        }

        var repeated = await Timed(verdict.Command, blocking.Count > 0 ? blocking : observed);
        var passed = repeated.ExitCode == 0 && !repeated.TimedOut;
        if (!passed) passed = await Reference(verdict.Command, blocking.Count > 0 ? blocking : observed);
        return verdict with { ExitCode = passed ? 0 : verdict.ExitCode, TimedOut = !passed && repeated.TimedOut,
            FlakyTests = passed ? observed : [], Retried = true };
    }
}
