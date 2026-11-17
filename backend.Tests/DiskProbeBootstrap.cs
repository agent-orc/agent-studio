using System.Runtime.CompilerServices;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

/// <summary>
/// Pins the process-wide disk probe before any test type is touched
/// (AGT-3866). Disk admission in the batch gate pilot, the API host lease
/// path and the runner daemons otherwise measured the machine running the
/// suite: once its root file system passed 95 % full, pilot ticks returned
/// early and unrelated tests failed. A test of disk pressure injects its own
/// <see cref="IHostFilesystemProbe"/>.
/// </summary>
internal static class DiskProbeBootstrap
{
    [ModuleInitializer]
    internal static void PinDiskProbe() => HostFilesystemProbes.Default = FixedHostFilesystemProbe.Healthy;
}
