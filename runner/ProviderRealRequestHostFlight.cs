namespace AgentRunner;

/// <summary>
/// The host-local exclusion around one provider real request. The lock file is
/// held until the request's run has ended, so two probe CLIs never share the
/// protected clean context. The run receives the probe deadline as its stop
/// token: when the deadline expires, the caller returns at once and the run
/// stops its CLI, which ends the run and releases the lock.
/// </summary>
internal static class ProviderRealRequestHostFlight
{
    public const string InProgress = "another host-local provider probe is in progress";

    public static async Task<ProcessResult> RunAsync(
        string lockPath,
        Func<CancellationToken, Task<(ProcessResult Result, bool TimedOut, bool LaunchFailed)>> run,
        CancellationToken deadline)
    {
        FileStream hostFlight;
        try
        {
            hostFlight = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return new ProcessResult(1, "", InProgress);
        }
        Task<(ProcessResult Result, bool TimedOut, bool LaunchFailed)> running;
        try { running = run(deadline); }
        catch
        {
            await hostFlight.DisposeAsync();
            throw;
        }
        _ = running.ContinueWith(_ => hostFlight.Dispose(), TaskScheduler.Default);
        var (result, timedOut, _) = await running.WaitAsync(deadline);
        // CAR reports its own watchdog as "Runner timeout"; that is a slow
        // CLI, not network evidence.
        return timedOut ? new ProcessResult(1, "", "real request deadline exceeded") : result;
    }
}
