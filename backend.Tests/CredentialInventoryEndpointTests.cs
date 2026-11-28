using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Host;
using AgentStudio.Secrets;
using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts.GitHubApp;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class CredentialInventoryEndpointTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Inventory_routes_join_host_and_platform_metadata_without_values_or_token_prefixes(bool proxied)
    {
        var root = Path.Combine(Path.GetTempPath(), "wi-endpoint-" + Guid.NewGuid().ToString("N"));
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        var edgeCredential = Guid.NewGuid().ToString("N");
        var value = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
        try
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseTestSettings(new Dictionary<string, string?>
                {
                    ["TaskRepository"] = Path.Combine(root, "workspace"),
                    ["Security:Profile"] = "local",
                    ["Secrets:KeyFile"] = Path.Combine(root, "keys", "secrets.kek"),
                    ["Secrets:KeyEnvironmentVariable"] = "WI_TEST_UNSET_" + Guid.NewGuid().ToString("N"),
                    ["TaskServer:BaseUrl"] = proxied ? "https://task-server.example.invalid" : null,
                    ["TaskServer:AuthToken"] = edgeCredential,
                    ["TaskServer:AuthTokenFile"] = null,
                });
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton<TimeProvider>(clock);
                    if (proxied) services.AddHttpClient(TaskServerPlaneProxy.ClientName)
                        .ConfigurePrimaryHttpMessageHandler(() => new MetadataHandler());
                });
            });
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
            var provider = factory.Services.GetRequiredService<ISecretProvider>();
            await provider.PutAsync(SecretScope.Parse("sample/prod"), "DEPLOY_ACCESS", value, SecretActor.Human("owner-a"));
            var security = factory.Services.GetRequiredService<AccessSecurityStore>();
            var account = security.CreateServiceAccount(new CreateServiceAccountRequest("inventory-service", null), "owner-a");
            var issued = security.CreateServiceAccountToken(account.Id, new CreateServiceAccountTokenRequest("inventory-token", null), "owner-a");
            var settings = factory.Services.GetRequiredService<SecretBackendSettings>();
            await settings.SaveAsync(new SecretBackendBinding("workspace", "vault", "https://vault.example.invalid", "secret",
                null, null, "WI_TEST_UNSET_TOKEN_" + Guid.NewGuid().ToString("N")), default);

            var fetch = new GitHubOperationTarget("https", "github.com/example/source");
            var push = new GitHubOperationTarget("ssh", "github.com/example/backup");
            var grant = new GitHubGrantReference("ssh-deploy-key", "54201");
            var observedAt = clock.GetUtcNow();
            var proof = new GitHubOperationSuccess(observedAt.AddMinutes(-1), Guid.NewGuid(),
                grant, 2, new string('a', 40));
            var receipt = new GitHubCallerReceipt("workspace-export", "server-a", "export", "svc-export",
                "task-server", "workspace-job", "active", observedAt, Guid.NewGuid(), fetch, push, grant, null, proof);
            const string receiptRoute = "/api/v1/management/credential-inventory/github-receipt";
            using var accepted = await client.PutAsJsonAsync(receiptRoute, receipt);
            Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
            using var rejected = await client.PutAsJsonAsync(receiptRoute,
                receipt with { LastWrite = proof with { CommitsPublished = 0 } });
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            using var unsafeReceipt = await client.PutAsJsonAsync(receiptRoute,
                receipt with { Push = push with { Repository = $"https://user:{value}@github.com/example/backup" } });
            Assert.Equal(HttpStatusCode.BadRequest, unsafeReceipt.StatusCode);
            using var stale = await client.PutAsJsonAsync(receiptRoute,
                receipt with { State = "disabled", ObservedAt = observedAt.AddMinutes(-2), LastWrite = null });
            Assert.Equal(HttpStatusCode.NoContent, stale.StatusCode);

            using var response = await client.GetAsync("/api/v1/management/credential-inventory");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            var json = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(edgeCredential, json);
            Assert.DoesNotContain(edgeCredential[..12], json);
            Assert.DoesNotContain(value, json);
            Assert.DoesNotContain(value[..12], json);
            Assert.DoesNotContain(issued.Secret, json);
            Assert.DoesNotContain(issued.Secret[..12], json);
            Assert.DoesNotContain("secretHash", json);
            using var document = JsonDocument.Parse(json);
            var items = document.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.Contains(items, item => item.GetProperty("kind").GetString() == "external_store_token");
            Assert.Contains(items, item => item.GetProperty("kind").GetString() == "service_account_token");
            Assert.Contains(items, item => item.GetProperty("label").GetString() == "DEPLOY_ACCESS");
            Assert.All(items, item => Assert.True(item.TryGetProperty("lifetimeClass", out _)));
            var operations = document.RootElement.GetProperty("gitHubOperations");
            var recorded = Assert.Single(operations.GetProperty("receipts").EnumerateArray());
            Assert.Equal("active", recorded.GetProperty("state").GetString());
            Assert.Equal("github.com/example/source", recorded.GetProperty("fetch").GetProperty("repository").GetString());
            Assert.Equal("github.com/example/backup", recorded.GetProperty("push").GetProperty("repository").GetString());
            Assert.Equal("54201", recorded.GetProperty("lastWrite").GetProperty("credential").GetProperty("grantId").GetString());
            if (proxied)
            {
                Assert.Contains(items, item => item.GetProperty("id").GetString() == "remote-registry");
                Assert.Single(operations.GetProperty("writes").EnumerateArray());
                var taskServerApp = operations.GetProperty("appCoverage")[1];
                Assert.Contains(taskServerApp.GetProperty("repositories").EnumerateArray(), value =>
                    value.GetProperty("repository").GetString() == "github.com/example/backup");
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class MetadataHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            object body = request.RequestUri!.AbsolutePath.EndsWith("github-app", StringComparison.Ordinal)
                ? new { connected = true, installations = Array.Empty<object>(), repositories = Array.Empty<object>(),
                    hosts = Array.Empty<object>(), adoptedRepositories = new[] { new {
                        repository = "github.com/example/backup", state = "covered", installationId = 19 } } }
                : new
                {
                    gitHubOperations = new { callers = Array.Empty<object>(), receipts = Array.Empty<object>(),
                        writes = new[] { new { caller = "workspace-export", target = new {
                            transport = "ssh", repository = "github.com/example/backup" },
                            succeededAt = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), commitsToPublish = 2 } },
                        appCoverage = Array.Empty<object>() },
                    generatedAt = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
                    items = new[] { new { id = "remote-registry", category = "repository", hostId = "host-a", kind = "github_deploy_key",
                        label = "Repository fallback", scopes = Array.Empty<string>(), state = "ok", detail = "Metadata only", affectedCards = Array.Empty<string>() } },
                };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
