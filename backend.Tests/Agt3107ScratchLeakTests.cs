using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

public sealed class Agt3107ScratchLeakTests
{
    [Fact]
    public async Task Scratch_hosts_are_collectable()
    {
        var refs = new List<WeakReference>();
        var log = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            refs.Add(await BootOnce());
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            log.Add($"boot {i}: timers={Timer.ActiveCount} alive={refs.Count(r => r.IsAlive)} heap={GC.GetTotalMemory(true) / 1048576}MB");
        }
        await Task.Delay(3000);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        log.Add($"final: timers={Timer.ActiveCount} alive={refs.Count(r => r.IsAlive)} heap={GC.GetTotalMemory(true) / 1048576}MB");
        File.WriteAllLines("/tmp/agt3107-leak.txt", log);
        File.WriteAllText("/tmp/agt3107-leak.pid", Environment.ProcessId.ToString());
        for (var i = 0; i < 300 && !File.Exists("/tmp/agt3107-leak-continue"); i++) await Task.Delay(1000);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> BootOnce()
    {
        var ws = Path.Combine(Path.GetTempPath(), "agt3107-leak-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ws);
        WeakReference weak;
        using (var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Test");
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = ws }));
        }))
        {
            using var client = factory.CreateClient();
            (await client.GetAsync("/api/environment")).EnsureSuccessStatusCode();
            weak = new WeakReference(factory.Services.GetRequiredService<AgentStudio.Runner.TaskRunnerService>());
        }
        return weak;
    }
}
