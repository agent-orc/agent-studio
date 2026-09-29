using System.Text;
using System.Diagnostics;
using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class WorkstationProfileTests
{
    [Fact]
    public void Workstation_options_parse_named_roots_and_bounded_preview()
    {
        using var temp = new TempDirectory();
        var (options, _, _, _) = RunnerOptions.Parse(
        [
            "--workstation", "1",
            "--workstation-roots", $"studio={temp.Path}",
            "--workstation-tools", "dotnet",
            "--preview-origin", "https://preview.example.test",
            "--preview-lifetime-seconds", "1800",
        ]);

        Assert.True(options.IsWorkstation);
        Assert.Equal("local-repo:studio", Assert.Single(options.WorkstationRepositoryRoots).CapabilityKey);
        Assert.Equal("toolchain:dotnet", $"toolchain:{Assert.Single(options.WorkstationRequiredTools)}");
        Assert.Single(RunnerCapabilityProbe.Advertise(options, gitPushReady: false),
            capability => capability.Key == "toolchain:dotnet");
        Assert.Equal(1800, options.WorkstationPreview!.MaxLifetimeSeconds);
        Assert.Throws<ArgumentException>(() => RunnerOptions.Parse(
            ["--workstation", "0", "--workstation-roots", $"studio={temp.Path}"]));
    }

    [Fact]
    public void Local_source_must_be_inside_an_existing_named_root()
    {
        using var temp = new TempDirectory();
        var root = Path.Combine(temp.Path, "repos");
        var included = Path.Combine(root, "project");
        var sibling = Path.Combine(temp.Path, "repos-other");
        Directory.CreateDirectory(included);
        Directory.CreateDirectory(sibling);
        var roots = WorkstationProfile.ParseRoots($"studio={root}");

        Assert.True(WorkstationProfile.TryAdmitSource(included, roots, out _));
        Assert.True(WorkstationProfile.TryAdmitSource(new Uri(included).AbsoluteUri, roots, out _));
        Assert.False(WorkstationProfile.TryAdmitSource(sibling, roots, out var reason));
        Assert.Equal("local-repository-root-not-authorized", reason);
        Assert.False(WorkstationProfile.TryAdmitSource("../repos-other", roots, out _));
        Assert.True(WorkstationProfile.TryAdmitSource("https://example.test/repo.git", roots, out _));
    }

    [Fact]
    public void Missing_workstation_tool_is_rejected_before_execution_and_advertised_unavailable()
    {
        var options = Options("absent-workstation-tool-2940");
        Assert.False(WorkstationProfile.TryAdmitClaim(options, "https://example.test/repo.git", null, out var reason));
        Assert.Equal("workstation-tool-unavailable:absent-workstation-tool-2940", reason);
        var capability = Assert.Single(RunnerCapabilityProbe.Advertise(options, gitPushReady: false),
            item => item.Key == "toolchain:absent-workstation-tool-2940");
        Assert.Equal("unavailable", capability.Status);
        Assert.Contains("toolchain:absent-workstation-tool-2940",
            RunnerCapabilityProbe.CodingHostRequirements(options));
    }

    [Fact]
    public void Claimed_placement_capabilities_are_checked_again_at_worker_start()
    {
        var options = Options();
        Assert.False(WorkstationProfile.TryAdmitClaim(
            options, "https://example.test/repo.git",
            ["toolchain:absent-workstation-tool-2940"], out var toolReason));
        Assert.Equal("workstation-tool-unavailable:absent-workstation-tool-2940", toolReason);
        Assert.False(WorkstationProfile.TryAdmitClaim(
            options, "https://example.test/repo.git",
            ["local-repo:unconfigured"], out var rootReason));
        Assert.Equal("workstation-root-unavailable:local-repo:unconfigured", rootReason);
    }

    [Fact]
    public void Preview_artifact_requires_declared_origin_reachability_and_bounded_expiry()
    {
        var now = new DateTimeOffset(2026, 9, 27, 8, 0, 0, TimeSpan.Zero);
        var preview = WorkstationProfile.ParsePreview(
            "https://preview.example.test", "operator-browser", "3600")!;
        byte[] Artifact(string url, string expires) => Encoding.UTF8.GetBytes(
            $$"""{"url":"{{url}}","reachableFrom":"operator-browser","createdAtUtc":"2026-09-27T08:00:00Z","expiresAtUtc":"{{expires}}"}""");

        Assert.True(WorkstationProfile.TryValidatePreviewArtifact(
            Artifact("https://preview.example.test/run/42", "2026-09-27T08:30:00Z"),
            preview, now, out _));
        Assert.False(WorkstationProfile.TryValidatePreviewArtifact(
            Artifact("file:///C:/shared/preview", "2026-09-27T08:30:00Z"),
            preview, now, out _));
        Assert.False(WorkstationProfile.TryValidatePreviewArtifact(
            Artifact("https://preview.example.test/run/42", "2026-09-27T10:00:00Z"),
            preview, now, out _));
        Assert.False(WorkstationProfile.TryValidatePreviewArtifact(
            Artifact("https://preview.example.test/run/42?token=secret", "2026-09-27T08:30:00Z"),
            preview, now, out _));
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public void Windows_workstation_checks_real_root_and_powershell_tool()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TempDirectory();
        var repository = Path.Combine(temp.Path, "repository");
        Directory.CreateDirectory(repository);
        var options = Options("powershell", temp.Path);
        Assert.True(WorkstationProfile.TryAdmitClaim(options, repository, null, out _));
        Assert.Contains(RunnerCapabilityProbe.Advertise(options, gitPushReady: false),
            capability => capability.Key == "platform:windows" && capability.Status == "ready");
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    [Trait("Category", "ReviewFlaky")]
    public async Task Windows_fenced_process_cleanup_uses_the_recorded_generation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "-NoProfile", "-Command", "Start-Sleep -Seconds 60" },
        })!;
        try
        {
            Assert.True(ProcessSignalGuard.TryKillTree(
                process.Id, "workstation-test", process.StartTime.ToUniversalTime()));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(deadline.Token);
            Assert.True(process.HasExited);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    private static RunnerOptions Options(string? tool = null, string? root = null) => new()
    {
        ServerUrl = "http://127.0.0.1:5031",
        RunnerId = "workstation-test",
        RunnerName = "workstation-test",
        Hostname = "workstation",
        BackendName = "test",
        IsWorkstation = true,
        WorkstationRepositoryRoots = root is null ? [] : [new WorkstationRepositoryRoot("test", root)],
        WorkstationRequiredTools = tool is null ? [] : [tool],
        WorkDir = Path.GetTempPath(),
        BaseBranch = "main",
        CliBin = "test",
        CliArgs = "",
    };

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "workstation-profile-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* Best effort cleanup for machine-bound filesystem checks. */ }
        }
    }
}
