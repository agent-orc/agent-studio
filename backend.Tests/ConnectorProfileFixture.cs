using Xunit;

namespace AgentStudio.Tests;

public sealed class ConnectorProfileFixture : IAsyncLifetime
{
    internal RecordingTransport Transport { get; } = new();
    private ConnectorUnderTest? _connector;
    internal ConnectorUnderTest Connector => _connector
        ?? throw new InvalidOperationException("Connector fixture initialization did not complete.");

    public Task InitializeAsync()
    {
        _connector = ConnectorUnderTest.Boot(Transport);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_connector is { } connector)
            await connector.DisposeAsync();
    }
}
