using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TaskServer.Tests;

/// <summary>
/// P1 group G6 "task-owned files, attachments, artifacts, screenshots, and
/// results" - proves every content-bearing route round-trips the exact bytes
/// stored, at the HTTP boundary.
///
/// Program.cs is wired centrally, once, after every P1 group lands, so it
/// does not yet call <c>StudioTaskArtifactsEndpoints.MapStudioTaskArtifactsEndpoints</c>
/// or <c>TaskServerStore.ApplyStudioTaskArtifactsMigrationAsync</c>. Until
/// then, each test host here wires both itself: the routes through an
/// <see cref="IStartupFilter"/> (calling the same <c>IEndpointRouteBuilder</c>
/// mapping logic Program.cs will call through the <c>WebApplication</c>
/// overload) and the schema through a direct migration call against the
/// store's own database file. Neither touches Program.cs.
/// </summary>
public sealed class StudioTaskArtifactsTests
{
    private static async Task<(WebApplicationFactory<Program> Factory, HttpClient Client)> CreateAsync()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "studio-task-artifacts-" + Guid.NewGuid().ToString("N"));
        var baseFactory = new StudioTestApiFactory(dataDirectory);
        var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter, MapStudioTaskArtifactsStartupFilter>()));
        var client = StudioTestClient.Create(factory);

        var store = factory.Services.GetRequiredService<TaskServerStore>();
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = store.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString());
        await connection.OpenAsync();
        await store.ApplyStudioTaskArtifactsMigrationAsync(connection, CancellationToken.None);

        return (factory, client);
    }

    private sealed class MapStudioTaskArtifactsStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.UseEndpoints(endpoints => endpoints.MapStudioTaskArtifactsEndpoints());
        };
    }

    [Fact]
    public async Task Attachment_upload_then_download_round_trips_the_exact_bytes()
    {
        var (factory, client) = await CreateAsync();
        await using (factory)
        {
            var (_, project, task) = await SeedTaskAsync(client);
            var bytes = Encoding.UTF8.GetBytes("attachment content ☃ " + Guid.NewGuid());

            using var form = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(bytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            form.Add(fileContent, "file", "notes.txt");

            var upload = await client.PostAsync($"{BasePath(project, task)}/attachments", form);
            var uploadBody = await upload.Content.ReadAsStringAsync();
            Assert.True(upload.IsSuccessStatusCode, $"upload returned {(int)upload.StatusCode}: {uploadBody}");
            var uploaded = Deserialize<TaskAttachmentDto>(uploadBody);
            Assert.Equal("notes.txt", uploaded.FileName);
            Assert.Equal(bytes.LongLength, uploaded.SizeBytes);

            var download = await client.GetAsync($"{BasePath(project, task)}/attachments/notes.txt");
            download.EnsureSuccessStatusCode();
            var downloaded = await download.Content.ReadAsByteArrayAsync();
            Assert.Equal(bytes, downloaded);
            Assert.Equal("text/plain", download.Content.Headers.ContentType!.MediaType);
        }
    }

    [Fact]
    public async Task Task_file_put_then_get_round_trips_exact_content_and_version()
    {
        var (factory, client) = await CreateAsync();
        await using (factory)
        {
            var (_, project, task) = await SeedTaskAsync(client);
            const string content = "first version content";

            var put = await client.PutAsJsonAsync(
                $"{BasePath(project, task)}/files/notes.md",
                new UpdateTaskFileRequest(Convert.ToBase64String(Encoding.UTF8.GetBytes(content)), 0, "text/markdown"));
            var putBody = await put.Content.ReadAsStringAsync();
            Assert.True(put.IsSuccessStatusCode, $"put returned {(int)put.StatusCode}: {putBody}");
            var putDto = Deserialize<TaskFileDto>(putBody);
            Assert.Equal(1, putDto.Version);

            var get = await client.GetAsync($"{BasePath(project, task)}/files/notes.md");
            get.EnsureSuccessStatusCode();
            var downloaded = await get.Content.ReadAsStringAsync();
            Assert.Equal(content, downloaded);
            Assert.Equal("text/markdown", get.Content.Headers.ContentType!.MediaType);
            Assert.Equal("1", get.Headers.GetValues("X-Task-File-Version").Single());
        }
    }

    [Fact]
    public async Task Stale_expected_version_on_file_put_returns_conflict()
    {
        var (factory, client) = await CreateAsync();
        await using (factory)
        {
            var (_, project, task) = await SeedTaskAsync(client);
            var first = await client.PutAsJsonAsync(
                $"{BasePath(project, task)}/files/notes.md",
                new UpdateTaskFileRequest(Convert.ToBase64String(Encoding.UTF8.GetBytes("v1")), 0));
            first.EnsureSuccessStatusCode();

            var stale = await client.PutAsJsonAsync(
                $"{BasePath(project, task)}/files/notes.md",
                new UpdateTaskFileRequest(Convert.ToBase64String(Encoding.UTF8.GetBytes("v2-stale")), 0));
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            var error = Deserialize<ApiError>(await stale.Content.ReadAsStringAsync());
            Assert.Equal("resource-version-mismatch", error.Code);
        }
    }

    [Fact]
    public async Task File_read_with_scope_code_is_rejected()
    {
        var (factory, client) = await CreateAsync();
        await using (factory)
        {
            var (_, project, task) = await SeedTaskAsync(client);
            var put = await client.PutAsJsonAsync(
                $"{BasePath(project, task)}/files/notes.md",
                new UpdateTaskFileRequest(Convert.ToBase64String(Encoding.UTF8.GetBytes("v1")), 0));
            put.EnsureSuccessStatusCode();

            var response = await client.GetAsync($"{BasePath(project, task)}/files/notes.md?scope=code");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var error = Deserialize<ApiError>(await response.Content.ReadAsStringAsync());
            Assert.Equal("dev-seat-scope-required", error.Code);
        }
    }

    [Fact]
    public async Task File_history_reflects_multiple_versions_in_order()
    {
        var (factory, client) = await CreateAsync();
        await using (factory)
        {
            var (_, project, task) = await SeedTaskAsync(client);
            for (var version = 1; version <= 3; version++)
            {
                var put = await client.PutAsJsonAsync(
                    $"{BasePath(project, task)}/files/notes.md",
                    new UpdateTaskFileRequest(
                        Convert.ToBase64String(Encoding.UTF8.GetBytes($"content v{version}")), version - 1));
                put.EnsureSuccessStatusCode();
            }

            var history = await client.GetFromJsonAsync<TaskFileHistoryResponse>(
                $"{BasePath(project, task)}/files/notes.md/history");
            Assert.NotNull(history);
            Assert.Equal(new long[] { 1, 2, 3 }, history!.Revisions.Select(item => item.Version));
        }
    }

    [Fact]
    public async Task Task_artifacts_screenshots_and_results_round_trip_real_run_artifact_content()
    {
        var (factory, client) = await CreateAsync();
        await using (factory)
        {
            var (_, project, task) = await SeedTaskAsync(client, state: "2-ready");
            var (run, lease) = await SeedClaimedRunAsync(client, "runner-g6-" + Guid.NewGuid().ToString("N")[..8]);

            var reportBytes = Encoding.UTF8.GetBytes("<html>report</html>");
            var report = await IngestArtifactAsync(
                client, run.RunId, lease.Fence, "results/report.html", "text/html", reportBytes);

            var screenshotBytes = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
            var screenshot = await IngestArtifactAsync(
                client, run.RunId, lease.Fence, "screenshots/final.png", "image/png", screenshotBytes);

            // artifacts: both artifacts show up in the task-scoped join.
            var artifacts = await client.GetFromJsonAsync<List<ArtifactDto>>($"{BasePath(project, task)}/artifacts");
            Assert.Contains(artifacts!, item => item.ArtifactId == report.ArtifactId);
            Assert.Contains(artifacts!, item => item.ArtifactId == screenshot.ArtifactId);

            // screenshots: only the image/* artifact is listed.
            var screenshots = await client.GetFromJsonAsync<List<ArtifactDto>>($"{BasePath(project, task)}/screenshots");
            var onlyScreenshot = Assert.Single(screenshots!);
            Assert.Equal(screenshot.ArtifactId, onlyScreenshot.ArtifactId);

            // screenshot (singular): the latest image artifact's exact bytes.
            var screenshotContent = await client.GetAsync($"{BasePath(project, task)}/screenshot");
            screenshotContent.EnsureSuccessStatusCode();
            Assert.Equal(screenshotBytes, await screenshotContent.Content.ReadAsByteArrayAsync());
            Assert.Equal("image/png", screenshotContent.Content.Headers.ContentType!.MediaType);

            // results/{path*}: exact bytes of the matching artifact, served as a
            // real file download rather than a JSON-wrapped base64 blob.
            var resultContent = await client.GetAsync($"{BasePath(project, task)}/results/report.html");
            resultContent.EnsureSuccessStatusCode();
            Assert.Equal(reportBytes, await resultContent.Content.ReadAsByteArrayAsync());
            Assert.Equal("text/html", resultContent.Content.Headers.ContentType!.MediaType);
        }
    }

    [Fact]
    public async Task Task_output_summarizes_the_latest_run()
    {
        var (factory, client) = await CreateAsync();
        await using (factory)
        {
            var (_, project, task) = await SeedTaskAsync(client, state: "2-ready");
            var (run, lease) = await SeedClaimedRunAsync(client, "runner-g6-output-" + Guid.NewGuid().ToString("N")[..8]);
            await IngestArtifactAsync(client, run.RunId, lease.Fence, "results/report.html", "text/html", "x"u8.ToArray());

            var output = await client.GetFromJsonAsync<TaskOutputDto>($"{BasePath(project, task)}/output");
            Assert.NotNull(output);
            Assert.NotNull(output!.LatestRun);
            Assert.Equal(run.RunId, output.LatestRun!.RunId);
        }
    }

    private static async Task<(WorkspaceDto Workspace, ProjectDto Project, TaskDto Task)> SeedTaskAsync(
        HttpClient client, string state = "0-backlog")
    {
        var workspace = await PostAsync<CreateWorkspaceRequest, WorkspaceDto>(
            client, "/api/v1/workspaces", new CreateWorkspaceRequest("G6 workspace " + Guid.NewGuid().ToString("N")[..8]));
        var project = await PostAsync<CreateProjectRequest, ProjectDto>(
            client,
            "/api/v1/projects",
            new CreateProjectRequest(workspace.WorkspaceId, "G6 project", "G6" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant()));
        var task = await PostAsync<CreateTaskRequest, TaskDto>(
            client,
            $"/api/v1/projects/{project.ProjectId}/tasks",
            new CreateTaskRequest("G6 artifact test task", State: state));
        return (workspace, project, task);
    }

    private static async Task<(RunDto Run, LeaseDto Lease)> SeedClaimedRunAsync(HttpClient client, string runnerId)
    {
        var register = await client.PutAsJsonAsync(
            $"/api/v1/runners/{runnerId}",
            new RegisterRunnerRequest(
                "runner", "host-" + runnerId, "instance-" + runnerId, "1.0.0", TaskServerProtocol.Current,
                [ReviewCapabilities.CodingExecutor]));
        register.EnsureSuccessStatusCode();
        var claim = await PostAsync<ClaimRequest, ClaimResponse>(
            client, $"/api/v1/runners/{runnerId}/claims", new ClaimRequest(runnerId, "instance-" + runnerId));
        Assert.Equal("claimed", claim.Status);
        return (claim.Run!, claim.Lease!);
    }

    private static async Task<ArtifactDto> IngestArtifactAsync(
        HttpClient client, string runId, long fence, string name, string mediaType, byte[] content)
    {
        var sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        return await PostAsync<ArtifactIngestRequest, ArtifactDto>(
            client,
            $"/api/v1/runs/{runId}/artifacts",
            new ArtifactIngestRequest(
                "artifact-" + Guid.NewGuid().ToString("N")[..8],
                name,
                mediaType,
                Convert.ToBase64String(content),
                sha256,
                "idem-" + Guid.NewGuid().ToString("N"),
                fence));
    }

    private static async Task<TResponse> PostAsync<TRequest, TResponse>(HttpClient client, string path, TRequest request)
    {
        using var response = await client.PostAsJsonAsync(path, request);
        var detail = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{path} returned {(int)response.StatusCode}: {detail}");
        return Deserialize<TResponse>(detail);
    }

    private static T Deserialize<T>(string json)
        => JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static string BasePath(ProjectDto project, TaskDto task)
        => $"/api/v1/projects/{project.ProjectId}/tasks/{task.TaskId}";
}
