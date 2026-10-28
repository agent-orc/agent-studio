using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.AttemptCredentials;
using Xunit;

namespace TaskServer.Tests;

public sealed class AttemptRootSecretStoreTests
{
    [Fact]
    public async Task Root_secret_is_encrypted_with_external_key_and_rejects_tampering()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Path);
        var keyFile = Path.Combine(temp.Path, "wrapping-key");
        await File.WriteAllTextAsync(keyFile, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var store = new EncryptedAttemptRootSecretStore(Path.Combine(temp.Path, "roots"), keyFile);
        var secret = "private-material-" + Guid.NewGuid().ToString("N");
        await store.WriteAsync("github-app-test", secret, default);
        var path = Path.Combine(temp.Path, "roots", "github-app-test.enc");
        Assert.DoesNotContain(secret, await File.ReadAllTextAsync(path), StringComparison.Ordinal);
        Assert.Equal(secret, await store.ReadAsync("github-app-test", default));
        var envelope = await File.ReadAllBytesAsync(path);
        envelope[^1] ^= 1;
        await File.WriteAllBytesAsync(path, envelope);
        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() => store.ReadAsync("github-app-test", default));
    }

    [Fact]
    public async Task GitHub_app_request_narrows_one_repository_to_read_only_contents()
    {
        using var rsa = RSA.Create(2048);
        var root = new GitHubAppRoot(10, 20, 30, "https://github.com/example/one.git", rsa.ExportRSAPrivateKeyPem());
        var roots = new MemoryRoots(JsonSerializer.Serialize(root));
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var issuer = new GitHubAppTokenIssuer(http, roots);
        var issued = await issuer.IssueReadAsync("repository-one", root.RepositoryUrl, default);
        Assert.Equal(30, issued.RepositoryId);
        using var request = JsonDocument.Parse(handler.Body!);
        Assert.Equal(30, request.RootElement.GetProperty("repository_ids")[0].GetInt64());
        Assert.Single(request.RootElement.GetProperty("repository_ids").EnumerateArray());
        Assert.Equal("read", request.RootElement.GetProperty("permissions").GetProperty("contents").GetString());
        Assert.Single(request.RootElement.GetProperty("permissions").EnumerateObject());
        Assert.Contains("/installations/20/access_tokens", handler.Path);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task GitHub_app_refuses_an_issuer_response_with_another_repository_or_write_grant(
        bool secondRepository, bool writeGrant)
    {
        using var rsa = RSA.Create(2048);
        var root = new GitHubAppRoot(10, 20, 30, "https://github.com/example/one.git", rsa.ExportRSAPrivateKeyPem());
        using var http = new HttpClient(new RecordingHandler(secondRepository, writeGrant));
        var issuer = new GitHubAppTokenIssuer(http, new MemoryRoots(JsonSerializer.Serialize(root)));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            issuer.IssueReadAsync("repository-one", root.RepositoryUrl, default));
    }

    private sealed class MemoryRoots(string content) : IAttemptRootSecretStore
    {
        public Task<string?> ReadAsync(string name, CancellationToken ct) => Task.FromResult<string?>(content);
        public Task WriteAsync(string name, string value, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RecordingHandler(bool secondRepository = false, bool writeGrant = false) : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public string? Path { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri?.AbsolutePath;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var response = new
            {
                token = "opaque-" + Guid.NewGuid().ToString("N"),
                expires_at = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc),
                permissions = new { contents = writeGrant ? "write" : "read", metadata = "read" },
                repositories = secondRepository ? new[] { new { id = 30 }, new { id = 31 } } : [new { id = 30 }],
            };
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json"),
            };
        }
    }
}
