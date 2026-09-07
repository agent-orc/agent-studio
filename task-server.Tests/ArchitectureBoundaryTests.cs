using Xunit;

namespace TaskServer.Tests;

public sealed class ArchitectureBoundaryTests
{
    [Fact]
    public void Task_server_project_references_contracts_and_host_neutral_retention_only()
    {
        var root = ProtocolTests.RepositoryRoot();
        var project = File.ReadAllText(Path.Combine(root, "task-server", "TaskServer.csproj"));
        Assert.Contains("TaskServer.Contracts.csproj", project);
        Assert.Contains("AgentStudio.Retention.csproj", project);
        Assert.DoesNotContain("frontend", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OrchestratorApi.csproj", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AgentRunner.csproj", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CodingAgentRunner", project, StringComparison.OrdinalIgnoreCase);

        var serverSources = Directory.EnumerateFiles(Path.Combine(root, "task-server"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        foreach (var source in serverSources)
        {
            var text = File.ReadAllText(source);
            Assert.DoesNotContain("System.Diagnostics.Process", text, StringComparison.Ordinal);
            Assert.DoesNotContain("AgentRunner", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Angular", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Production_profile_is_a_self_contained_single_file_with_its_own_backup_timer()
    {
        var root = ProtocolTests.RepositoryRoot();
        var profile = File.ReadAllText(Path.Combine(
            root,
            "task-server",
            "Properties",
            "PublishProfiles",
            "linux-x64.pubxml"));
        Assert.Contains("<RuntimeIdentifier>linux-x64</RuntimeIdentifier>", profile);
        Assert.Contains("<SelfContained>true</SelfContained>", profile);
        Assert.Contains("<PublishSingleFile>true</PublishSingleFile>", profile);
        Assert.Contains("<IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>", profile);

        var timerService = File.ReadAllText(Path.Combine(
            root,
            "deploy",
            "systemd",
            "agent-task-server-backup.service"));
        Assert.Contains("task-server backup --name timer", timerService);
        Assert.Contains("EnvironmentFile=/etc/agent-orchestrator/server.env", timerService);
    }

    [Fact]
    public void Windows_package_is_self_contained_and_runs_under_a_supervised_s4u_task()
    {
        var root = ProtocolTests.RepositoryRoot();
        var profile = File.ReadAllText(Path.Combine(
            root,
            "task-server",
            "Properties",
            "PublishProfiles",
            "win-x64.pubxml"));
        Assert.Contains("<RuntimeIdentifier>win-x64</RuntimeIdentifier>", profile);
        Assert.Contains("<SelfContained>true</SelfContained>", profile);
        Assert.Contains("<PublishSingleFile>true</PublishSingleFile>", profile);

        var registration = File.ReadAllText(Path.Combine(
            root,
            "deploy",
            "windows",
            "task-server",
            "register-task-server.ps1"));
        Assert.Contains("New-ScheduledTaskTrigger -AtStartup", registration);
        Assert.Contains("-LogonType S4U", registration);
        Assert.Contains("-RestartCount 3", registration);
        Assert.DoesNotContain("-LogonType Interactive", registration, StringComparison.OrdinalIgnoreCase);

        var installer = File.ReadAllText(Path.Combine(
            root,
            "deploy",
            "windows",
            "task-server",
            "install-task-server-release.ps1"));
        Assert.Contains("LISTEN_URL = $ListenUrl", installer);
        Assert.Contains("STORE_PATH = $DataDirectory", installer);
        Assert.Contains("Copy-Item -LiteralPath $supervisorScript", installer);
        Assert.Contains("Stop-ScheduledTask -TaskName $TaskName", installer);
        Assert.Contains("-StartScriptPath (Join-Path $current 'start-task-server.ps1')", installer);
        Assert.Contains("TaskServer", installer);
        Assert.Contains("BaseUrl", installer);
    }
}
