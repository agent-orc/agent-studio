using Xunit;

namespace AgentStudio.Tests;

public sealed class ConnectorProfileFixture : IAsyncLifetime
{
    internal RecordingTransport Transport { get; } = new();
    internal ConnectorUnderTest Connector { get; private set; } = null!;

    public Task InitializeAsync()
    {
        Connector = ConnectorUnderTest.Boot(Transport);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await (Connector?.DisposeAsync() ?? ValueTask.CompletedTask);
}
