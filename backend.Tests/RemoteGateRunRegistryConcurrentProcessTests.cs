using System.Diagnostics;
using AgentStudio.Pipeline;
using AgentStudio.TestSupport;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

[Trait("Category", "MachineBound")]
public sealed class RemoteGateRunRegistryConcurrentProcessTests
{
    private const string ChildRootVariable = "AGENT_STUDIO_REGISTRY_RACE_ROOT";
    private static readonly FakeTimeProvider Clock = new(DateTimeOffset.Parse("2026-10-20T02:00:00Z"));

    [Fact]
    public async Task First_installation_id_is_shared_by_concurrent_processes()
    {
        var childRoot = Environment.GetEnvironmentVariable(ChildRootVariable);
        if (childRoot is not null)
        {
            var pid = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            File.WriteAllText(Path.Combine(childRoot, "ready-" + pid), "ready");
            Assert.True(SpinWait.SpinUntil(() => File.Exists(Path.Combine(childRoot, "go")),
                TimeSpan.FromSeconds(90)));
            var registry = new RemoteGateRunRegistry(Path.Combine(childRoot, "registry"), Clock);
            File.WriteAllText(Path.Combine(childRoot, "id-" + pid), registry.InstallationId);
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "remote-gate-registry-race", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Process? first = null;
        Process? second = null;
        try
        {
            first = Process.Start(ChildInfo(root)) ?? throw new InvalidOperationException("First test process did not start.");
            second = Process.Start(ChildInfo(root)) ?? throw new InvalidOperationException("Second test process did not start.");
            Assert.True(SpinWait.SpinUntil(() => Directory.GetFiles(root, "ready-*").Length == 2,
                TimeSpan.FromSeconds(90)));
            File.WriteAllText(Path.Combine(root, "go"), "go");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await Task.WhenAll(first.WaitForExitAsync(timeout.Token), second.WaitForExitAsync(timeout.Token));
            Assert.True(first.ExitCode == 0 && second.ExitCode == 0,
                $"Registry child exits: {first.ExitCode}, {second.ExitCode}");
            var ids = Directory.GetFiles(root, "id-*").Select(File.ReadAllText).ToArray();
            Assert.Equal(2, ids.Length);
            Assert.Equal(ids[0], ids[1]);
            Assert.Equal(ids[0], new RemoteGateRunRegistry(Path.Combine(root, "registry"), Clock).InstallationId);
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "registry"), "*.tmp"));
        }
        finally
        {
            if (first is { HasExited: false })
            {
                first.Kill(entireProcessTree: true);
                first.WaitForExit(5000);
            }
            if (second is { HasExited: false })
            {
                second.Kill(entireProcessTree: true);
                second.WaitForExit(5000);
            }
            first?.Dispose();
            second?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static ProcessStartInfo ChildInfo(string root)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = RepositoryRoot.Find(),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("test");
        info.ArgumentList.Add("backend.Tests/OrchestratorApi.Tests.csproj");
        info.ArgumentList.Add("--no-build");
        info.ArgumentList.Add("--filter");
        info.ArgumentList.Add("FullyQualifiedName~RemoteGateRunRegistryConcurrentProcessTests");
        info.Environment[ChildRootVariable] = root;
        return info;
    }
}
