using System.Runtime.CompilerServices;
using AgentStudio.TestSupport;

namespace AgentRunner.Tests;

/// <summary>
/// Points this test host's temp root at a per-process suite directory before
/// any test type is touched (AGT-2858).
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
    internal static void RedirectTempRoot() => TestTempRoot.Redirect();
}
