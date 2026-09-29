using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace AgentStudio.Tests;

public sealed class ArtifactIngestionEndpointsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "artifact-ingest-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Advertised_limit_accounts_for_base64_without_raising_global_body_cap()
    {
        var policy = ArtifactTransferPolicy.Resolve(
            25L * 1024 * 1024,
            projectMaxFileBytes: 20L * 1024 * 1024,
            projectMaxTotalBytes: null);

        Assert.Equal(25L * 1024 * 1024, policy.MaxRequestBodyBytes);
        Assert.True(policy.MaxFileBytes < 20L * 1024 * 1024);
        Assert.True((policy.MaxFileBytes * 4 / 3) < policy.MaxRequestBodyBytes);
        Assert.Equal(100L * 1024 * 1024, policy.MaxTotalBytes);
    }

    [Fact]
    public void Default_per_file_limit_is_eight_mebibytes()
    {
        var limits = ArtifactTransferPolicy.Resolve(25L * 1024 * 1024, null, null);
        Assert.Equal(8L * 1024 * 1024, limits.MaxFileBytes);
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(1025)]
    public void Base64_size_calculation_preserves_the_exact_binary_boundary(int size)
    {
        Assert.Equal(size, ArtifactTransferPolicy.DecodedLength(
            Convert.ToBase64String(new byte[size])));
    }

    [Fact]
    public async Task Oversized_artifact_request_returns_typed_limit_and_received_size()
    {
        var called = false;
        var middleware = new ArtifactRequestLimitMiddleware(
            _ => { called = true; return Task.CompletedTask; },
            new ArtifactRequestLimits(1024));
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/runner/artifacts";
        context.Request.ContentLength = 2048;
        context.Response.Body = new MemoryStream();
        context.RequestServices = new ServiceCollection()
            .AddOptions()
            .AddLogging()
            .Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(_ => { })
            .BuildServiceProvider();

        await middleware.InvokeAsync(context);

        Assert.False(called);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal("artifact-request-too-large", body.RootElement.GetProperty("type").GetString());
        Assert.Equal(1024, body.RootElement.GetProperty("limitBytes").GetInt64());
        Assert.Equal(2048, body.RootElement.GetProperty("receivedBytes").GetInt64());
    }

    [Fact]
    public void Partial_artifact_board_fact_names_file_size_limit_and_non_transfer()
    {
        var fact = ArtifactTransferPolicy.BoardFact(new ArtifactTransferIssue(
            "results/playwright/archive/trace.zip",
            18L * 1024 * 1024,
            "exceeded the 25 MB upload limit"));

        Assert.Equal(
            "result artifact trace.zip 18 MB exceeded the 25 MB upload limit; not transferred",
            fact);
    }

    [Fact]
    public void NormalizeResultsPath_AddsResultsPrefix()
    {
        Assert.Equal(
            "results/screenshots/home--real.png",
            ArtifactIngestionEndpoints.NormalizeResultsPath("screenshots/home--real.png"));
    }

    [Theory]
    [InlineData("../outside.png")]
    [InlineData("results/../outside.png")]
    [InlineData("C:/temp/outside.png")]
    [InlineData(@"C:\temp\outside.png")]
    public void NormalizeResultsPath_RejectsEscapes(string path)
    {
        Assert.Throws<ArtifactIngestException>(() => ArtifactIngestionEndpoints.NormalizeResultsPath(path));
    }

    [Fact]
    public void WriteArtifacts_WritesDecodedBytesUnderResults()
    {
        var job = Path.Combine(_root, "projects", "demo", "tasks", "001", "AGT-1");
        Directory.CreateDirectory(job);
        var task = new TaskInfo { Id = "AGT-1", TaskKey = "AGT-1", FolderPath = job };
        var request = new ArtifactIngestRequest(
            "AGT-1",
            [new RunnerArtifactUpload(
                "screenshots/home--real.png",
                Convert.ToBase64String(Encoding.UTF8.GetBytes("png bytes")))]);

        var response = ArtifactIngestionEndpoints.WriteArtifacts(task, request);

        Assert.Equal(1, response.Uploaded);
        Assert.Equal(["results/screenshots/home--real.png"], response.Files);
        Assert.Equal(
            "png bytes",
            File.ReadAllText(Path.Combine(job, "results", "screenshots", "home--real.png")));
    }

    [Fact]
    public void WriteArtifacts_RedactsCredentialsFromTextEvidence()
    {
        var job = Path.Combine(_root, "projects", "demo", "tasks", "001", "AGT-2");
        Directory.CreateDirectory(job);
        var task = new TaskInfo { Id = "AGT-2", TaskKey = "AGT-2", FolderPath = job };
        const string secret = "rnr.credential.abcdefghijklmnopqrstuvwxyz012345";
        var request = new ArtifactIngestRequest(
            "AGT-2",
            [new RunnerArtifactUpload("report.md", Convert.ToBase64String(Encoding.UTF8.GetBytes($"token: {secret}")))]);

        ArtifactIngestionEndpoints.WriteArtifacts(task, request);

        var written = File.ReadAllText(Path.Combine(job, "results", "report.md"));
        Assert.DoesNotContain(secret, written);
        Assert.Contains("REDACTED_CREDENTIAL", written);
    }

    [Fact]
    public void ArtifactUploadCommitMessage_ListsUploadedFiles()
    {
        var message = WorkspaceArtifactCommitService.BuildArtifactUploadMessage(
            "AGT-1",
            ["results/a.png", "results/report.json"]);

        Assert.Contains("record uploaded artifacts for AGT-1", message);
        Assert.Contains("Artifact-Upload-Files: results/a.png,results/report.json", message);
    }
}
