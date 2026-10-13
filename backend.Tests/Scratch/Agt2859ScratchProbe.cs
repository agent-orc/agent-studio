using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using Xunit.Abstractions;

namespace AgentStudio.Tests;

// Scratch repro for AGT-2859; not committed.
[Collection(WebApplicationFactorySerialCollection.Name)]
public sealed class Agt2859ScratchProbe(ITestOutputHelper output)
{
    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    public static TheoryData<string, string> Cases => new()
    {
        { "plain", "dispose" }, { "plain", "sigterm" },
        { "fixture", "dispose" }, { "fixture", "sigterm" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Repro(string factoryKind, string stop)
    {
        if (Environment.GetEnvironmentVariable("AGT2859_CASE") != $"{factoryKind}/{stop}") return;
        var ws = Path.Combine(Path.GetTempPath(), "agt2859-probe-" + Guid.NewGuid().ToString("N"));
        var watch = Path.Combine(ws, "projects", "p");
        foreach (var s in TaskStates.All) Directory.CreateDirectory(Path.Combine(watch, s));
        var hold = new Hold();
        WebApplicationFactory<Program> root = factoryKind == "plain" ? new WebApplicationFactory<Program>() : new TestHostFactory();
        var factory = root.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Test");
            b.UseSetting("TaskRepository", ws);
            b.UseSetting("WatchPaths:0:Name", "p");
            b.UseSetting("WatchPaths:0:Path", watch);
            b.UseSetting("WatchPaths:0:RootPath", watch);
            b.UseSetting("WatchPaths:0:RepositoryPath", watch);
            b.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(hold));
        });
        var http = factory.Server.CreateClient();
        var lifetime = factory.Services.GetRequiredService<IHostApplicationLifetime>();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/runners/review-runner-diagnostics/capability-failures");
        request.Headers.Add("X-Hold", "1");
        request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        var inFlight = http.SendAsync(request);
        await hold.Entered;
        Task stopping;
        if (stop == "dispose") stopping = factory.DisposeAsync().AsTask();
        else { kill(Environment.ProcessId, 15); stopping = Task.CompletedTask; }
        // Give the host the chance to stop and dispose its provider.
        var stopped = await Task.WhenAny(stopping, Task.Delay(TimeSpan.FromSeconds(10)));
        output.WriteLine($"{factoryKind}/{stop}: stopping={lifetime.ApplicationStopping.IsCancellationRequested} stopped={lifetime.ApplicationStopped.IsCancellationRequested} disposalCompleted={stopping.IsCompleted}");
        hold.Release();
        try
        {
            var response = await inFlight;
            output.WriteLine($"{factoryKind}/{stop}: response {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        }
        catch (Exception ex)
        {
            output.WriteLine($"{factoryKind}/{stop}: request failed:\n{ex}");
        }
        try { await stopping; } catch (Exception ex) { output.WriteLine("stop: " + ex.Message); }
        await factory.DisposeAsync();
    }

    private sealed class Hold : IStartupFilter
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Entered => _entered.Task;
        public void Release() => _released.TrySetResult();
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, n) =>
            {
                if (context.Request.Headers.ContainsKey("X-Hold")) { _entered.TrySetResult(); await _released.Task; }
                await n(context);
            });
            next(app);
        };
    }
}
