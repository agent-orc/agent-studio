using AgentRunner;

namespace AgentRunner.Tests;

internal static class RunnerTestOptions
{
    internal static (RunnerOptions Options, string? TaskKey, bool Once, bool Help) Parse(
        string[] args, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        if (args.Contains("--state-dir", StringComparer.Ordinal))
            return RunnerOptions.Parse(args, environment);

        var state = Path.Combine(Path.GetTempPath(), "runner-state-" + Guid.NewGuid().ToString("N"));
        return RunnerOptions.Parse([.. args, "--state-dir", state], environment);
    }
}
