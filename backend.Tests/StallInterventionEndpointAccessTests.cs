using System.Net;
using System.Text.Json;
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
    public async Task Scoped_account_reads_only_its_project_and_cannot_undo_a_foreign_receipt()
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
        var store = factory.Services.GetRequiredService<StallInterventionStore>();
        var own = Save("Alpha", "host:alpha:review");
        var foreign = Save("Beta", "host:beta:review");

        using var response = await client.GetAsync("/api/watcher/interventions");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var listed = body.RootElement.GetProperty("interventions").EnumerateArray().ToArray();
        Assert.Single(listed);
        Assert.Equal("Alpha", listed[0].GetProperty("project").GetString());
        Assert.Equal(own.Id, listed[0].GetProperty("receipt").GetProperty("id").GetString());

        using var denied = await client.PostAsync($"/api/watcher/interventions/{foreign.Id}/undo", null);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Null(store.Find(foreign.Id)!.UndoneAtUtc);
        using var allowed = await client.PostAsync($"/api/watcher/interventions/{own.Id}/undo", null);
        Assert.Equal(HttpStatusCode.Conflict, allowed.StatusCode);

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
