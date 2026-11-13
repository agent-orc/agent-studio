namespace AgentStudio.Cli;

/// <summary>
/// AGT-3709: the model catalogue a request may wait for. A cold Claude or
/// Codex catalogue is discovered by starting the CLI in a PTY, which takes
/// several seconds; on the create path that made <c>POST /api/tasks</c> take
/// about 9 s against its 30 ms budget. A request waits at most
/// <see cref="Budget"/> and then answers from the registry catalogue, while the
/// discovery keeps running and warms the cache for the next request.
/// </summary>
public static class RequestPathModelCatalogue
{
    public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(250);

    public const string RegistrySource = "request-path-registry-fallback";

    public static Task<CliModelCatalog> GetAsync(CliRouter router, string cliType, CancellationToken ct)
        => GetAsync(cliType, token => router.Get(cliType).GetModelCatalogAsync(false, token), Budget, ct);

    /// <summary>
    /// Waits for <paramref name="load"/> up to <paramref name="budget"/>. A CLI
    /// without a registry catalogue rethrows the load failure or throws
    /// <see cref="TimeoutException"/>, which callers already treat as "no
    /// catalogue".
    /// </summary>
    public static async Task<CliModelCatalog> GetAsync(
        string cliType,
        Func<CancellationToken, Task<CliModelCatalog>> load,
        TimeSpan budget,
        CancellationToken ct)
    {
        // The discovery is not tied to the request: when the request stops
        // waiting, the discovery still finishes and fills the shared cache.
        var pending = load(CancellationToken.None);
        if (!pending.IsCompleted)
        {
            var finished = await Task.WhenAny(pending, Task.Delay(budget, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (finished != pending)
            {
                _ = pending.ContinueWith(
                    static task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return RegistryCatalogue(cliType)
                    ?? throw new TimeoutException($"{cliType} model catalogue did not answer within {budget.TotalMilliseconds:0} ms.");
            }
        }

        try
        {
            return await pending.ConfigureAwait(false);
        }
        catch (Exception) when (RegistryCatalogue(cliType) is { } registry)
        {
            return registry;
        }
    }

    private static CliModelCatalog? RegistryCatalogue(string cliType) => cliType switch
    {
        CliTypes.Claude => ClaudeModelDiscovery.FallbackCatalog(RegistrySource),
        CliTypes.Codex => CodexModelDiscovery.FallbackCatalog(RegistrySource),
        _ => null,
    };
}
