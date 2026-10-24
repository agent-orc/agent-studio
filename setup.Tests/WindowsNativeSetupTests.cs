using AgentStudio.Setup;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
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
    [InlineData("codex")]
    [InlineData("claude")]
    public async Task Unattended_missing_provider_is_installed(string provider)
    {
        string? command = null;
        IReadOnlyList<string>? arguments = null;
        var nodeInstalled = false;
        await new WindowsNativeDecisions(provider, "skip", null, null).PrepareProviderAsync(
            new ConsolePrompter(true), unattended: true,
            commandOnPath: name => nodeInstalled && (name is "node.exe" or "npm.cmd"),
            install: (file, args) =>
            {
                Assert.True(nodeInstalled);
                command = file;
                arguments = args;
                return Task.CompletedTask;
            },
            installNode: () => { nodeInstalled = true; return Task.CompletedTask; });
        Assert.True(nodeInstalled);
        Assert.Equal("cmd.exe", command);
        Assert.Equal("/d", arguments![0]);
        Assert.Equal("/s", arguments[1]);
        Assert.Equal("/c", arguments[2]);
        Assert.Contains(provider == "codex" ? "@openai/codex" : "@anthropic-ai/claude-code",
            arguments[3]);
    }

    [Fact]
    public async Task Missing_npm_with_node_already_present_installs_complete_toolchain()
    {
        var nodeInstalled = false;
        await new WindowsNativeDecisions("codex", "skip", null, null).PrepareProviderAsync(
            new ConsolePrompter(true), unattended: true,
            commandOnPath: name => name == "node.exe" || nodeInstalled && name == "npm.cmd",
            install: (_, _) => { Assert.True(nodeInstalled); return Task.CompletedTask; },
            installNode: () => { nodeInstalled = true; return Task.CompletedTask; });
        Assert.True(nodeInstalled);
    }

    [Fact]
    public async Task Node_install_downloads_verified_msi_runs_installer_and_refreshes_path()
    {
        var msi = new byte[] { 77, 83, 73, 1, 2, 3 };
        var checksum = Convert.ToHexString(SHA256.HashData(msi));
        using var client = new HttpClient(new NodeReleaseHandler(msi,
            $"{checksum}  node-v22.23.1-x64.msi\n"));
        string? refreshedPath = null;
        var installed = false;
        await WindowsNativeDecisions.InstallNodeAsync(client, async (file, arguments) =>
        {
            Assert.Equal("msiexec.exe", file);
            Assert.Equal("/i", arguments[0]);
            Assert.Equal(msi, await File.ReadAllBytesAsync(arguments[1]));
            Assert.Equal(new[] { "/qn", "/norestart", "ALLUSERS=1" }, arguments.Skip(2));
            installed = true;
            return 3010;
        }, (name, target) => target == EnvironmentVariableTarget.Machine
            ? @"C:\Program Files\nodejs" : @"C:\Users\fixture\bin",
            path => refreshedPath = path);

        Assert.True(installed);
        Assert.Equal(@"C:\Program Files\nodejs" + Path.PathSeparator + @"C:\Users\fixture\bin",
            refreshedPath);
    }

    [Fact]
    public async Task Node_install_rejects_mismatched_release_before_running_msi()
    {
        using var client = new HttpClient(new NodeReleaseHandler([1, 2, 3],
            $"{new string('0', 64)}  node-v22.23.1-x64.msi\n"));
        await Assert.ThrowsAsync<InvalidDataException>(() => WindowsNativeDecisions.InstallNodeAsync(
            client, (_, _) => throw new Xunit.Sdk.XunitException("Unverified MSI was run."),
            (_, _) => null, _ => { }));
    }

    [Theory]
    [InlineData("codex", "@openai/codex")]
    [InlineData("claude", "@anthropic-ai/claude-code")]
    public async Task Clean_unattended_machine_installs_node_then_selected_provider(
        string provider, string package)
    {
        var msi = new byte[] { 77, 83, 73, 4, 5, 6 };
        using var client = new HttpClient(new NodeReleaseHandler(msi,
            $"{Convert.ToHexString(SHA256.HashData(msi))}  node-v22.23.1-x64.msi\n"));
        var nodeInstalled = false;
        var providerInstalled = false;
        await new WindowsNativeDecisions(provider, "skip", null, null).PrepareProviderAsync(
            new ConsolePrompter(true), unattended: true,
            commandOnPath: name => nodeInstalled && name is "node.exe" or "npm.cmd",
            install: (file, arguments) =>
            {
                Assert.True(nodeInstalled);
                Assert.Equal("cmd.exe", file);
                Assert.Contains(package, arguments[3]);
                providerInstalled = true;
                return Task.CompletedTask;
            },
            installNode: () => WindowsNativeDecisions.InstallNodeAsync(client,
                async (file, arguments) =>
                {
                    Assert.Equal(msi, await File.ReadAllBytesAsync(arguments[1]));
                    nodeInstalled = true;
                    return 0;
                }, (_, _) => "fixture-path", _ => { }));
        Assert.True(providerInstalled);
    }

    [Theory]
    [InlineData("skip", "skip", false)]
    [InlineData("codex", "skip", true)]
    [InlineData("skip", "gcm", true)]
    [InlineData("skip", "token", true)]
    [InlineData("skip", "deploy-key", true)]
    public void Runner_uses_password_logon_for_provider_or_GitHub_access(
        string provider, string gitAccess, bool expected)
        => Assert.Equal(expected,
            new WindowsNativeDecisions(provider, gitAccess, null, null).RequiresRunnerPassword);

    [Fact]
    public async Task Guided_deploy_key_verification_uses_runner_Git_environment()
    {
        var root = Directory.CreateTempSubdirectory("native-git-verification-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "runner.env"),
                "RUNNER_ID=local-coding\nGIT_CONFIG_COUNT=1\nGIT_SSH_COMMAND=ssh -F config\n");
            var choices = new WindowsNativeDecisions("skip", "deploy-key", null,
                "git@github.com:example/project.git");
            var verified = false;
            await choices.VerifyDeployKeyAsync(new ConsolePrompter(true), root.FullName, environment =>
            {
                verified = true;
                Assert.Equal("1", environment["GIT_CONFIG_COUNT"]);
                Assert.Equal("ssh -F config", environment["GIT_SSH_COMMAND"]);
                Assert.DoesNotContain("RUNNER_ID", environment.Keys);
                return Task.FromResult(true);
            });
            Assert.True(verified);
        }
        finally { root.Delete(recursive: true); }
    }

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
        Assert.Contains("-Unattended", WindowsServiceScripts.Install(
            ProductProfile.StudioWindowsServices, layout, "pkg", null, null,
            unattended: true));
    }

    [Fact]
    public async Task Native_verification_uses_the_Task_Server_board_route()
    {
        var seen = new List<string>();
        using var http = new HttpClient(new NativeHealthHandler(seen));

        await WindowsServiceSetup.VerifyAsync(ProductProfile.StudioWindowsServices, http);

        Assert.Contains("http://127.0.0.1:4011/api/v1/studio/board", seen);
        Assert.DoesNotContain(seen, path => path.EndsWith("/api/system/version", StringComparison.Ordinal));
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

    [Fact]
    public async Task Windows_packager_places_browser_at_the_path_required_by_native_setup()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("windows-package-contract-");
        try
        {
            const string version = "0.0.0";
            var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));
            var packageScript = Path.Combine(repoRoot, "scripts/release/package-release-windows.ps1");
            var publish = Path.Combine(root.FullName, "publish");
            var linux = Path.Combine(root.FullName, "linux");
            var output = Path.Combine(root.FullName, "output");
            foreach (var relative in new[]
            {
                "task-server/task-server.exe", "orchestrator-engine/orchestrator-engine.exe",
                "studio-bff/agent-studio-bff.exe", "agent-host/agent-host.exe",
                "setup/agent-studio-setup.exe", "studio-backend/OrchestratorApi.exe",
                "studio-backend/appsettings.json",
            })
            {
                var file = Path.Combine(publish, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllTextAsync(file, "fixture");
            }
            Directory.CreateDirectory(linux);
            Directory.CreateDirectory(output);
            await using (var archive = File.Create(Path.Combine(linux, $"agent-studio-{version}.tar.gz")))
            await using (var gzip = new GZipStream(archive, CompressionLevel.Fastest))
            using (var tar = new TarWriter(gzip))
            {
                using var index = new MemoryStream("<html></html>"u8.ToArray());
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile,
                    $"agent-studio-{version}/browser/index.html") { DataStream = index });
            }
            var start = new ProcessStartInfo("powershell.exe")
            {
                WorkingDirectory = repoRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                         "-File", packageScript, "-Version", version, "-Sha", new string('a', 40),
                         "-PublishRoot", publish, "-LinuxReleaseRoot", linux, "-OutputRoot", output })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, error);
            using var zip = ZipFile.OpenRead(Path.Combine(output,
                $"agent-orchestrator-{version}-win-x64.zip"));
            var prefix = $"agent-orchestrator-{version}-win-x64/";
            Assert.Contains(zip.Entries, entry => entry.FullName.Replace('\\', '/') == prefix + "browser/index.html");
            Assert.Contains(zip.Entries, entry => entry.FullName.Replace('\\', '/') == prefix + "OrchestratorApi.exe");
            Assert.Contains(zip.Entries, entry => entry.FullName.Replace('\\', '/') == prefix + "agent-host.exe");
        }
        finally { root.Delete(recursive: true); }
    }

    private sealed class NodeReleaseHandler(byte[] msi, string checksums) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal("nodejs.org", request.RequestUri!.Host);
            Assert.StartsWith("/download/release/v22.23.1/", request.RequestUri.AbsolutePath);
            var body = request.RequestUri.AbsolutePath.EndsWith("SHASUMS256.txt", StringComparison.Ordinal)
                ? new ByteArrayContent(System.Text.Encoding.ASCII.GetBytes(checksums))
                : new ByteArrayContent(msi);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body });
        }
    }

    private sealed class NativeHealthHandler(List<string> seen) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            seen.Add(url);
            var content = url switch
            {
                "http://127.0.0.1:5071/readyz" => "{\"status\":\"ready\",\"mode\":\"Normal\"}",
                "http://127.0.0.1:5031/healthz" or "http://127.0.0.1:4011/healthz" => "{\"status\":\"live\"}",
                "http://127.0.0.1:4011/api/v1/studio/board" => "{}",
                _ => throw new Xunit.Sdk.XunitException($"Unexpected native verification route: {url}"),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content),
            });
        }
    }
}
