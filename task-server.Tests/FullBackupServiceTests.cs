using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed class FullBackupServiceTests
{
    [Fact]
    public async Task Full_backup_round_trips_through_verify_and_restore()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Workspace"), "test", default);
        await store.CreateProjectAsync(new CreateProjectRequest(workspace.WorkspaceId, "Project", "FB"), "test", default);

        var management = new FullBackupManagementService(store);
        var summary = await management.CreateAsync("test", default);
        Assert.True(summary.TotalBytes > 0);
        Assert.Equal(0, summary.ColdPayloadCount);

        var listed = await management.ListAsync(default);
        Assert.Contains(listed, item => item.Id == summary.Id);

        var verified = await management.VerifyAsync(summary.Id, default);
        Assert.True(verified.Verified);
        Assert.Equal(summary.SetSha256, verified.Summary.SetSha256);

        await store.ChangeModeAsync(new ChangeModeRequest(TaskServerMode.Maintenance, "restore rehearsal"), "operator", default);
        var restored = await management.RestoreAsync(summary.Id, "operator", default);
        Assert.True(restored.Restored);

        var projects = await store.ListProjectsAsync(workspace.WorkspaceId, default);
        Assert.Single(projects);
    }

    private static TaskServerStore Store(string dataDirectory)
        => new(Options.Create(new TaskServerOptions { DataDirectory = dataDirectory }), TimeProvider.System);
}
