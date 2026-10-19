using AgentStudio.CliHosting;

namespace AgentRunner;

/// <summary>
/// Per-provider clean-context homes for the idle auth probe (AGT-2823). A
/// lease is reused while the provider's credential generation stays the same
/// and re-acquired when it changes, so a login that appears after the home was
/// created (first <c>codex login</c> on a container host) is linked into the
/// probe home by <see cref="TaskCleanContextStore.Acquire"/> without a process
/// restart (AGT-3216).
/// </summary>
public sealed class ProviderAuthProbeContexts
{
    private readonly object _sync = new();
    private readonly Func<string, TaskCleanContextLease> _acquire;
    private readonly Func<string, string?> _credentialGeneration;
    private readonly Action<string> _log;
    private readonly Dictionary<string, (TaskCleanContextLease Lease, string? Generation)> _leases =
        new(StringComparer.OrdinalIgnoreCase);

    public ProviderAuthProbeContexts(
        Func<string, TaskCleanContextLease> acquire,
        Func<string, string?> credentialGeneration,
        Action<string> log)
    {
        _acquire = acquire;
        _credentialGeneration = credentialGeneration;
        _log = log;
    }

    /// <summary>The probe home for <paramref name="provider"/>, or null when isolation is unavailable.</summary>
    public TaskCleanContextLease? Get(string provider)
    {
        if (provider is not ("claude" or "codex")) return null;
        lock (_sync)
        {
            var generation = _credentialGeneration(provider);
            if (_leases.TryGetValue(provider, out var existing))
            {
                if (existing.Generation == generation) return existing.Lease;
                existing.Lease.Dispose();
                _leases.Remove(provider);
            }

            try
            {
                var lease = _acquire(provider);
                _leases[provider] = (lease, generation);
                return lease;
            }
            catch (Exception ex)
            {
                _log($"provider-auth-probe clean-context isolation unavailable for '{provider}': {ex.Message}; "
                    + "probe will run without isolation from agent sessions.");
                return null;
            }
        }
    }
}
