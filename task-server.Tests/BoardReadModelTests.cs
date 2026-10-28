using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AgentStudio.Shared;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TaskServer.Tests;

/// <summary>
/// Board read model, change feed and workspace shadow import (pipeline
/// delivery target, M1 and M2, decision D7 A-B).
/// </summary>
public sealed class BoardReadModelTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Projection_orders_live_lanes_by_lifecycle_and_rank_and_only_counts_the_archive()
    {
        var at = Start.UtcDateTime;
        var model = BoardReadModelProjection.Project("prj", "Project", 7, 3,
        [
            Card("B-2", TaskStates.Ready, rank: 2, at),
            Card("B-1", TaskStates.Ready, rank: 1, at),
            Card("B-3", TaskStates.Ready, rank: 1, at),
            Card("B-4", TaskStates.Backlog, rank: 0, at),
            Card("B-5", TaskStates.Archive, rank: 0, at),
            Card("B-6", "9-unknown", rank: 0, at),
        ]);

        Assert.Equal(
            TaskStates.All.Where(state => state != TaskStates.Archive).Append("9-unknown"),
            model.Lanes.Select(lane => lane.State));
        var ready = model.Lanes.Single(lane => lane.State == TaskStates.Ready);
        Assert.Equal(["B-1", "B-3", "B-2"], ready.Cards.Select(card => card.TaskKey));
        Assert.Equal(3, ready.Count);
        Assert.Equal(5, model.LiveCount);
        Assert.Equal(1, model.ArchivedCount);
        Assert.Equal("\"board-v1-g7\"", model.ETag);
        Assert.Equal(3, model.WorkspaceSourceGeneration);
    }

    [Fact]
    public void Validator_matching_accepts_the_current_tag_a_list_and_the_wildcard_only()
    {
        var current = BoardReadModelProjection.FormatETag(4);
        Assert.True(BoardReadModelEndpoints.MatchesValidator(current, current));
        Assert.True(BoardReadModelEndpoints.MatchesValidator($"\"other\", {current}", current));
        Assert.True(BoardReadModelEndpoints.MatchesValidator("*", current));
        Assert.False(BoardReadModelEndpoints.MatchesValidator(BoardReadModelProjection.FormatETag(3), current));
        Assert.False(BoardReadModelEndpoints.MatchesValidator("not a tag", current));
    }

    [Fact]
    public Task Board_read_advances_its_generation_on_a_lane_move_and_answers_304_for_the_current_tag()
        => TempDirectory.RunAsync(async temp =>
        {
            await using var factory = new BoardApiFactory(temp.Path, new ManualTimeProvider(Start));
            using var client = StudioTestClient.Create(factory);
            var store = factory.Services.GetRequiredService<TaskServerStore>();
            var (project, task) = await SeedAsync(store);

            var first = await client.GetAsync($"/api/v1/projects/{project.Name}/board");
            first.EnsureSuccessStatusCode();
            var before = (await first.Content.ReadFromJsonAsync<BoardReadModelResponse>())!;
            Assert.Equal(before.ETag, first.Headers.ETag!.ToString());
            Assert.Contains(before.Lanes.Single(lane => lane.State == TaskStates.Backlog).Cards,
                card => card.TaskKey == task.TaskKey);
            Assert.Null(before.WorkspaceSourceGeneration);

            using (var conditional = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/projects/{project.ProjectId}/board"))
            {
                conditional.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(before.ETag));
                var unchanged = await client.SendAsync(conditional);
                Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
            }

            var move = await client.PutAsJsonAsync(
                $"/api/v1/projects/{project.ProjectId}/tasks/{task.TaskId}",
                new UpdateTaskRequest(null, null, TaskStates.Ready, task.Version));
            move.EnsureSuccessStatusCode();

            using (var stale = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/projects/{project.ProjectId}/board"))
            {
                stale.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(before.ETag));
                var changed = await client.SendAsync(stale);
                Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
                var after = (await changed.Content.ReadFromJsonAsync<BoardReadModelResponse>())!;
                Assert.True(after.Generation > before.Generation);
                Assert.Contains(after.Lanes.Single(lane => lane.State == TaskStates.Ready).Cards,
                    card => card.TaskKey == task.TaskKey);
                Assert.DoesNotContain(after.Lanes.Single(lane => lane.State == TaskStates.Backlog).Cards,
                    card => card.TaskKey == task.TaskKey);
            }
        });

    [Fact]
    public Task Change_feed_lists_changes_after_a_cursor_and_requests_a_resync_for_a_cursor_ahead_of_the_server()
        => TempDirectory.RunAsync(async temp =>
        {
            await using var factory = new BoardApiFactory(temp.Path, new ManualTimeProvider(Start));
            using var client = StudioTestClient.Create(factory);
            var store = factory.Services.GetRequiredService<TaskServerStore>();
            var (project, task) = await SeedAsync(store);
            var cursor = (await store.GetBoardReadModelAsync(project.ProjectId, default)).Generation;

            await store.UpdateTaskAsync(project.ProjectId, task.TaskId,
                new UpdateTaskRequest("Renamed", null, TaskStates.Ready, task.Version), "test", default);
            await store.UpdateTaskAsync(project.ProjectId, task.TaskId,
                new UpdateTaskRequest(null, "body only", null, task.Version + 1), "test", default);

            var feed = (await client.GetFromJsonAsync<BoardChangeFeedResponse>(
                $"/api/v1/projects/{project.ProjectId}/board/changes?after={cursor}"))!;
            var change = Assert.Single(feed.Changes);
            Assert.Equal(task.TaskKey, change.TaskKey);
            Assert.Equal(BoardChangeKinds.Upsert, change.Change);
            Assert.Equal(TaskStates.Ready, change.State);
            Assert.Equal(feed.Generation, change.Generation);
            Assert.False(feed.ResyncRequired);

            var caughtUp = (await client.GetFromJsonAsync<BoardChangeFeedResponse>(
                $"/api/v1/projects/{project.ProjectId}/board/changes?after={feed.Generation}"))!;
            Assert.Empty(caughtUp.Changes);
            Assert.False(caughtUp.ResyncRequired);

            var ahead = (await client.GetFromJsonAsync<BoardChangeFeedResponse>(
                $"/api/v1/projects/{project.ProjectId}/board/changes?after={feed.Generation + 10}"))!;
            Assert.True(ahead.ResyncRequired);
            Assert.Empty(ahead.Changes);

            var unknown = await client.GetAsync("/api/v1/projects/no-such-project/board/changes");
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        });

    [Fact]
    public Task Workspace_import_inserts_updates_and_removes_cards_and_records_its_source_generation()
        => TempDirectory.RunAsync(async temp =>
        {
            await using var factory = new BoardApiFactory(temp.Path, new ManualTimeProvider(Start));
            using var client = StudioTestClient.Create(factory);
            var store = factory.Services.GetRequiredService<TaskServerStore>();
            var (project, task) = await SeedAsync(store);

            var response = await client.PutAsJsonAsync(
                $"/api/v1/projects/{project.Name}/board/workspace-import",
                new BoardWorkspaceImportRequest(
                    41,
                    [
                        new BoardWorkspaceCard(task.TaskKey, task.Title, TaskStates.Progress, 3),
                        new BoardWorkspaceCard("brd-90", "Workspace only", TaskStates.Ready, 1),
                    ],
                    []));
            response.EnsureSuccessStatusCode();
            var imported = (await response.Content.ReadFromJsonAsync<BoardWorkspaceImportResponse>())!;
            Assert.Equal((1, 1, 0, 0), (imported.Inserted, imported.Updated, imported.Unchanged, imported.Removed));
            Assert.Empty(imported.Rejected);

            var board = await store.GetBoardReadModelAsync(project.ProjectId, default);
            Assert.Equal(imported.Generation, board.Generation);
            Assert.Equal(41, board.WorkspaceSourceGeneration);
            var moved = board.Lanes.Single(lane => lane.State == TaskStates.Progress).Cards.Single();
            Assert.Equal((task.TaskKey, 3L), (moved.TaskKey, moved.Rank));
            Assert.Contains(board.Lanes.Single(lane => lane.State == TaskStates.Ready).Cards,
                card => card.TaskKey == "BRD-90");

            var repeat = await store.ImportWorkspaceBoardAsync(project.ProjectId,
                new BoardWorkspaceImportRequest(42, [new BoardWorkspaceCard(task.TaskKey, task.Title, TaskStates.Progress, 3)], ["BRD-90"]),
                "test", default);
            Assert.Equal((0, 0, 1, 1), (repeat.Inserted, repeat.Updated, repeat.Unchanged, repeat.Removed));
            Assert.True(repeat.Generation > imported.Generation);
            var created = await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Next key"), "test", default);
            Assert.Equal("BRD-91", created.TaskKey);
        });

    [Fact]
    public Task Workspace_import_rejects_a_key_owned_by_another_project_and_leaves_it_unchanged()
        => TempDirectory.RunAsync(async temp =>
        {
            await using var factory = new BoardApiFactory(temp.Path, new ManualTimeProvider(Start));
            var store = factory.Services.GetRequiredService<TaskServerStore>();
            var (project, task) = await SeedAsync(store);
            var other = await store.CreateProjectAsync(
                new CreateProjectRequest(project.WorkspaceId, "Other", "OTH"), "test", default);

            var result = await store.ImportWorkspaceBoardAsync(other.ProjectId,
                new BoardWorkspaceImportRequest(1, [new BoardWorkspaceCard(task.TaskKey, "Hijack", TaskStates.Completed, 0)], null),
                "test", default);

            var rejection = Assert.Single(result.Rejected);
            Assert.Equal("task-key-in-other-project", rejection.Code);
            var unchanged = await store.GetTaskAsync(project.ProjectId, task.TaskId, default);
            Assert.Equal((task.Title, TaskStates.Backlog), (unchanged!.Title, unchanged.State));
            await Assert.ThrowsAsync<ArgumentException>(() => store.ImportWorkspaceBoardAsync(other.ProjectId,
                new BoardWorkspaceImportRequest(1, [new BoardWorkspaceCard(" ", "No key", TaskStates.Ready, 0)], null),
                "test", default));
        });

    private static async Task<(ProjectDto Project, TaskDto Task)> SeedAsync(TaskServerStore store)
    {
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Board WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Board Project", "BRD"), "test", default);
        var task = await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Backlog task"), "test", default);
        return (project, task);
    }

    private static BoardCardDto Card(string key, string state, long rank, DateTime at)
        => new($"tsk-{key}", key, key, state, rank, 1, null, at);

    private sealed class BoardApiFactory(string dataDirectory, TimeProvider clock) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TaskServer:DataDirectory"] = dataDirectory,
                    ["TaskServer:ListenUrl"] = string.Empty,
                    ["TaskServer:RetentionSchedulerEnabled"] = "false",
                }));
            builder.ConfigureServices(services => services.AddSingleton(clock));
        }
    }
}
