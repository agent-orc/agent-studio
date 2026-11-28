using AgentStudio.CliHosting;

namespace AgentRunner;

/// <summary>
/// Stable per-provider homes for auth probes (AGT-2823). Each invocation
/// acquires its own lease, which also links credentials from a first login
/// that appeared after the home was created (AGT-3216).
/// </summary>
public sealed class ProviderAuthProbeContexts(
    Func<string, TaskCleanContextLease> acquire,
    Action<string> log)
{
    /// <summary>A caller-owned lease for one invocation, or null when isolation is unavailable.</summary>
    public TaskCleanContextLease? Acquire(string provider)
    {
        if (provider is not ("claude" or "codex")) return null;
        try { return acquire(provider); }
        catch (Exception ex)
        {
            log($"provider-auth-probe clean-context isolation unavailable for '{provider}': {ex.Message}");
            return null;
        }
    }
}
