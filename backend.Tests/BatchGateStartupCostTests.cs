using System.Diagnostics;
using AgentStudio.Pipeline;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace AgentStudio.Tests;

public sealed class BatchGateStartupCostTests(ITestOutputHelper output)
{
    [Fact]
    public void Test_state_root_belongs_to_the_suite()
    {
        Assert.StartsWith(TestTempRoot.SuiteRoot + Path.DirectorySeparatorChar,
            LocalStateRoot.Resolve(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(50_000)]
    [InlineData(200_000)]
    [Trait("Category", "MachineBound")]
    public void Preparation_does_not_probe_siblings(int count)
    {
        using var temp = new TempWorkspace("batch-start-cost");
        var state = Path.Combine(temp.Path, "state");
        var workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        var logger = new CaptureLogger();
        void Prepare() => BatchGateStorageRoot.Prepare(temp.Path, workspace, logger, state);
        Prepare(); // Warm identity creation and the runtime before timing.
        var empty = Measure(Prepare);
        var instances = Path.Combine(state, "agentstudio", "instances");
        for (var i = 0; i < count; i++)
            Directory.CreateDirectory(Path.Combine(instances, "sibling-" + i));
        // A warning here proves that startup inspected a sibling.
        var manifest = Path.Combine(instances, "sibling-0", "batch-gates", "batch", "manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(manifest, "{}");
        logger.Messages.Clear();
        File.WriteAllText(Path.Combine(temp.Path, "trace-preparation-start"), "");
        var populated = Measure(Prepare);
        File.WriteAllText(Path.Combine(temp.Path, "trace-preparation-stop"), "");
        output.WriteLine($"Prepare siblings=0 median_ms={empty.TotalMilliseconds:F3}; siblings={count} median_ms={populated.TotalMilliseconds:F3}");
        Assert.Empty(logger.Messages);
        var tolerance = TimeSpan.FromMilliseconds(250);
        Assert.True(populated <= empty + tolerance,
            $"Prepare exceeded the named 250 ms sibling tolerance: empty={empty}, populated={populated}");
    }

    private static TimeSpan Measure(Action action)
    {
        var samples = Enumerable.Range(0, 5).Select(_ =>
        {
            var timer = Stopwatch.StartNew();
            action();
            return timer.Elapsed;
        }).Order().ToArray();
        return samples[2];
    }

    private sealed class CaptureLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
