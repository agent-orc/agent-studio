
namespace AgentStudio.Cli;

/// <summary>
/// Dispatches calls to the right <see cref="ICliExecutionService"/> based on
/// <see cref="CliTypes"/>. Re-broadcasts each backend's events so consumers
/// (SignalR hub, runner) only need a single subscription.
/// </summary>
public sealed class CliRouter
{
    private readonly Dictionary<string, ICliExecutionService> _byType;

    public event Action<string, string, CliOutputLine>? OnOutput;          // (cliType, jobKey, line)
    public event Action<string, string, CliExecution>?  OnStarted;
    public event Action<string, string, CliExecution>?  OnFinished;
    public event Action<string, string, CliRunEvent>?   OnRunEvent;        // (cliType, jobKey, event)

    public CliRouter(params ICliExecutionService[] services)
    {
        _byType = new(StringComparer.OrdinalIgnoreCase);
        foreach (var svc in services)
        {
            if (svc == null) continue;
            _byType[CliTypes.Normalize(svc.CliType)] = svc;
        }

        foreach (var (type, svc) in _byType)
        {
            svc.OnOutput   += (jobKey, line) => OnOutput?.Invoke(type, jobKey, line);
            svc.OnStarted  += (jobKey, exec) => OnStarted?.Invoke(type, jobKey, exec);
            svc.OnFinished += (jobKey, exec) => OnFinished?.Invoke(type, jobKey, exec);
            svc.OnRunEvent += (jobKey, evt)  => OnRunEvent?.Invoke(type, jobKey, evt);
        }
    }

    public IEnumerable<ICliExecutionService> All => _byType.Values;

    public ICliExecutionService Get(string? cliType)
        => _byType.TryGetValue(CliTypes.Normalize(cliType), out var svc)
            ? svc
            // Fallback for an unknown/unset cli is Claude: it is the project
            // default and always has plan quota.
            : _byType[CliTypes.Claude];

    public void ReattachAll()
    {
        foreach (var svc in _byType.Values) svc.ReattachOnStartup();
    }

    public bool CanReattach(string jobKey)
        => _byType.Values.Any(service => service.CanReattach(jobKey));

    /// <summary>
    /// Run each backend's periodic stale-orphan sweep. Driven by
    /// <c>OrphanReaperHostedService</c> on a timer so orphaned CLI process
    /// trees from finished/crashed runs do not accumulate over a long-lived
    /// backend session. Safe to call repeatedly: a live run is never reaped.
    /// </summary>
    public void ReapStaleOrphansAll()
    {
        foreach (var svc in _byType.Values) svc.ReapStaleOrphans();
    }
}
