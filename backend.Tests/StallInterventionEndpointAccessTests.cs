using System.Net;
using AgentStudio.Security;
using AgentStudio.Watcher;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class StallInterventionEndpointAccessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stall-access-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Project_scoped_account_can_neither_read_nor_undo_receipts_of_any_project()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["Security:Profile"] = "networked",
                ["AllowedHosts"] = "studio.test",
                ["Watcher:Enabled"] = "false",
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(_clock);
            });
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://studio.test"), HandleCookies = false });
        var security = factory.Services.GetRequiredService<AccessSecurityStore>();
        security.Bootstrap(new BootstrapRequest("owner", "correct horse battery staple!", null));
        var created = security.CreateUser(new CreateUserRequest("scoped", "Scoped", StudioRoles.Operator,
            ["Alpha"], null));
        var login = security.Login("scoped", created.TemporaryPassword, "scoped|test");
        security.ChangePassword(security.AuthenticateSession(login.SessionToken)!,
            new ChangePasswordRequest(created.TemporaryPassword, "another long passphrase!"));
        client.DefaultRequestHeaders.Add("Cookie", $"{AccessSecurityStore.SessionCookieName}={login.SessionToken}");
        client.DefaultRequestHeaders.Add("X-CSRF-Token", login.CsrfToken);
        // A session request proves it came from a Studio page (AGT-3230).
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-origin");
        var store = factory.Services.GetRequiredService<StallInterventionStore>();
        var own = Save("Alpha", "host:alpha:review");
        var foreign = Save("Beta", "host:beta:review");

        // The Watcher routes are installation-wide and need workspace.operate
        // (develop's IAM catalogue); the endpoint's own project filter stays
        // as the second layer for workspace accounts.
        using var response = await client.GetAsync("/api/watcher/interventions");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("workspace.operate", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(own.Id, await response.Content.ReadAsStringAsync());

        foreach (var record in new[] { own, foreign })
        {
            using var undo = await client.PostAsync($"/api/watcher/interventions/{record.Id}/undo", null);
            Assert.Equal(HttpStatusCode.Forbidden, undo.StatusCode);
            Assert.Null(store.Find(record.Id)!.UndoneAtUtc);
        }

        StallInterventionRecord Save(string project, string subject)
        {
            var key = "scope-test:" + subject;
            return store.Save(new StallInterventionRecord
            {
                Id = StallInterventionStore.IdFor(key), WriteKey = key, Project = project,
                Subject = subject, Kind = StallKinds.IdleCapacity, Host = subject.Split(':')[1],
                Action = StallInterventionActions.ProposeRestartService, Autonomy = "A2",
                Disposition = "Propose", ReasonCode = "running-work", Status = StallInterventionStatuses.Proposed,
                DecidedAtUtc = _clock.GetUtcNow().UtcDateTime,
            });
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
