using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3032: operator archive for chat sessions. The pure policy is pinned as
/// a matrix; the monolith's file-backed store is exercised end to end.
/// </summary>
public sealed class OrchestratorContextArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "atp-orch-archive-" + Guid.NewGuid().ToString("N"));
    private readonly string _watchPath;

    public OrchestratorContextArchiveTests()
    {
        _watchPath = Path.Combine(_root, "project-a");
        Directory.CreateDirectory(_watchPath);
    }

    [Theory]
    [InlineData("idle", true)]
    [InlineData("parked", true)]
    [InlineData(null, true)]
    [InlineData("active", false)]
    [InlineData("queued", false)]
    public void Only_idle_or_parked_sessions_can_be_archived(string? runtimeStatus, bool expected)
        => Assert.Equal(expected, OrchestratorContextArchivePolicy.CanArchive(runtimeStatus));

    [Fact]
    public void Archive_request_keeps_the_first_mark_and_unarchive_clears_it()
    {
        var first = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        var later = first.AddDays(1);

        Assert.Equal(later, OrchestratorContextArchivePolicy.ArchivedAtAfterRequest(null, archived: true, later));
        Assert.Equal(first, OrchestratorContextArchivePolicy.ArchivedAtAfterRequest(first, archived: true, later));
        Assert.Null(OrchestratorContextArchivePolicy.ArchivedAtAfterRequest(first, archived: false, later));
        Assert.Null(OrchestratorContextArchivePolicy.ArchivedAtAfterRequest(null, archived: false, later));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void Archived_group_holds_operator_and_task_archive_marks(bool operatorMark, bool hiddenMark, bool expected)
        => Assert.Equal(expected, OrchestratorContextArchivePolicy.IsArchived(
            operatorMark ? DateTime.UtcNow : null,
            hiddenMark ? DateTime.UtcNow : null));

    [Fact]
    public async Task Local_store_persists_the_mark_across_instances_and_a_new_turn_clears_it()
    {
        Assert.True(OrchestratorContextKey.TryParse("task:project-a/A-1", out var taskContext));
        var persistence = CreateLocalPersistence();
        await persistence.AppendAsync("project-a", _watchPath, taskContext,
            new OrchestratorChatTurn { Id = "turn_1", Role = OrchestratorChatRoles.User, Text = "Question" },
            CancellationToken.None);

        var archived = await persistence.SetArchivedAsync(taskContext, archived: true, CancellationToken.None);
        Assert.NotNull(archived.ArchivedAt);

        // A fresh instance reads the same mark: archive survives a restart.
        var restarted = CreateLocalPersistence();
        var listed = Assert.Single(
            await restarted.ListContextsAsync(includeHidden: true, CancellationToken.None),
            context => context.ContextKey == taskContext.Value);
        Assert.Equal(archived.ArchivedAt, listed.ArchivedAt);

        await restarted.AppendAsync("project-a", _watchPath, taskContext,
            new OrchestratorChatTurn { Id = "turn_2", Role = OrchestratorChatRoles.User, Text = "Follow-up" },
            CancellationToken.None);
        var reopened = Assert.Single(
            await restarted.ListContextsAsync(includeHidden: true, CancellationToken.None),
            context => context.ContextKey == taskContext.Value);
        Assert.Null(reopened.ArchivedAt);
    }

    [Fact]
    public async Task Local_store_refuses_to_archive_a_session_without_transcript()
    {
        Assert.True(OrchestratorContextKey.TryParse("task:project-a/A-404", out var missing));

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateLocalPersistence().SetArchivedAsync(missing, archived: true, CancellationToken.None));
    }

    private LocalOrchestratorChatPersistence CreateLocalPersistence()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["WatchPaths:0:Name"] = "project-a",
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
            })
            .Build();
        var scanner = new TaskScannerService(
            configuration,
            NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, configuration));
        return new LocalOrchestratorChatPersistence(
            new OrchestratorChat(NullLogger<OrchestratorChat>.Instance),
            scanner);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
