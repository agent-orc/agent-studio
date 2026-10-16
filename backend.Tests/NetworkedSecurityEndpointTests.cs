extern alias Runner;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Security;
using AgentStudio.Clients;
using AgentStudio.Projects;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

using RClient = Runner::AgentRunner.TaskServerClient;
using RClaim = Runner::AgentRunner.RunnerClaimRequest;
using RAcquire = Runner::AgentRunner.RunLeaseAcquireRequest;
using RComplete = Runner::AgentRunner.RemoteRunCompletionRequest;

namespace AgentStudio.Tests;

public sealed class NetworkedSecurityEndpointTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "studio-networked-endpoints-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Enrolled_runner_acquires_and_completes_a_fake_task_on_the_networked_stack()
    {
        const string project = "networked-smoke";
        const string taskKey = "AGT-NETWORKED-SMOKE";
        var projectPath = Path.Combine(_workspace, "projects", project);
        var taskPath = Path.Combine(projectPath, "3-progress", taskKey);
        Directory.CreateDirectory(taskPath);
        Directory.CreateDirectory(Path.Combine(projectPath, "2-ready"));
        File.WriteAllText(Path.Combine(taskPath, "task.json"), JsonSerializer.Serialize(new
        {
            id = taskKey, title = "Networked runner smoke", state = "3-progress",
            order = 1, agent = "codex", kind = "task",
        }));
        File.WriteAllText(Path.Combine(taskPath, "prompt.md"), "Complete the fixture task.");
        File.WriteAllText(Path.Combine(taskPath, "status.md"), "Result: pending.");

        using var factory = BuildFactory(projectPath);
        using var http = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://studio.test"), HandleCookies = false });
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        store.Bootstrap(new BootstrapRequest("first.owner", "correct horse battery staple!", null));
        var enrolled = store.EnrollRunner(store.CreateEnrollment(
            new RunnerEnrollmentRequest("existing-board-name", null, null, null)).Code);
        factory.Services.GetRequiredService<ClientIdentityStore>()
            .EnsureEnrolledRunner(enrolled.Runner.Id, enrolled.Runner.Name);
        factory.Services.GetRequiredService<ProjectSettingsService>()
            .SetExecutionSettings(project, "auto", enrolled.Runner.Id, true);
        using var runner = new RClient(http, enrolled.Runner.Id, authToken: enrolled.Secret);

        var lease = await runner.AcquireLeaseAsync(
            new RAcquire(taskKey, enrolled.Runner.Id, enrolled.Runner.Name, "smoke-host", 42, "codex"), CancellationToken.None);
        Assert.True(lease.Granted);
        var completion = await runner.CompleteRunAsync(new RComplete(
            taskKey, lease.Lease!.LeaseId, lease.Lease.FencingToken,
            enrolled.Runner.Id, "Blocked", Reason: "networked fixture completed",
            GateItems: ["fixture: controlled networked completion"],
            AttemptId: lease.Lease.AttemptId, AuthorityEpoch: lease.Lease.AuthorityEpoch,
            IdempotencyKey: "networked-smoke-completion"), CancellationToken.None);
        Assert.NotNull(completion);
    }

    [Fact]
    public async Task Real_endpoints_bootstrap_session_csrf_and_runner_enrollment_fail_closed()
    {
        using var factory = BuildFactory();
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://studio.test"), HandleCookies = false });

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/tasks")).StatusCode);
        var anonymousManagement = await anonymous.GetAsync("/api/v1/management/status");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousManagement.StatusCode);
        var anonymousManagementBody = await anonymousManagement.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("authentication-required", anonymousManagementBody.GetProperty("error").GetString());
        Assert.Equal("/api/auth/login", anonymousManagementBody.GetProperty("loginUrl").GetString());
        Assert.Contains("Sign in", anonymousManagementBody.GetProperty("message").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.PostAsJsonAsync("/api/clients/register", new { displayName = "open-runner", kind = "service" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/hubs/jobs/negotiate", null)).StatusCode);

        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://studio.test"), HandleCookies = true });
        var bootstrap = await browser.PostAsJsonAsync("/api/auth/bootstrap", new
        {
            username = "first.owner",
            password = "correct horse battery staple!",
            displayName = "First Owner"
        });
        bootstrap.EnsureSuccessStatusCode();
        var auth = await bootstrap.Content.ReadFromJsonAsync<AuthStatusResponse>();
        Assert.NotNull(auth?.CsrfToken);
        Assert.Contains(bootstrap.Headers.GetValues("Set-Cookie"), value =>
            value.Contains("HttpOnly", StringComparison.OrdinalIgnoreCase)
            && value.Contains("Secure", StringComparison.OrdinalIgnoreCase)
            && value.Contains("SameSite=Strict", StringComparison.OrdinalIgnoreCase));
        (await browser.GetAsync("/api/v1/management/status")).EnsureSuccessStatusCode();

        var missingCsrf = await browser.PostAsJsonAsync("/api/auth/runner-enrollments", new { name = "runner-01" });
        Assert.Equal(HttpStatusCode.Forbidden, missingCsrf.StatusCode);

        using var enrollmentRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/runner-enrollments")
        {
            Content = JsonContent.Create(new { name = "runner-01" })
        };
        enrollmentRequest.Headers.Add("X-CSRF-Token", auth!.CsrfToken);
        var enrollmentResponse = await browser.SendAsync(enrollmentRequest);
        enrollmentResponse.EnsureSuccessStatusCode();
        var enrollment = await enrollmentResponse.Content.ReadFromJsonAsync<OneTimeEnrollmentResponse>();
        Assert.StartsWith("enr.", enrollment!.EnrollmentCode);

        var enrolledResponse = await anonymous.PostAsJsonAsync("/api/auth/runner-enroll", new { code = enrollment.EnrollmentCode });
        enrolledResponse.EnsureSuccessStatusCode();
        var enrolled = await enrolledResponse.Content.ReadFromJsonAsync<OneTimeSecretResponse>();
        Assert.StartsWith("rnr.", enrolled!.Secret);

        using var selfRequest = new HttpRequestMessage(HttpMethod.Get, "/api/auth/runner");
        selfRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", enrolled.Secret);
        var selfResponse = await anonymous.SendAsync(selfRequest);
        selfResponse.EnsureSuccessStatusCode();
        var self = await selfResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(enrolled.RunnerId, self.GetProperty("id").GetString());
        Assert.Equal(enrolled.RunnerName,
            factory.Services.GetRequiredService<ClientIdentityStore>().Find(enrolled.RunnerId)?.DisplayName);
        Assert.Equal(enrolled.RunnerName,
            new ClientIdentityStore(factory.Services.GetRequiredService<IConfiguration>(),
                NullLogger<ClientIdentityStore>.Instance).Find(enrolled.RunnerId)?.DisplayName);

        var replay = await anonymous.PostAsJsonAsync("/api/auth/runner-enroll", new { code = enrollment.EnrollmentCode });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        using var runner = new RClient(anonymous, enrolled.RunnerId, authToken: enrolled.Secret);
        Assert.Equal(enrolled.RunnerId, await runner.RegisterAsync(enrolled.RunnerName, "service", CancellationToken.None));
        var claim = await runner.ClaimAsync(new RClaim(
            enrolled.RunnerId, enrolled.RunnerName, "runner-host", 42, "remote-runner", AvailableSlots: 1),
            CancellationToken.None);
        Assert.NotEqual(Runner::AgentRunner.RunnerClaimStatus.Invalid, claim.Status);

        using var drainRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/management/commands")
        {
            Content = JsonContent.Create(new
            {
                kind = "runner-drain", dryRun = false, confirmation = "runner-drain",
                idempotencyKey = "networked-runner-drain", runnerId = enrolled.RunnerId,
            })
        };
        drainRequest.Headers.Add("X-CSRF-Token", auth.CsrfToken);
        drainRequest.Headers.Add("Idempotency-Key", "networked-runner-drain");
        var drained = await browser.SendAsync(drainRequest);
        drained.EnsureSuccessStatusCode();
        var managementStatus = await browser.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/management/status");
        Assert.Equal("draining", managementStatus.GetProperty("runners")[0].GetProperty("state").GetString());
        Assert.True(File.Exists(Path.Combine(_workspace, ".audit", "management.jsonl")));

        using var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        logoutRequest.Headers.Add("X-CSRF-Token", auth.CsrfToken);
        Assert.Equal(HttpStatusCode.NoContent, (await browser.SendAsync(logoutRequest)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/tasks")).StatusCode);
    }

    private WebApplicationFactory<Program> BuildFactory(string? projectPath = null) => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseEnvironment("Test");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _workspace,
            ["Security:Profile"] = "networked",
            ["AllowedHosts"] = "studio.test",
            ["WatchPaths:0:Name"] = projectPath is null ? null : "networked-smoke",
            ["WatchPaths:0:Path"] = projectPath,
            ["WatchPaths:0:RootPath"] = projectPath,
            ["WatchPaths:0:RepositoryPath"] = projectPath,
        }));
    });

    public void Dispose()
    {
        try { Directory.Delete(_workspace, true); } catch { }
    }
}
