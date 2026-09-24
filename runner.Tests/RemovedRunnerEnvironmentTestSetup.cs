using System.Runtime.CompilerServices;

namespace AgentRunner.Tests;

internal static class RemovedRunnerEnvironmentTestSetup
{
    [ModuleInitializer]
    internal static void ClearRemovedInvocationSettings()
    {
        Environment.SetEnvironmentVariable("RUNNER_EXEC_ENGINE", null);
        Environment.SetEnvironmentVariable("RUNNER_CLI_BIN", null);
        Environment.SetEnvironmentVariable("RUNNER_CLI_ARGS", null);
        Environment.SetEnvironmentVariable("RUNNER_CLI_RESUME_ARGS", null);
    }
}
