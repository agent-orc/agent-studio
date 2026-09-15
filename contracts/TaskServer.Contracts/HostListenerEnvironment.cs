namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Environment variables through which a running ASP.NET Core host hands its own
/// listener configuration to a child process.
///
/// The Studio backend starts the local merge gate, so every gate command
/// inherits the Studio's environment - and that environment carries a listener:
/// <c>dotnet run</c> applies the project's launch profile, which exports
/// <c>ASPNETCORE_URLS</c> even when the actual port comes from <c>--urls</c> on
/// the command line. A gate command that boots an ASP.NET Core host of its own
/// then reads the Studio's listener as its configuration. That is how the
/// connector profile, which owns exactly one endpoint and rejects any other,
/// refused to start inside the gate while the same tests passed from an operator
/// shell (AGT-2840). The preparation gate already hands the prepare script a
/// curated environment (<see cref="PreparationHostEnvironment"/>); a verify
/// command needs the same boundary for the keys that carry a listener.
///
/// Dropping them is safe in both directions: a gate command that serves brings
/// its own listener configuration, and every other command needs none.
/// </summary>
public static class HostListenerEnvironment
{
    /// <summary>
    /// The exact variables a host reads as listener configuration. <c>URLS</c>
    /// has no prefix because the unprefixed environment provider feeds
    /// application configuration, which is what a host-owned listener guard
    /// reads; the prefixed spellings reach host configuration.
    /// </summary>
    public static readonly IReadOnlyList<string> Keys =
    [
        "ASPNETCORE_URLS",
        "ASPNETCORE_HTTP_PORTS",
        "ASPNETCORE_HTTPS_PORTS",
        "ASPNETCORE_HTTPS_PORT",
        "DOTNET_URLS",
        "URLS",
    ];

    /// <summary>
    /// Environment spelling of the <c>Kestrel</c> configuration section. Every
    /// key below it configures endpoints, so the whole prefix carries a
    /// listener, not just <c>Kestrel__Endpoints__*</c>.
    /// </summary>
    public const string KestrelPrefix = "Kestrel__";

    /// <summary>
    /// True when the variable carries listener configuration. The comparison
    /// ignores case because configuration keys do, and because the same variable
    /// is spelled differently on Windows and on Linux.
    /// </summary>
    public static bool Carries(string? name)
        => !string.IsNullOrEmpty(name)
           && (Keys.Contains(name, StringComparer.OrdinalIgnoreCase)
               || name.StartsWith(KestrelPrefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Drops every carrying variable from an environment the caller is still
    /// building, and returns the dropped names so a caller can record what it
    /// fenced.
    /// </summary>
    public static IReadOnlyList<string> RemoveFrom(IDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var removed = environment.Keys.Where(Carries).ToArray();
        foreach (var key in removed) environment.Remove(key);
        return removed;
    }
}
