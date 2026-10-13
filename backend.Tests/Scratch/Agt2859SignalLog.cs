using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AgentStudio.Tests;

// Scratch for AGT-2859; not committed. Records every SIGINT/SIGTERM/SIGQUIT the
// xunit process receives without changing how the signal is handled.
internal static class Agt2859SignalLog
{
    private static readonly List<PosixSignalRegistration> Registrations = new();

    [ModuleInitializer]
    internal static void Register()
    {
        var path = Environment.GetEnvironmentVariable("AGT2859_SIGNAL_LOG");
        if (string.IsNullOrEmpty(path)) return;
        foreach (var signal in new[] { PosixSignal.SIGINT, PosixSignal.SIGTERM, PosixSignal.SIGQUIT })
        {
            Registrations.Add(PosixSignalRegistration.Create(signal, context =>
                File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} pid={Environment.ProcessId} signal={context.Signal}\n")));
        }
        File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} pid={Environment.ProcessId} registered\n");
    }
}
