using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-3005: the stale build-node sweep decision and the build-server fence the
/// runner injects into preparation and every process it spawns.
/// </summary>
public sealed class BuildNodeSweepPolicyTests
{
    private const int RunnerUid = 1001;
    private const string NodeCommand =
        "/usr/share/dotnet/dotnet /usr/share/dotnet/sdk/10.0.100/MSBuild.dll /nologo /nodemode:1 /nodeReuse:true /low:false";
    private const string CompilerCommand =
        "/usr/share/dotnet/dotnet /usr/share/dotnet/sdk/10.0.100/Roslyn/bincore/VBCSCompiler.dll -pipename:abc";
    private static readonly DateTime Now = new(2026, 9, 29, 15, 0, 0, DateTimeKind.Utc);

    private static BuildNodeSweepContext Context(IEnumerable<int>? pids = null, IEnumerable<string>? paths = null)
        => new(RunnerUid, Now, (pids ?? []).ToHashSet(), (paths ?? []).ToArray());

    private static BuildProcessObservation Node(
        int pid = 500,
        int parent = 1,
        int uid = RunnerUid,
        TimeSpan? age = null,
        string command = NodeCommand,
        string? cwd = "/home/runner/old")
        => new(pid, parent, uid, "dotnet", command, Now - (age ?? TimeSpan.FromHours(101)), cwd);

    private static BuildNodeVerdict Decide(BuildProcessObservation node, BuildNodeSweepContext context, params BuildProcessObservation[] others)
    {
        var all = others.Append(node).ToDictionary(process => process.Pid);
        return BuildNodeSweepPolicy.Decide(node, pid => all.GetValueOrDefault(pid), context);
    }

    [Theory]
    [InlineData("dotnet", NodeCommand, BuildNodeKind.MsBuildNode)]
    [InlineData("dotnet", "dotnet /sdk/MSBuild.dll -nodemode:8", BuildNodeKind.MsBuildNode)]
    [InlineData("dotnet", CompilerCommand, BuildNodeKind.CompilerServer)]
    [InlineData("VBCSCompiler", "VBCSCompiler", BuildNodeKind.CompilerServer)]
    [InlineData("dotnet", "/usr/bin/dotnet build-server shutdown", BuildNodeKind.BuildServerCommand)]
    [InlineData("dotnet", "dotnet build backend/OrchestratorApi.csproj", BuildNodeKind.None)]
    [InlineData("dotnet", "dotnet /sdk/MSBuild.dll backend.csproj", BuildNodeKind.None)]
    [InlineData("bash", "bash -lc make", BuildNodeKind.None)]
    public void Classifies_build_server_processes(string comm, string command, BuildNodeKind expected)
        => Assert.Equal(expected, BuildNodeSweepPolicy.Classify(comm, command));

    [Fact]
    public void An_orphaned_old_node_of_the_runner_user_is_stale()
    {
        Assert.Equal(BuildNodeVerdict.Stale, Decide(Node(), Context()));
        Assert.Equal(BuildNodeVerdict.Stale, Decide(Node(command: CompilerCommand), Context()));
    }

    [Fact]
    public void A_node_under_the_systemd_manager_or_another_node_is_stale()
    {
        var manager = new BuildProcessObservation(40, 1, RunnerUid, "systemd", "/lib/systemd/systemd --user", Now.AddDays(-9), "/");
        Assert.Equal(BuildNodeVerdict.Stale, Decide(Node(parent: 40), Context(), manager));
        var otherNode = Node(pid: 41);
        Assert.Equal(BuildNodeVerdict.Stale, Decide(Node(parent: 41), Context(), otherNode));
    }

    [Fact]
    public void A_non_build_process_is_never_touched()
        => Assert.Equal(
            BuildNodeVerdict.NotBuildNode,
            Decide(Node(command: "dotnet build backend.csproj"), Context()));

    [Fact]
    public void Another_users_node_is_never_touched()
        => Assert.Equal(BuildNodeVerdict.ForeignUser, Decide(Node(uid: 0), Context()));

