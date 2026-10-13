using System.Collections;
using System.Runtime.CompilerServices;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;

namespace AgentStudio.Tests;

/// <summary>
/// Points this test host's temp root at a per-process suite directory before
/// any test type is touched (AGT-2858), and drops the launcher's listener
/// configuration once for the whole process (AGT-3106).
///
/// The CLR runs a module initializer before the first access to anything in the
/// assembly, so every fixture field initialized with
/// <c>Path.Combine(Path.GetTempPath(), ...)</c> already resolves inside the
/// suite root - including the several hundred fixtures that predate
/// <see cref="TempWorkspace"/>. <see cref="TestTempRoot"/> removes that root
/// again when the process exits and sweeps roots a killed host left behind.
/// </summary>
internal static class TempRootBootstrap
{
    [ModuleInitializer]
    internal static void RedirectTempRoot()
    {
        TestTempRoot.Redirect();
        var removed = LauncherListenerEnvironment.Strip(
            Environment.GetEnvironmentVariables(),
            name => Environment.SetEnvironmentVariable(name, null));
        if (removed.Count > 0) Console.Error.WriteLine(LauncherListenerEnvironment.Describe(removed));
    }
}

/// <summary>
/// The listener configuration a test process inherits from whatever launched
/// it. WebApplicationFactory runs the real entry point inside the test
/// process, so a host under test reads that configuration as its own: the
/// Studio backend starts <c>dotnet test</c> with <c>ASPNETCORE_URLS</c> set,
/// and the connector profile, which owns exactly one endpoint, then refused
/// to boot (AGT-2840). No test needs the launcher's listener, so the
/// bootstrap removes it once per process instead of every host boot setting
/// it aside and restoring it, which only worked while no two hosts booted at
/// the same time.
/// </summary>
internal static class LauncherListenerEnvironment
{
    /// <summary>
    /// Unsets every variable of <paramref name="environment"/> that carries
    /// listener configuration (<see cref="HostListenerEnvironment"/>) and
    /// returns the removed names in ordinal order.
    /// </summary>
    public static IReadOnlyList<string> Strip(IDictionary environment, Action<string> unset)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(unset);
        var removed = environment.Keys
            .OfType<string>()
            .Where(HostListenerEnvironment.Carries)
            .Order(StringComparer.Ordinal)
            .ToArray();
        foreach (var name in removed) unset(name);
        return removed;
    }

    /// <summary>The one log line naming what the bootstrap removed.</summary>
    public static string Describe(IReadOnlyList<string> removed)
        => "backend.Tests: removed the launcher's listener configuration ("
           + string.Join(", ", removed)
           + ") so every host under test owns its listener.";
}
