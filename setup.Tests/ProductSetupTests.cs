using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using AgentStudio.Setup;
using Xunit;

namespace AgentOrchestratorSetup.Tests;

public sealed class ProductSetupTests
{
    [Theory]
    [InlineData(true, "--mode", "control-plane")]
    [InlineData(true, "--mode", "control-plane", "--target", "native")]
    [InlineData(true, "--mode", "agent-host", "--join-token-file", "join.token")]
    [InlineData(true, "--join")]
    [InlineData(true, "--mode", "connector")]
    [InlineData(true)]
    [InlineData(false, "--mode", "demo")]
    [InlineData(false, "--mode", "single")]
    [InlineData(false, "--mode", "single-machine", "--non-interactive")]
    public void Every_mode_except_demo_and_single_is_routed_through_the_product_parser(
        bool product, params string[] args)
        => Assert.Equal(product, ProductSetup.IsProductCommand(args));

    [Fact]
    public void Control_plane_native_translates_to_systemd_whether_or_not_unattended()
    {
        var interactive = PlanFor(false, "--mode", "control-plane", "--target", "native",
            "--server-url", "https://tasks.example.com");
        var unattended = PlanFor(false, "--mode", "control-plane", "--target", "native",
            "--server-url", "https://tasks.example.com", "--unattended");

        Assert.Equal(
            ["--mode", "control-plane", "--target", "systemd", "--server-url", "https://tasks.example.com"],
            interactive.DelegatedArguments);
        Assert.Equal(
            [.. interactive.DelegatedArguments, "--non-interactive"],
            unattended.DelegatedArguments);
    }

    [Fact]
    public void Legacy_systemd_spelling_and_non_interactive_flag_still_work()
    {
        var plan = PlanFor(false, "--mode", "control-plane", "--target", "systemd", "--non-interactive");

        Assert.Equal("native", plan.Target);
        Assert.Equal(["--mode", "control-plane", "--target", "systemd", "--non-interactive"],
            plan.DelegatedArguments);
    }

    [Fact]
    public void Linux_only_options_are_forwarded_to_the_delegated_agent_host_flow()
    {
        var plan = PlanFor(false, "--join", "--join-token-file", "/secure/join.token",
            "--runner-name", "runner-one", "--role", "review", "--dry-run");

        Assert.Equal(ProductProfile.Delegated, plan.Profile);
        Assert.Equal(
            ["--mode", "agent-host", "--join-token-file", "/secure/join.token",
             "--runner-name", "runner-one", "--role", "review", "--dry-run"],
            plan.DelegatedArguments);
    }

    [Theory]
    [InlineData("studio", "docker", false, "StudioDocker")]
    [InlineData("studio", "docker", true, "StudioDocker")]
    [InlineData("studio", "native", true, "StudioWindowsServices")]
    [InlineData("studio", "native", false, "Delegated")]
    [InlineData("connector", "native", true, "ConnectorWindows")]
    [InlineData("control-plane", "docker", false, "Delegated")]
    [InlineData("control-plane", "native", false, "Delegated")]
    [InlineData("agent-host", "native", false, "Delegated")]
    public void Mode_target_and_platform_select_one_profile(
        string mode, string target, bool windows, string expected)
        => Assert.Equal(expected, ProductPlanner.Plan(Parse(), mode, target, windows, []).Profile.ToString());

    [Theory]
    [InlineData("connector", "native", false)]
    [InlineData("agent-host", "docker", false)]
    [InlineData("control-plane", "native", true)]
    [InlineData("agent-host", "native", true)]
    public void Unavailable_combinations_fail_with_platform_guidance(string mode, string target, bool windows)
        => Assert.Throws<PlatformNotSupportedException>(
            () => ProductPlanner.Plan(Parse(), mode, target, windows, []));

    [Fact]
    public void Linux_native_studio_is_the_single_machine_profile()
        => Assert.Equal(["--mode", "single"],
            ProductPlanner.Plan(Parse(), "studio", "native", windows: false, []).DelegatedArguments);

    [Fact]
    public void Delegated_profiles_install_only()
        => Assert.Contains("update.sh",
            Assert.Throws<ArgumentException>(
                () => ProductPlanner.Plan(Parse("update"), "studio", "native", windows: false, [])).Message);

