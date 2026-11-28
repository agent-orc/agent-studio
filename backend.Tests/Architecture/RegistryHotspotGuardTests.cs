using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace AgentStudio.Tests.Architecture;

[Trait("Category", "Guard")]
public sealed class RegistryHotspotGuardTests(ITestOutputHelper log)
{
    [Fact]
    public Task New_registry_hotspots_have_a_recorded_split_generation_or_bounded_union_strategy() =>
        RunPython("scripts/registry-hotspots.py", "--guard");

    [Fact]
    public Task Measurement_fixtures_cover_missing_source_objects_and_card_attribution() =>
        RunPython("scripts/test_registry_hotspots.py");

    private async Task RunPython(params string[] arguments)
    {
        var root = CSharpSourceScanner.RepoRoot();
        var output = Path.Combine(Path.GetTempPath(), "registry-guard-" + Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "python" : "python3")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // Let the CLI publish its report to the job directory when the host supplies one.
        var useTemp = arguments.Contains("--guard") && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("JOB_RESULTS_DIR"));
        foreach (var argument in arguments.Concat(useTemp
                     ? new[] { "--output", output } : Array.Empty<string>()))
            start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw;
            }
            log.WriteLine(await stdout);
            Assert.True(process.ExitCode == 0, await stdout + "\n" + await stderr);
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }
}
