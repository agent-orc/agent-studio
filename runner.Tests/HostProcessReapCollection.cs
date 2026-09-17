using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// Cases that assert <c>CliProcessReaper.ReapedCount</c>, the process-global
/// reap total this daemon reports as host telemetry. Every collection that
/// cleans up a workspace with a live process in it advances the same counter,
/// so these cases cannot run beside one: under parallel collections the total
/// read after the call includes another collection's reap and the arithmetic
/// stops holding.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HostProcessReapCollection
{
    public const string Name = "Host process reap counters";
}
