using AgentStudio.Setup;
using Xunit;

namespace AgentOrchestratorSetup.Tests;

public sealed class LinuxInstallerTests
{
    [Theory]
    [InlineData("/api/v1/studio/auth/status", "http://127.0.0.1:5072")]
    [InlineData("/hubs/events", "http://127.0.0.1:5072")]
    [InlineData("/api/tasks", "http://127.0.0.1:5031")]
    [InlineData("/healthz", "http://127.0.0.1:5031")]
    [InlineData("/readyz", "http://127.0.0.1:5031")]
    public void Native_web_routes_match_compose(string route, string expected)
        => Assert.Equal(expected, NativeWebHost.UpstreamFor(route,
            "http://127.0.0.1:5031", "http://127.0.0.1:5072"));

    [Fact]
    public void Native_web_serves_static_routes_locally()
        => Assert.Null(NativeWebHost.UpstreamFor("/projects", "api", "bff"));

    [Fact]
    public void Join_token_file_is_owner_only_when_created()
    {
        if (!OperatingSystem.IsLinux()) return;
        var path = Path.Combine(Path.GetTempPath(), $"studio-token-{Guid.NewGuid():N}");
        try
        {
            SetupSecrets.WriteOwnerOnly(path, "fixture-token");
            Assert.Equal("fixture-token", File.ReadAllText(path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(path));
            Assert.Throws<IOException>(() => SetupSecrets.WriteOwnerOnly(path, "replacement"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Refused_review_replacement_restores_the_active_host_release()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), $"studio-host-switch-{Guid.NewGuid():N}");
        try
        {
            var oldRelease = Path.Combine(root, "old");
            var newRelease = Path.Combine(root, "new");
            Directory.CreateDirectory(oldRelease);
            Directory.CreateDirectory(newRelease);
            var current = Path.Combine(root, "current");
            var previous = Path.Combine(root, "previous");
            Directory.CreateSymbolicLink(current, oldRelease);
            Directory.CreateSymbolicLink(previous, oldRelease);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                NativeLifecycle.ActivateHostReleaseAsync(new ProcessRunner(false), current,
                    oldRelease, newRelease, () =>
                    {
                        Assert.Equal(newRelease, Directory.ResolveLinkTarget(current, true)!.FullName);
                        throw new InvalidOperationException("Review restart guard refused");
                    }));
            Assert.Equal(oldRelease, Directory.ResolveLinkTarget(current, true)!.FullName);
            Assert.Equal(oldRelease, Directory.ResolveLinkTarget(previous, true)!.FullName);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Networked_studio_origin_is_allowed_and_preserved_for_updates()
    {
        var origin = "https://studio.example.test";
        var allowed = NativeInstaller.BuildStudioAllowedOrigins(origin + "/");
        Assert.Equal("http://127.0.0.1:4011,http://localhost:4011," + origin, allowed);
        var path = Path.Combine(Path.GetTempPath(), $"studio-bff-unit-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(path, $"[Service]\nEnvironment=\"Studio__AllowedOrigins={allowed}\"\n");
            Assert.Equal(origin, NativeInstaller.ReadInstalledPublicStudioOrigin(path));
            Assert.Equal(allowed, NativeInstaller.BuildStudioAllowedOrigins(
                NativeInstaller.ReadInstalledPublicStudioOrigin(path)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Native_studio_unit_is_loopback_and_owned_by_service_user()
    {
        var unit = NativeInstaller.BuildStudioUnit("Agent Studio API", "/opt/api",
            "/opt", ["ASPNETCORE_URLS=http://127.0.0.1:5031"], "/var/lib/agent-orchestrator");
        Assert.Contains("User=agent-orchestrator", unit);
        Assert.Contains("Environment=\"ASPNETCORE_URLS=http://127.0.0.1:5031\"", unit);
        Assert.Contains("ExecStart=/opt/api", unit);
        Assert.Contains("ReadWritePaths=/var/lib/agent-orchestrator", unit);
    }

    [Fact]
    public void Answer_file_options_have_native_parser_equivalents()
    {
        var command = ProductCommand.Parse(["--mode", "studio", "--target", "native",
            "--git-access", "existing-ssh", "--git-ssh-key-file", "/tmp/id_ed25519"]);
        var plan = ProductPlanner.Plan(command, "studio", "native", windows: false, []);
        Assert.Contains("--git-access", plan.DelegatedArguments);
        var options = SetupOptions.Parse(plan.DelegatedArguments.ToArray());
        Assert.Equal("existing-ssh", options.GitAccess);
        Assert.Equal("/tmp/id_ed25519", options.GitSshKeyFile);
    }

    [Fact]
    public void Native_release_inventory_is_checked_before_service_install()
    {
        var root = Path.Combine(Path.GetTempPath(), $"studio-inventory-{Guid.NewGuid():N}");
        var orchestrator = Path.Combine(root, "orchestrator");
        var studio = Path.Combine(root, "studio");
        try
        {
            foreach (var directory in new[]
                     {
                         Path.Combine(orchestrator, "api"), Path.Combine(orchestrator, "bff"),
                         Path.Combine(studio, "browser"),
                     })
                Directory.CreateDirectory(directory);
            foreach (var file in new[]
                     {
                         Path.Combine(orchestrator, "api", "OrchestratorApi"),
                         Path.Combine(orchestrator, "bff", "agent-studio-bff"),
                         Path.Combine(studio, "browser", "index.html"),
                         Path.Combine(studio, "agent-studio-setup"),
                     })
                File.WriteAllText(file, "fixture");
            NativeInstaller.ValidateStudioRelease(orchestrator, studio);
            File.Delete(Path.Combine(orchestrator, "bff", "agent-studio-bff"));
            Assert.Throws<InvalidDataException>(() =>
                NativeInstaller.ValidateStudioRelease(orchestrator, studio));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Native_rollback_selects_the_retained_release()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), $"studio-rollback-{Guid.NewGuid():N}");
        try
        {
            var orchestrator = Path.Combine(root, "orchestrator");
            Directory.CreateDirectory(Path.Combine(orchestrator, "1.2.3"));
            Directory.CreateSymbolicLink(Path.Combine(orchestrator, "previous"),
                Path.Combine(orchestrator, "1.2.3"));
            var paths = InstallPaths.Load() with { OrchestratorOpt = orchestrator };
            var plan = ProductPlanner.Plan(ProductCommand.Parse(["rollback"]),
                "studio", "native", windows: false, []);
            Assert.Equal("1.2.3", NativeLifecycle.PreviousVersion(plan, paths));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Host_release_requires_the_setup_command_for_later_updates()
    {
        var root = Path.Combine(Path.GetTempPath(), $"host-inventory-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "linux-x64"));
            Directory.CreateDirectory(Path.Combine(root, "systemd"));
            foreach (var file in new[]
                     {
                         Path.Combine(root, "linux-x64", "agent-host"),
                         Path.Combine(root, "systemd", "agent-host.service"),
                         Path.Combine(root, "agent-host-resource-governance.sh"),
                         Path.Combine(root, "agent-studio-setup"),
                     })
                File.WriteAllText(file, "fixture");
            NativeInstaller.ValidateHostRelease(root);
            File.Delete(Path.Combine(root, "agent-studio-setup"));
            Assert.Throws<InvalidDataException>(() => NativeInstaller.ValidateHostRelease(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
