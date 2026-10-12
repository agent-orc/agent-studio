namespace AgentStudio.Clients;

/// <summary>
/// The registered client whose write request is running on the current async
/// flow (AGT-3088). <see cref="ClientIdentityMiddleware"/> enters it for a
/// write request; the task index reads it at invalidation time so a board read
/// of that client waits for its own mutation and for nothing else.
///
/// <para>The scope ends with the request. Work the request hands to the
/// background (a fire-and-forget task, a queued timer) still sees the scope
/// object through the copied execution context, but the scope is inactive once
/// the request has finished, so a mutation such work makes later is never
/// attributed to the client. Detach a hand-off explicitly with
/// <see cref="Detach"/> when it can run while the request is still open.</para>
/// </summary>
public static class AmbientClient
{
    private static readonly AsyncLocal<Scope?> Current = new();

    /// <summary>The active write request's client id, or null.</summary>
    public static string? Id => Current.Value is { Active: true } scope ? scope.ClientId : null;

    /// <summary>Marks the current flow as <paramref name="clientId"/>'s write request.</summary>
    public static IDisposable Enter(string clientId)
    {
        var scope = new Scope(clientId, Current.Value);
        Current.Value = scope;
        return scope;
    }

    /// <summary>
    /// Clears the client for the current flow until disposed. Wrap the start of
    /// background work in it so the work does not inherit the client.
    /// </summary>
    public static IDisposable Detach()
    {
        var scope = new Scope(null, Current.Value);
        Current.Value = scope;
        return scope;
    }

    private sealed class Scope(string? clientId, Scope? previous) : IDisposable
    {
        private volatile bool _disposed;

        public string? ClientId => clientId;

        public bool Active => !_disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (ReferenceEquals(Current.Value, this)) Current.Value = previous;
        }
    }
}
