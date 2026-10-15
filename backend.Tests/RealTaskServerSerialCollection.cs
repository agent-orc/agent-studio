using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Reason: its members start a real task-server.dll child process whose start and request handling starved for CPU inside the parallel backend run.
/// </summary>
/// <remarks>
/// <c>ConnectorNegativeMatrixTests</c> passed in 10 s alone and timed out
/// after 240 s three times inside the full parallel run (AGT-3140): a
/// separate OS process competes with every parallel host in the suite for
/// CPU. Members run after every parallel collection has finished.
/// Architecture/Fixtures/serial-collections.txt lists every member.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealTaskServerSerialCollection
{
    public const string Name = "RealTaskServerSerial";
}
