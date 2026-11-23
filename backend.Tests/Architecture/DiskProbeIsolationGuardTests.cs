using AgentStudio.Pipeline;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AgentStudio.Tests.Architecture;

/// <summary>
/// No test host measures the disk of the machine that runs the suite
/// (AGT-3866). On 16 November 2026 the gate host's root file system was 95 %
/// full; the batch gate pilot measured it through <c>TaskRepository</c>,
/// returned from every tick and failed twelve unrelated tests, which the
/// gate then classified as an environment failure of three cards.
/// </summary>
[Trait("Category", "Guard")]
public sealed class DiskProbeIsolationGuardTests
{
    private const string ProbeOwner = "contracts/TaskServer.Contracts/HostDiskPressure.cs";
    private const string PinStatement = "HostFilesystemProbes.Default = FixedHostFilesystemProbe.Healthy";

    [Fact]
    public void This_test_process_pins_the_default_disk_probe()
        => Assert.IsNotType<SystemHostFilesystemProbe>(HostFilesystemProbes.Default);

    [Fact]
    public void A_test_host_constructs_the_pilot_without_the_real_file_system_probe()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "atp-disk-probe-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["TaskRepository"] = workspace }));
                builder.ConfigureTestServices(services => services.RemoveAll<IHostedService>());
            });
            var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();

            Assert.IsNotType<SystemHostFilesystemProbe>(pilot.DiskProbe);
            Assert.Same(factory.Services.GetRequiredService<SshGateHostProbe>(),
                factory.Services.GetRequiredService<IGateHostProbe>());
            Assert.IsNotType<SystemHostFilesystemProbe>(
                factory.Services.GetRequiredService<IHostFilesystemProbe>());
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); }
            catch (IOException) { }
        }
    }

    /// <summary>
    /// Every test project that loads the backend or the runner, and with them
    /// a disk-pressure consumer, pins the probe in a module initializer.
    /// </summary>
    [Fact]
    public void Every_test_project_that_loads_a_disk_consumer_pins_the_probe()
    {
        var root = CSharpSourceScanner.RepoRoot();
        var missing = Directory.EnumerateFiles(root, "*.Tests.csproj", SearchOption.AllDirectories)
            .Where(project => !project.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Where(project => File.ReadAllText(project) is var text
                && (text.Contains(@"..\backend\", StringComparison.Ordinal)
                    || text.Contains("../backend/", StringComparison.Ordinal)
                    || text.Contains(@"..\runner\", StringComparison.Ordinal)
                    || text.Contains("../runner/", StringComparison.Ordinal)))
            .Select(Path.GetDirectoryName)
            .Where(directory => !Directory.EnumerateFiles(directory!, "*.cs", SearchOption.TopDirectoryOnly)
                .Select(File.ReadAllText)
                .Any(source => source.Contains("[ModuleInitializer]", StringComparison.Ordinal)
                    && source.Contains(PinStatement, StringComparison.Ordinal)))
            .Select(directory => Path.GetFileName(directory!))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "Each of these test projects loads a disk-pressure consumer without a [ModuleInitializer] "
            + $"that runs `{PinStatement};` (see backend.Tests/DiskProbeBootstrap.cs): "
            + string.Join(", ", missing));
    }

    /// <summary>
    /// Production code measures disk only through <see cref="IHostFilesystemProbe"/>,
    /// so a new consumer cannot bypass the pinned probe, and no test reaches
    /// for the real probe.
    /// </summary>
    [Fact]
    public void Only_the_system_probe_measures_the_real_file_system()
    {
        var root = CSharpSourceScanner.RepoRoot();
        var production = new[] { "backend", "contracts", "runner", "task-server", "operations-server" }
            .Where(folder => Directory.Exists(Path.Combine(root, folder)))
            .SelectMany(folder => CSharpSourceScanner.SourceFiles(root, folder))
            .Select(path => (File: CSharpSourceScanner.Relative(root, path),
                Code: CSharpSourceScanner.MaskCommentsAndLiterals(CSharpSourceScanner.Read(path))))
            .Where(source => source.File != ProbeOwner
                && source.Code.Contains("HostFilesystemProbe.Measure(", StringComparison.Ordinal))
            .Select(source => source.File);
        var tests = new[] { "backend.Tests", "runner.Tests", "task-server.Tests", "orchestrator-engine.Tests" }
            .Where(folder => Directory.Exists(Path.Combine(root, folder)))
            .SelectMany(folder => CSharpSourceScanner.SourceFiles(root, folder))
            .Select(path => (File: CSharpSourceScanner.Relative(root, path),
                Code: CSharpSourceScanner.MaskCommentsAndLiterals(CSharpSourceScanner.Read(path))))
            .Where(source => !source.File.EndsWith("/DiskProbeIsolationGuardTests.cs", StringComparison.Ordinal)
                && (source.Code.Contains("SystemHostFilesystemProbe.Instance", StringComparison.Ordinal)
                    || source.Code.Contains("HostFilesystemProbe.Measure(", StringComparison.Ordinal)))
            .Select(source => source.File);
        var violations = production.Concat(tests).ToList();

        Assert.True(violations.Count == 0,
            "Measure disk through an injected IHostFilesystemProbe (default HostFilesystemProbes.Default); "
            + "tests use FixedHostFilesystemProbe:\n  " + string.Join("\n  ", violations));
    }
}
