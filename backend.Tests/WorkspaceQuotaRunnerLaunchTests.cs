using AgentStudio.Quota;
using AgentStudio.Registry;
using AgentStudio.Runner;
using AgentStudio.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3177: a local coding launch asks the workspace quota at the same
/// boundary that rejects provider quota caps, before any CLI is spawned.
/// </summary>
public sealed class WorkspaceQuotaRunnerLaunchTests : IDisposable
{
    private const string ProjectName = "quota-runner";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "quota-runner-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Launch_is_rejected_as_quota_cap_while_the_project_quota_is_reached()
    {
        var watch = Path.Combine(_root, "projects", ProjectName);
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(watch, state));
        var job = Path.Combine(watch, TaskStates.Ready, "quota-card");
        Directory.CreateDirectory(job);
        File.WriteAllText(Path.Combine(job, "task.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            id = "quota-card", title = "Quota card", state = TaskStates.Ready, agent = "codex",
            createdAt = _clock.GetUtcNow().UtcDateTime,
        }));
        File.WriteAllText(Path.Combine(job, "prompt.md"), "Do the work.");
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Test")
                .ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _root, ["PublicDemo:Enabled"] = "false",
                    ["WatchPaths:0:Name"] = ProjectName, ["WatchPaths:0:Path"] = watch,
                    ["WatchPaths:0:RootPath"] = watch, ["WatchPaths:0:RepositoryPath"] = watch,
                }))
                .ConfigureTestServices(services =>
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton<TimeProvider>(_clock);
                }));
        var services = factory.Services;
        var workspace = services.GetRequiredService<WorkspaceRegistry>().EnsureDefaultWorkspace(_clock);
        var projects = services.GetRequiredService<ProjectRegistry>();
        var project = projects.FindByIdOrDisplayName(ProjectName)
            ?? projects.EnsureProjectForStorage(watch, ProjectName, workspace.Id, _clock);
        var quota = services.GetRequiredService<WorkspaceQuotaService>();
        var runner = ActivatorUtilities.CreateInstance<ProjectRunner>(services, ProjectName, new WatchPathEntry
        {
            Name = ProjectName, Path = watch, RootPath = watch, RepositoryPath = watch,
        }, NullLogger<ProjectRunner>.Instance, AllowLocalCodingForTests.Instance);
        services.GetRequiredService<TaskScannerService>().InvalidateCache();

        Assert.Null(runner.WorkspaceQuotaRefusal());
        quota.SetLimits(project.WorkspaceId, project.Id, new QuotaLimits(null, 0, null));
        var outcome = await runner.StartJobManualAsync("quota-card", CancellationToken.None);
        quota.Override(project.WorkspaceId, project.Id, "owner-1", "Release day");

        // Refused at the launch boundary: no CLI execution, and the card stays in Ready.
        Assert.Null(outcome.Execution);
        Assert.Equal(RunRejectReason.QuotaCapExceeded, outcome.Rejection!.Reason);
        Assert.Contains("Weekly quota reached", outcome.Rejection.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(job));
        Assert.Null(runner.WorkspaceQuotaRefusal());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
