using System.Net;
using System.Security.Cryptography;
using AgentStudio.Retention;

namespace AgentStudio.Retention.Tests;

public sealed class ArchiveTargetTests
{
    [Fact]
    public async Task Local_target_upload_verify_read_tamper_and_delete()
    {
        using var temp = new RetentionTestWorkspace();
        var target = new LocalDirectoryTarget(Path.Combine(temp.Root, "archive"));
        var bytes = "cold payload"u8.ToArray();
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));

        await using var input = new MemoryStream(bytes);
        var reference = await target.PutAsync("project/TASK-1/20260908T010203000Z/payload.zip", input, sha, bytes.Length);

        Assert.Equal("local", reference.Target);
        Assert.True((await target.VerifyAsync(reference)).Verified);
        await using (var restored = await target.OpenReadAsync(reference.ObjectKey))
        using (var output = new MemoryStream())
        {
            await restored.CopyToAsync(output);
            Assert.Equal(bytes, output.ToArray());
        }

        await File.WriteAllTextAsync(target.Resolve(reference.ObjectKey), "tampered");
        Assert.Equal("sha256-mismatch", (await target.VerifyAsync(reference)).Error);
        await target.DeleteAsync(reference.ObjectKey);
        Assert.Equal("object-missing", (await target.VerifyAsync(reference)).Error);
    }

    [Fact]
    public async Task S3_target_uses_path_style_checksums_and_verifies_bytes_instead_of_trusting_etag()
    {
        var handler = new InMemoryS3Handler();
        using var http = new HttpClient(handler);
        using var target = new S3Target(new S3TargetOptions
        {
            Endpoint = new Uri("http://minio.test:9000"),
            Bucket = "archive",
            Prefix = "cold",
            Region = "us-east-1",
            AccessKey = "minio",
            SecretKey = "minio-secret",
            PathStyle = true,
            ServerSideChecksum = true,
        }, http);
        var bytes = "s3 payload"u8.ToArray();
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));

        await using var input = new MemoryStream(bytes);
        var reference = await target.PutAsync("PROJ/TASK-2/at/payload.zip", input, sha, bytes.Length);

        Assert.Equal("s3", reference.Target);
        Assert.Equal("cold/PROJ/TASK-2/at/payload.zip", reference.ObjectKey);
        Assert.Equal("etag-from-server", reference.ETag);
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Put
            && request.Path == "/archive/cold/PROJ/TASK-2/at/payload.zip"
            && request.HasAuthorization
            && request.HasServerChecksum);
        Assert.True((await target.VerifyAsync(reference)).Verified);

        handler.Objects["/archive/cold/PROJ/TASK-2/at/payload.zip"] = "tampered"u8.ToArray();
        Assert.Equal("sha256-mismatch", (await target.VerifyAsync(reference)).Error);
        await target.DeleteAsync(reference.ObjectKey);
        Assert.False((await target.VerifyAsync(reference)).Exists);
    }

    [Fact]
    public async Task S3_target_runs_against_configured_MinIO()
    {
        var endpoint = Environment.GetEnvironmentVariable("MINIO_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint)) return;
        using var target = new S3Target(new S3TargetOptions
        {
            Endpoint = new Uri(endpoint),
            Bucket = Environment.GetEnvironmentVariable("MINIO_BUCKET") ?? "agent-studio-archive",
            Prefix = "integration",
            AccessKey = Environment.GetEnvironmentVariable("MINIO_ACCESS_KEY") ?? "minioadmin",
            SecretKey = Environment.GetEnvironmentVariable("MINIO_SECRET_KEY") ?? "minioadmin",
            PathStyle = true,
            ServerSideChecksum = true,
        });
        var bytes = RandomNumberGenerator.GetBytes(4096);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var key = $"tests/{Guid.NewGuid():N}/payload.zip";
        await using var input = new MemoryStream(bytes);
        var reference = await target.PutAsync(key, input, sha, bytes.Length);
        Assert.True((await target.VerifyAsync(reference)).Verified);
        await target.DeleteAsync(reference.ObjectKey);
        Assert.False((await target.VerifyAsync(reference)).Exists);
    }

    private sealed class InMemoryS3Handler : HttpMessageHandler
    {
        internal Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
        internal List<RequestObservation> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(new RequestObservation(
                request.Method,
                path,
                request.Headers.Authorization?.Scheme == "AWS4-HMAC-SHA256",
                request.Headers.Contains("x-amz-checksum-sha256")));
            if (request.Method == HttpMethod.Put)
            {
                Objects[path] = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag-from-server\"");
                response.Headers.TryAddWithoutValidation("x-amz-checksum-sha256", "server-checksum");
                return response;
            }
            if (request.Method == HttpMethod.Get)
                return Objects.TryGetValue(path, out var bytes)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            if (request.Method == HttpMethod.Delete)
            {
                Objects.Remove(path);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }
    }

    private sealed record RequestObservation(HttpMethod Method, string Path, bool HasAuthorization, bool HasServerChecksum);
}