    [Fact]
    public void Linux_only_options_are_rejected_for_the_docker_profile()
        => Assert.Contains("--runner-name",
            Assert.Throws<ArgumentException>(
                () => ProductPlanner.Plan(Parse("--runner-name", "r"), "studio", "docker", false, [])).Message);

    [Theory]
    [InlineData(null, false, "studio")]
    [InlineData(null, true, "agent-host")]
    [InlineData("control", false, "control-plane")]
    [InlineData("Connector", false, "connector")]
    public void Modes_normalize_with_their_defaults(string? mode, bool joinToken, string expected)
        => Assert.Equal(expected, ProductCommand.NormalizeMode(mode, joinToken));

    [Theory]
    [InlineData(null, "studio", "docker")]
    [InlineData(null, "control-plane", "docker")]
    [InlineData(null, "agent-host", "native")]
    [InlineData(null, "connector", "native")]
    [InlineData("systemd", "control-plane", "native")]
    public void Targets_normalize_with_their_defaults(string? target, string mode, string expected)
        => Assert.Equal(expected, ProductCommand.NormalizeTarget(target, mode));

    [Fact]
    public void Purge_requires_uninstall()
        => Assert.Throws<ArgumentException>(() => ProductCommand.Parse(["update", "--purge"]));

    [Fact]
    public void Windows_studio_services_run_the_D6_installer_as_the_primary_task_server()
    {
        var layout = new WindowsServiceLayout(@"C:\AgentOrchestrator", @"C:\ProgramData\AgentOrchestrator",
            @"C:\ProgramData\AgentStudio\data");

        var arguments = WindowsServiceScripts.Install(ProductProfile.StudioWindowsServices, layout,
            "pkg", null, null);

        Assert.Equal(["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File"], arguments.Take(5));
        Assert.Equal(Path.Combine("pkg", "deploy-windows", "fallback", "install-fallback-profile.ps1"), arguments[5]);
        AssertPair(arguments, "-RestMode", "Normal");
        AssertPair(arguments, "-DataDirectory", layout.DataDirectory);
        AssertPair(arguments, "-ReleasePackageRoot", "pkg");
    }

    [Fact]
    public void Connector_update_keeps_the_existing_upstream_configuration()
    {
        var layout = new WindowsServiceLayout("base", "config", "data");

        var install = WindowsServiceScripts.Install(ProductProfile.ConnectorWindows, layout, "pkg",
            "https://tasks.example.com", "token");
        var update = WindowsServiceScripts.Install(ProductProfile.ConnectorWindows, layout, "pkg", null, null);

        AssertPair(install, "-UpstreamUrl", "https://tasks.example.com");
        AssertPair(install, "-UpstreamTokenFile", "token");
        Assert.DoesNotContain("-UpstreamUrl", update);
        Assert.DoesNotContain("-UpstreamTokenFile", update);
    }

    [Fact]
    public void Uninstall_keeps_data_unless_purged()
    {
        var layout = new WindowsServiceLayout("base", "config", "data");

        var keep = WindowsServiceScripts.Uninstall(ProductProfile.StudioWindowsServices, layout, "scripts", purge: false);
        var purge = WindowsServiceScripts.Uninstall(ProductProfile.StudioWindowsServices, layout, "scripts", purge: true);
        var connector = WindowsServiceScripts.Uninstall(ProductProfile.ConnectorWindows, layout, "scripts", purge: true);

        Assert.DoesNotContain("-RemoveData", keep);
        AssertPair(purge, "-DataDirectory", "data");
        Assert.Contains("-RemoveData", purge);
        AssertPair(connector, "-TaskNames", WindowsServiceLayout.ConnectorTaskName);
        Assert.DoesNotContain("-RemoveData", connector);
        Assert.Equal(["config"], WindowsServiceScripts.PurgedConfiguration(ProductProfile.StudioWindowsServices, layout));
        Assert.DoesNotContain("config",
            WindowsServiceScripts.PurgedConfiguration(ProductProfile.ConnectorWindows, layout));
    }

