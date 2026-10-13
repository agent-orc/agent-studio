using Xunit;

namespace AgentStudio.Tests;

// Reason: real git processes and server/API scheduling need isolation until parallel failures are compared against the base.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RemoteRunnerSerialCollection
{
    public const string Name = "RemoteRunnerSerial";
}
