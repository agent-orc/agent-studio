using AgentStudio.Setup;
using Xunit;

namespace AgentOrchestratorSetup.Tests;

public sealed class WindowsNativeSetupTests
{
    [Fact]
    public void Windows_defaults_to_native_one_box()
        => Assert.Equal("native", ProductCommand.NormalizeTarget(null, "studio", windows: true));

    [Fact]
    public void Linux_stays_on_its_existing_default()
        => Assert.Equal("docker", ProductCommand.NormalizeTarget(null, "studio", windows: false));

    [Fact]
    public void Native_preflight_names_each_local_prerequisite()
    {
        var facts = new HostFacts(true, true, null, null, null, null,
            30L * 1024 * 1024 * 1024, null, null, null, null, null, null,
            WindowsVersionSupported: false, GitAvailable: false, NativePortsAvailable: false,
            ExistingInstallation: true);

        var findings = PreflightPolicy.Evaluate(InstallationJourney.OneBox, "native", facts);

        Assert.Contains(findings, finding => finding.Check == "Windows version"
            && finding.Status == PreflightStatus.Fail && finding.Recovery is not null);
        Assert.Contains(findings, finding => finding.Check == "Git for Windows"
            && finding.Status == PreflightStatus.Fail && finding.Recovery is not null);
        Assert.Contains(findings, finding => finding.Check == "Local ports"
            && finding.Status == PreflightStatus.Fail && finding.Recovery is not null);
        Assert.Contains(findings, finding => finding.Check == "Existing installation"
            && finding.Observed.Contains("repair", StringComparison.Ordinal));
    }

    [Fact]
    public void Unattended_choices_record_only_names_and_paths()
    {
        var choices = WindowsNativeDecisions.Resolve(new ConsolePrompter(true), "skip", "skip", null, null);

        Assert.Equal("skip", choices.ToRecord()["providerCli"]);
        Assert.Equal("skip", choices.ToRecord()["gitAccess"]);
        Assert.DoesNotContain("gitTokenFile", choices.ToRecord().Keys);
        Assert.Throws<ArgumentException>(() => WindowsNativeDecisions.Resolve(
            new ConsolePrompter(true), "invalid", "skip", null, null));
    }

    [Theory]
    [InlineData("codex", "@openai/codex")]
    [InlineData("claude", "@anthropic-ai/claude-code")]
    public async Task Unattended_missing_provider_installs_selected_cli(string provider, string package)
    {
        var calls = new List<(string File, string Arguments)>();
        var decisions = new WindowsNativeDecisions(provider, "skip", null, null);

        await decisions.PrepareProviderAsync(new ConsolePrompter(true), unattended: true,
            commandOnPath: _ => false,
            requireProcess: (file, arguments) =>
            {
                calls.Add((file, string.Join(' ', arguments)));
                return Task.CompletedTask;
            });

        Assert.Single(calls);
        Assert.Equal("cmd.exe", calls[0].File);
        Assert.Contains($"npm install --global {package}", calls[0].Arguments);
    }

    [Theory]
    [InlineData("skip", "skip", false)]
    [InlineData("codex", "skip", true)]
    [InlineData("skip", "gcm", true)]
    [InlineData("skip", "token", true)]
    [InlineData("skip", "deploy-key", true)]
    public void Network_access_requires_password_logon(string provider, string git, bool required)
        => Assert.Equal(required, new WindowsNativeDecisions(provider, git, null, null)
            .RequiresRunnerPassword);

    [Fact]
    public void Native_install_passes_full_studio_switch_without_changing_connector_profile()
    {
        var layout = new WindowsServiceLayout("base", "config", "data");
        var studio = WindowsServiceScripts.Install(ProductProfile.StudioWindowsServices, layout, "pkg",
            null, null, new WindowsNativeDecisions("codex", "skip", null, null),
            runnerPasswordFromStdin: true);
        var connector = WindowsServiceScripts.Install(ProductProfile.ConnectorWindows, layout, "pkg", null, null);

        Assert.Contains("-FullStudio", studio);
        Assert.Contains("-ProviderCli", studio);
        Assert.Contains("-RunnerPasswordFromStdin", studio);
        Assert.DoesNotContain("-FullStudio", connector);
        Assert.Contains("-PrepareDeployKeyOnly", WindowsServiceScripts.Install(
            ProductProfile.StudioWindowsServices, layout, "pkg", null, null,
            new WindowsNativeDecisions("skip", "deploy-key", null, "git@github.com:owner/repo.git"),
            prepareDeployKeyOnly: true));
        Assert.Contains("-Unattended", WindowsServiceScripts.Install(
            ProductProfile.StudioWindowsServices, layout, "pkg", null, null,
            unattended: true));
    }

    [Fact]
    public void Incomplete_windows_package_is_rejected_before_installation_is_recorded()
    {
        var root = Directory.CreateTempSubdirectory("windows-studio-inventory-");
        try
        {
            foreach (var file in new[]
            {
                "task-server.exe", "orchestrator-engine.exe", "agent-studio-bff.exe",
                "OrchestratorApi.exe", "agent-host.exe", "provider-codex.exe", "provider-claude.exe",
                "appsettings.json",
                "agent-rules/core.md", "deploy-windows/fallback/register-native-service.ps1",
            })
            {
                var path = Path.Combine(root.FullName, file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "fixture");
            }
            var error = Assert.Throws<InvalidDataException>(() =>
                WindowsPackageInventory.RequireFullStudio(root.FullName));
            Assert.Contains("browser/index.html", error.Message);
        }
        finally { root.Delete(recursive: true); }
    }
}
