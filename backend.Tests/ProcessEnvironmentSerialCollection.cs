using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Reason: its members write process environment variables that every host booting at the same time would read as configuration.
/// </summary>
/// <remarks>
/// The only serial collection for in-process hosts (AGT-3106). A host under
/// test passes its settings with <c>UseSetting</c>, which reaches even the
/// reads <c>Program.cs</c> makes before <c>Build()</c>, so a test needs the
/// process environment only when the environment itself is the subject:
/// configuration inherited from the launching process. Those cases live here
/// and run after every parallel collection has finished.
/// Architecture/Fixtures/serial-collections.txt lists every member.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentSerialCollection
{
    public const string Name = "ProcessEnvironmentSerial";
}
