using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Security;
using AgentStudio.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ControlPlaneAuditMutationTests(Xunit.Abstractions.ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "audit-mutations-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Concurrent_256_audited_mutations_across_16_cards_preserve_the_chain()
    {
        var clock = new FakeTimeProvider();
        var project = Path.Combine(_root, "project");
        Directory.CreateDirectory(project);
        using var factory = new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["Security:Profile"] = "networked",
                ["Logging:LogLevel:Default"] = "Warning",
                ["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning",
                ["AllowedHosts"] = "studio.test",
                ["DeliveryChain:Guarded"] = "false",
                ["Secrets:KeyFile"] = Path.Combine(_root, "keys", "secrets.kek"),
                ["Secrets:KeyEnvironmentVariable"] = "AGENT_STUDIO_TEST_UNSET_KEK_" + Guid.NewGuid().ToString("N"),
                ["WatchPaths:0:Name"] = "audit",
                ["WatchPaths:0:Path"] = project,
                ["WatchPaths:0:RootPath"] = project,
            }));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                TestHostFactory.RemoveBackgroundHostedServices(services);
            });
        });
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var owner = store.Bootstrap(new("audit.owner", "correct horse battery staple!", null)).User;
        var account = store.CreateServiceAccount(new("audit regression", null,
            [new(IamRoles.Developer, IamScope.ForProject("audit"))]), owner.Id);
        var token = store.CreateServiceAccountToken(account.Id, new("regression", null), owner.Id);
        using var client = factory.CreateClient(new() { BaseAddress = new("https://studio.test"), HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Secret);
        var cards = new List<string>();
        for (var card = 0; card < 16; card++)
        {
            using var created = await client.PostAsJsonAsync("/api/tasks", new
            {
                id = $"audit-card-{card}", title = $"Audit card {card}", watchPath = project,
            });
            Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
            cards.Add((await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!);
        }
        var log = store.ControlPlaneAudit;
        var monitor = new ControlPlaneAuditMonitor(log, new("none", null, null, null), clock);
        using var verifier = new ControlPlaneAuditVerifier(log, monitor, null, clock);
        var first = log.HeadSequence + 1;
        var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        log.VerificationSnapshotCaptured = () =>
        {
            captured.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(60)), "Card mutations did not finish.");
        };
        var verifying = Task.Run(() => verifier.VerifyAsync(CancellationToken.None));
        try
        {
            await captured.Task.WaitAsync(TimeSpan.FromSeconds(60));
            await Task.WhenAll(cards.Select(async card =>
            {
                for (var mutation = 0; mutation < 16; mutation++)
                {
                    using var moved = await client.PostAsJsonAsync($"/api/tasks/{card}/move",
                        new MoveJobRequest { TargetState = mutation % 2 == 0 ? TaskStates.Preparation : TaskStates.Backlog });
                    Assert.True(moved.IsSuccessStatusCode, await moved.Content.ReadAsStringAsync());
                    if (log.HighestWritten >= first) release.Set();
                }
            }));
        }
        finally
        {
            log.VerificationSnapshotCaptured = null;
            release.Set();
            await verifying;
        }
        var entries = log.Read(first, 1000);
        Assert.Equal(256, entries.Count);
        Assert.All(cards, card => Assert.Equal(16, entries.Count(entry => entry.Operation == $"POST /api/tasks/{card}/move")));
        var chain = log.VerifyChain();
        Assert.True(chain.Ok, chain.Message);
        var all = log.Read(1, int.MaxValue);
        Assert.Equal(Enumerable.Range(1, all.Count).Select(index => (long)index), all.Select(entry => entry.Sequence));
        var during = await verifying;
        Assert.True(during.State == ControlPlaneAuditIntegrityStates.Verified, during.Message);
        Assert.Equal(ControlPlaneAuditIntegrityStates.Verified, (await verifier.VerifyAsync(CancellationToken.None)).State);
        Assert.Empty(monitor.Raised);
        output.WriteLine($"256 successful HTTP mutations, 16 cards, {all.Count} contiguous entries; hashes verified.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
