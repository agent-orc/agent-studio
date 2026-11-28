using System.Diagnostics;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Xunit;
using Xunit.Abstractions;

namespace AgentStudio.Tests;

[CollectionDefinition("Instance startup measurement", DisableParallelization = true)]
public sealed class InstanceStartupMeasurementCollection { }

[Collection("Instance startup measurement")]
public sealed class BatchGateHostStartupCostTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(50_000)]
    [InlineData(200_000)]
    [Trait("Category", "MachineBound")]
    public async Task Host_start_cost_is_independent_of_sibling_count(int count)
    {
        using var temp = new TempWorkspace("instance-host-cost");
        var instances = Path.Combine(LocalStateRoot.Resolve(), "agentstudio", "instances");
        var prefix = "cost-" + Guid.NewGuid().ToString("N") + "-";
        await Start(); // Warm compilation and process-wide caches.
        var empty = await Measure();
        try
        {
            for (var i = 0; i < count; i++)
                Directory.CreateDirectory(Path.Combine(instances, prefix + i));
            var populated = await Measure();
            output.WriteLine($"API host siblings=0 median_ms={empty.TotalMilliseconds:F3}; siblings={count} median_ms={populated.TotalMilliseconds:F3}");
            var tolerance = TimeSpan.FromSeconds(1);
            Assert.True(populated <= empty + tolerance,
                $"Host exceeded the named 1 s sibling tolerance: empty={empty}, populated={populated}");
        }
        finally
        {
            for (var i = 0; i < count; i++)
            {
                var path = Path.Combine(instances, prefix + i);
                if (Directory.Exists(path)) Directory.Delete(path);
            }
        }

        async Task<TimeSpan> Start()
        {
            var workspace = Path.Combine(temp.Path, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workspace);
            await using var factory = new TestHostFactory().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseTestSettings(new Dictionary<string, string?> { ["TaskRepository"] = workspace });
            });
            var timer = Stopwatch.StartNew();
            _ = factory.Server;
            return timer.Elapsed;
        }

        async Task<TimeSpan> Measure()
        {
            var samples = new List<TimeSpan>();
            for (var i = 0; i < 3; i++) samples.Add(await Start());
            return samples.Order().ElementAt(1);
        }
    }
}