    [Theory]
    [InlineData(0)]
    [InlineData(29)]
    public void A_node_younger_than_thirty_minutes_is_kept(int minutes)
        => Assert.Equal(BuildNodeVerdict.TooYoung, Decide(Node(age: TimeSpan.FromMinutes(minutes)), Context()));

    [Fact]
    public void A_node_exactly_thirty_minutes_old_is_stale()
        => Assert.Equal(BuildNodeVerdict.Stale, Decide(Node(age: BuildNodeSweepPolicy.MinimumAge), Context()));

    [Fact]
    public void An_unknown_start_time_is_kept()
        => Assert.Equal(
            BuildNodeVerdict.TooYoung,
            Decide(Node() with { StartedAtUtc = null }, Context()));

    [Fact]
    public void A_descendant_of_an_active_run_is_kept()
    {
        var worker = new BuildProcessObservation(300, 1, RunnerUid, "agent-host", "agent-host --detached-worker x", Now.AddHours(-2), "/w");
        var shell = new BuildProcessObservation(301, 300, RunnerUid, "sh", "sh -lc dotnet test", Now.AddHours(-1), "/w");
        Assert.Equal(BuildNodeVerdict.ActiveRun, Decide(Node(parent: 301), Context(pids: [300]), worker, shell));
    }

    [Fact]
    public void A_node_rooted_in_an_active_workspace_is_kept_even_when_orphaned()
    {
        Assert.Equal(
            BuildNodeVerdict.ActiveRun,
            Decide(Node(cwd: "/work/AGT-3005/backend"), Context(paths: ["/work/AGT-3005"])));
        Assert.Equal(
            BuildNodeVerdict.Stale,
            Decide(Node(cwd: "/work/AGT-30050/backend"), Context(paths: ["/work/AGT-3005"])));
    }

    [Fact]
    public void A_node_with_a_live_driver_of_another_role_is_kept()
    {
        var driver = new BuildProcessObservation(700, 1, RunnerUid, "dotnet", "dotnet test backend.Tests", Now.AddHours(-1), "/review/x");
        Assert.Equal(BuildNodeVerdict.LiveDriver, Decide(Node(parent: 700), Context(), driver));
    }

    [Fact]
    public void The_dotnet_process_count_includes_hosts_and_build_nodes()
    {
        Assert.True(BuildNodeSweepPolicy.IsDotnetProcess("dotnet", "dotnet build"));
        Assert.True(BuildNodeSweepPolicy.IsDotnetProcess("VBCSCompiler", "VBCSCompiler"));
        Assert.True(BuildNodeSweepPolicy.IsDotnetProcess(".NET ThreadPool", "/usr/share/dotnet/dotnet exec x.dll"));
        Assert.False(BuildNodeSweepPolicy.IsDotnetProcess("bash", "bash -lc ls"));
    }

    [Fact]
    public void The_runner_fence_matches_the_preparation_fence()
    {
        foreach (var (key, value) in PreparationBuildServerFence.Variables)
            Assert.Equal(value, WorkerBuildServerHygiene.Variables[key]);
        Assert.Equal("1", PreparationBuildServerFence.Variables["MSBUILDDISABLENODEREUSE"]);
        Assert.Equal("0", PreparationBuildServerFence.Variables["DOTNET_CLI_USE_MSBUILD_SERVER"]);
    }

    [Fact]
    public async Task Every_process_the_runner_spawns_carries_the_fence_even_with_a_cleared_environment()
    {
        if (OperatingSystem.IsWindows()) return;
        var result = await ProcessRunner.RunAsync(
            "/usr/bin/env",
            [],
            environment: new Dictionary<string, string?> { ["MSBUILDDISABLENODEREUSE"] = "0" },
            clearEnvironment: true);
        Assert.Contains("MSBUILDDISABLENODEREUSE=1", result.StdOut);
        Assert.Contains("DOTNET_CLI_USE_MSBUILD_SERVER=0", result.StdOut);
    }
}
