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

    public Task InitializeAsync()
    {
        Factory = CreateFactory(_workspace);
        using var client = Factory.CreateClient();
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
