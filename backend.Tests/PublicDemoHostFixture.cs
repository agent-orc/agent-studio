using System.Diagnostics;
using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class PublicDemoHostFixture : IAsyncLifetime
{
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), "studio-public-demo-shared-" + Guid.NewGuid().ToString("N"));

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public TimeSpan InitializationDuration { get; private set; }

    public Task InitializeAsync()
    {
        var timer = Stopwatch.StartNew();
        // The shared host serves every class of the collection from one client
        // address. The route inventory test alone sends one request per
        // execution route, so the default 120-per-minute edge limit would turn
        // later requests into 429s. The limiter itself is not under test here.
        Factory = CreateFactory(_workspace, new Dictionary<string, string?>
        {
            ["Security:PublicDemoEdge:RateLimit:PermitLimit"] = "1000",
        });
        using var client = Factory.CreateClient();
        InitializationDuration = timer.Elapsed;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        TestTempRoot.TryDelete(_workspace);
    }

    public static WebApplicationFactory<Program> CreateFactory(
        string workspace,
        IReadOnlyDictionary<string, string?>? overrides = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["TaskRepository"] = workspace,
            ["Security:Profile"] = ExecutionAdmissionPolicy.PublicDemoProfile,
        };
        if (overrides is not null)
            foreach (var (key, value) in overrides)
                values[key] = value;

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseTestSettings(values);
        });
    }
}