    [Theory]
    [InlineData("https://tasks.example.com", true)]
    [InlineData("http://127.0.0.1:5071", true)]
    [InlineData("http://tasks.example.com", false)]
    [InlineData("tasks.example.com", false)]
    public void Connector_upstream_must_be_https_or_loopback(string url, bool valid)
    {
        var error = Record.Exception(() => ProductSetup.ValidateUpstream(url));
        Assert.Equal(valid, error is null);
    }

    [Theory]
    [InlineData("fallback/install-fallback-profile.ps1",
        "$ReleasePackageRoot", "$DataDirectory", "$InstallBase", "$ConfigRoot", "$ListenUrl",
        "$ConnectorListenUrl", "$RestMode")]
    [InlineData("fallback/uninstall-fallback-profile.ps1",
        "$InstallBase", "$TaskNames", "$RemoveData", "$DataDirectory")]
    [InlineData("studio-connector/install-studio-connector.ps1",
        "$ReleasePackageRoot", "$InstallBase", "$ConfigRoot", "$ConnectorListenUrl", "$UpstreamUrl",
        "$UpstreamTokenFile")]
    public void Windows_scripts_declare_every_parameter_the_installer_passes(string script, params string[] parameters)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "windows", script));
        var header = text[..text.IndexOf("\n)", StringComparison.Ordinal)];
        Assert.All(parameters, parameter => Assert.Contains(parameter, header, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Windows_package_zip_is_verified_and_extracted()
    {
        var release = Directory.CreateTempSubdirectory("agent-studio-setup-test-");
        try
        {
            var name = "agent-orchestrator-9.8.7-win-x64";
            var staging = Directory.CreateDirectory(Path.Combine(release.FullName, "staging", name));
            await File.WriteAllTextAsync(Path.Combine(staging.FullName, "VERSION"), "9.8.7");
            await File.WriteAllTextAsync(Path.Combine(staging.FullName, "RELEASE"), "component=agent-orchestrator\n");
            var zip = Path.Combine(release.FullName, $"{name}.zip");
            ZipFile.CreateFromDirectory(staging.Parent!.FullName, zip);
            var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(zip))).ToLowerInvariant();
            await File.WriteAllTextAsync(Path.Combine(release.FullName, "SHA256SUMS"), $"{hash}  {name}.zip\r\n");

            await using (var artifacts = new ReleaseArtifacts("9.8.7", release.FullName))
            {
                var extracted = await artifacts.ExtractWindowsPackageAsync(default);
                Assert.Equal("9.8.7", await File.ReadAllTextAsync(Path.Combine(extracted, "VERSION")));
            }

            await File.WriteAllTextAsync(Path.Combine(release.FullName, "SHA256SUMS"),
                $"{new string('0', 64)}  {name}.zip\n");
            await using var tampered = new ReleaseArtifacts("9.8.7", release.FullName);
            await Assert.ThrowsAsync<InvalidDataException>(() => tampered.ExtractWindowsPackageAsync(default));
        }
        finally
        {
            release.Delete(recursive: true);
        }
    }

    private static ProductCommand Parse(params string[] args) => ProductCommand.Parse(args);

    private static ProductPlan PlanFor(bool windows, params string[] args)
    {
        var command = Parse(args);
        var mode = ProductCommand.NormalizeMode(command.Values.GetValueOrDefault("--mode"),
            command.Values.ContainsKey("--join-token-file"));
        var target = ProductCommand.NormalizeTarget(command.Values.GetValueOrDefault("--target"), mode);
        var forwarded = new[] { "--release-version", "--release-dir", "--server-url", "--join-token-file" }
            .Where(command.Values.ContainsKey)
            .Select(option => (option, command.Values[option]))
            .ToList();
        return ProductPlanner.Plan(command, mode, target, windows, forwarded);
    }

    private static void AssertPair(IReadOnlyList<string> arguments, string option, string value)
    {
        var index = arguments.ToList().IndexOf(option);
        Assert.True(index >= 0 && index + 1 < arguments.Count, $"{option} is missing.");
        Assert.Equal(value, arguments[index + 1]);
    }

    private static string RepoRoot([CallerFilePath] string sourceFile = "")
    {
        var current = Path.GetDirectoryName(sourceFile);
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "agent-taskboard.sln"))) return current;
            current = Path.GetDirectoryName(current);
        }
        throw new InvalidOperationException("agent-taskboard.sln not found.");
    }
}
