using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed class ArtifactChunkTransferTests
{
    [Fact]
    public async Task Artifact_of_130_mebibytes_resumes_through_100_megabyte_request_limit()
    {
        using var temp = new TempDirectory();
        var size = 130 * 1024 * 1024;
        var content = new byte[size];
        for (var index = 0; index < content.Length; index++) content[index] = (byte)(index % 251);
        var sha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var count = size / ArtifactChunkStore.MaxChunkBytes + 1;
        var settings = Options.Create(new TaskServerOptions
        {
            DataDirectory = temp.Path,
            ResultArtifactMaxFileBytes = 140L * 1024 * 1024,
            ResultArtifactMaxTotalBytes = 150L * 1024 * 1024,
        });
        await using var factory = new ChunkFactory(temp.Path);
        using var client = new HttpClient(new ProxyBodyLimitHandler(factory.Server.CreateHandler()))
        {
            BaseAddress = new Uri("http://localhost"),
        };
        client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName,
            TaskServerProtocol.Current.ToString());
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var claim = await SeedClaimAsync(store);
        var runId = claim.Run!.RunId;
        var resumed = new ArtifactChunkStore(settings);
        const long proxyBodyLimit = 100_000_000;
        for (var index = 0; index < count; index++)
        {
            var offset = index * ArtifactChunkStore.MaxChunkBytes;
            var length = Math.Min(ArtifactChunkStore.MaxChunkBytes, size - offset);
            var request = new ArtifactChunkRequest("results/large.bin", "application/octet-stream",
                sha, size, index, count, Convert.ToBase64String(content.AsSpan(offset, length)));
            Assert.True(JsonSerializer.SerializeToUtf8Bytes(request).LongLength < proxyBodyLimit);
            using var result = await client.PostAsJsonAsync($"/api/v1/runs/{runId}/artifact-chunks", request);
            Assert.True(result.IsSuccessStatusCode, await result.Content.ReadAsStringAsync());
            if (index == 0)
            {
                using var replay = await client.PostAsJsonAsync($"/api/v1/runs/{runId}/artifact-chunks", request);
                Assert.True(replay.IsSuccessStatusCode);
                using var changed = await client.PostAsJsonAsync($"/api/v1/runs/{runId}/artifact-chunks",
                    request with { ContentBase64 = Convert.ToBase64String(new byte[length]) });
                Assert.Equal(System.Net.HttpStatusCode.BadRequest, changed.StatusCode);
            }
            if (index == count / 2)
            {
                var partial = resumed.Status(runId, sha, count, size);
                Assert.Equal(index + 1, partial.ReceivedIndices.Count);
                // A restarted uploader asks for the indices already durable on the server.
                using var statusResponse = await client.GetAsync(
                    $"/api/v1/runs/{runId}/artifact-chunks/{sha}?count={count}&sizeBytes={size}");
                Assert.True(statusResponse.IsSuccessStatusCode);
                var serverStatus = await statusResponse.Content.ReadFromJsonAsync<ArtifactChunkStatus>();
                Assert.Equal(index + 1, serverStatus!.ReceivedIndices.Count);
            }
        }
        var status = resumed.Status(runId, sha, count, size);
        Assert.Equal(count, status.ReceivedIndices.Count);
        using var completed = await client.PostAsJsonAsync(
            $"/api/v1/runs/{runId}/artifact-chunks/complete",
            new ArtifactChunkCompleteRequest("art-130", "results/large.bin", "application/octet-stream",
                sha, size, count, "artifact-130", claim.Lease!.Fence,
                "chunk-runner", "chunk-runner:1", claim.Lease.LeaseId));
        Assert.True(completed.IsSuccessStatusCode, await completed.Content.ReadAsStringAsync());
        var artifact = await completed.Content.ReadFromJsonAsync<ArtifactDto>();
        Assert.Equal(sha, artifact!.Sha256);
        Assert.Equal(size, artifact.SizeBytes);
        Assert.True(File.Exists(store.ContentObjects.PathFor(sha)));
    }

    private sealed class ProxyBodyLimitHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null
                && (await request.Content.ReadAsByteArrayAsync(cancellationToken)).LongLength > 100_000_000)
                return new HttpResponseMessage(System.Net.HttpStatusCode.RequestEntityTooLarge);
            return await base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class ChunkFactory(string dataDirectory) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TaskServer:DataDirectory"] = dataDirectory,
                    ["TaskServer:ListenUrl"] = string.Empty,
                    ["TaskServer:ResultArtifactMaxFileBytes"] = (140L * 1024 * 1024).ToString(),
                    ["TaskServer:ResultArtifactMaxTotalBytes"] = (150L * 1024 * 1024).ToString(),
                }));
        }
    }

    private static async Task<ClaimResponse> SeedClaimAsync(TaskServerStore store)
    {
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Chunk proxy"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Chunk proxy", "CKP"), "test", default);
        await store.CreateTaskAsync(project.ProjectId,
            new CreateTaskRequest("Large artifact", State: "2-ready"), "test", default);
        await store.RegisterRunnerAsync("chunk-runner",
            new RegisterRunnerRequest("chunk-runner", "host", "chunk-runner:1", "1.0.0",
                TaskServerProtocol.Current, [ReviewCapabilities.CodingExecutor]), "test", default);
        return await store.ClaimAsync(new ClaimRequest("chunk-runner", "chunk-runner:1"), "test", default);
    }

    [Fact]
    public void Oversized_manifest_is_rejected_before_staging()
    {
        using var temp = new TempDirectory();
        var store = new ArtifactChunkStore(Options.Create(new TaskServerOptions
        {
            DataDirectory = temp.Path,
            ResultArtifactMaxFileBytes = 1024,
        }));
        Assert.Throws<ArtifactChunkTooLargeException>(() =>
            store.Put("run", new ArtifactChunkRequest("results/a", "application/octet-stream",
                new string('a', 64), 2048, 0, 1, Convert.ToBase64String(new byte[2048]))));
    }

    [Fact]
    public async Task Chunk_request_over_server_body_budget_returns_typed_413()
    {
        using var temp = new TempDirectory();
        await using var factory = new ChunkFactory(temp.Path);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName,
            TaskServerProtocol.Current.ToString());
        using var body = new StringContent(new string('x', 9 * 1024 * 1024));
        using var response = await client.PostAsync("/api/v1/runs/run/artifact-chunks", body);
        Assert.Equal(System.Net.HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("artifact-request-too-large", problem.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Completed_chunks_use_the_existing_fenced_artifact_write()
    {
        using var temp = new TempDirectory();
        var options = Options.Create(new TaskServerOptions { DataDirectory = temp.Path });
        var store = new TaskServerStore(options, new ManualTimeProvider(
            new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero)));
        await store.InitializeAsync();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Chunks"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Chunks", "CHK"), "test", default);
        await store.CreateTaskAsync(project.ProjectId,
            new CreateTaskRequest("Chunk task", State: "2-ready"), "test", default);
        await store.RegisterRunnerAsync("chunk-runner",
            new RegisterRunnerRequest("chunk-runner", "host", "chunk-runner:1", "1.0.0",
                TaskServerProtocol.Current, [ReviewCapabilities.CodingExecutor]), "test", default);
        var claim = await store.ClaimAsync(new ClaimRequest("chunk-runner", "chunk-runner:1"),
            "test", default);
        var bytes = Enumerable.Range(0, 5 * 1024 * 1024).Select(index => (byte)(index % 251)).ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var chunks = new ArtifactChunkStore(options);
        for (var index = 0; index < 2; index++)
        {
            var offset = index * ArtifactChunkStore.MaxChunkBytes;
            var length = Math.Min(ArtifactChunkStore.MaxChunkBytes, bytes.Length - offset);
            chunks.Put(claim.Run!.RunId, new ArtifactChunkRequest("results/proof.bin",
                "application/octet-stream", sha, bytes.LongLength, index, 2,
                Convert.ToBase64String(bytes.AsSpan(offset, length))));
        }
        var artifact = await chunks.CompleteAsync(claim.Run!.RunId,
            new ArtifactChunkCompleteRequest("art-proof", "results/proof.bin", "application/octet-stream",
                sha, bytes.LongLength, 2, "chunk-proof", claim.Lease!.Fence,
                "chunk-runner", "chunk-runner:1", claim.Lease.LeaseId), store, "test", default);
        Assert.Equal(bytes.LongLength, artifact.SizeBytes);
        var stored = await store.GetArtifactContentAsync(claim.Run.RunId, artifact.ArtifactId, default);
        Assert.Equal(bytes, Convert.FromBase64String(stored!.ContentBase64));
    }
}
