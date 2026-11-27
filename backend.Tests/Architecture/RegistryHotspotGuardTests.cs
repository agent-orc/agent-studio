using System.Diagnostics;
using Xunit;

namespace AgentStudio.Tests.Architecture;

[Trait("Category", "Guard")]
public sealed class RegistryHotspotGuardTests
{
    [Fact]
    public async Task New_registry_hotspots_have_a_recorded_split_generation_or_bounded_union_strategy()
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
        foreach (var argument in new[] { "scripts/registry-hotspots.py", "--guard", "--output", output })
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
            Assert.True(process.ExitCode == 0, await stdout + "\n" + await stderr);
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }
}
