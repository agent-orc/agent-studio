using AgentStudio.TaskServer;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

/// <summary>
/// Shares schema creation, while every test receives its own empty database.
/// Tests exercising migrations from older schemas still create a blank directory.
/// </summary>
public sealed class InitializedStoreFixture : IAsyncLifetime
{
    private readonly TempDirectory _template = new();
    private string? _databasePath;

    public async Task InitializeAsync()
    {
        var store = new TaskServerStore(Options.Create(new TaskServerOptions
        {
            DataDirectory = _template.Path,
        }), new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        await store.InitializeAsync();
        _databasePath = store.DatabasePath;
    }

    internal TempDirectory CreateDirectory()
    {
        var directory = new TempDirectory();
        Directory.CreateDirectory(directory.Path);
        File.Copy(_databasePath ?? throw new InvalidOperationException("Store template was not initialized."),
            Path.Combine(directory.Path, "task-server.db"));
        return directory;
    }

    public Task DisposeAsync()
    {
        _template.Dispose();
        return Task.CompletedTask;
    }
}
