using System.Text.Json;
using AgentStudio.Cli;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class LocalCliRepairServiceTests
{
    [Theory]
    [InlineData("claude", "@anthropic-ai", "claude-code", "2.1.231")]
    [InlineData("codex", "@openai", "codex", "1.2.3")]
    public void Inspect_distinguishes_missing_shim_from_uninstalled_package(
        string cliType,
        string scope,
        string package,
        string version)
    {
        using var temp = new TempDirectory();
        var npmBin = Path.Combine(temp.Path, "npm");
        var packageDir = Path.Combine(npmBin, "node_modules", scope, package);
        Directory.CreateDirectory(packageDir);
        File.WriteAllText(Path.Combine(packageDir, "package.json"), $$"""{"version":"{{version}}"}""");

        var installed = LocalCliRepairService.Inspect(cliType, cliType, npmBin);

        Assert.Equal(NpmCliInstallState.MissingShimWithPackagePresent, installed.State);
        Assert.Equal(version, installed.PackageVersion);

        Directory.Delete(packageDir, recursive: true);
        var absent = LocalCliRepairService.Inspect(cliType, cliType, npmBin);
        Assert.Equal(NpmCliInstallState.TrulyUninstalled, absent.State);
    }

    [Fact]
    public void Inspect_requires_the_windows_command_shim()
    {
        using var temp = new TempDirectory();
        var npmBin = Path.Combine(temp.Path, "npm");
        Directory.CreateDirectory(Path.Combine(
            npmBin, "node_modules", "@anthropic-ai", "claude-code"));
        File.WriteAllText(Path.Combine(npmBin, "claude"), "shell shim");

        var missingCommandShim = LocalCliRepairService.Inspect("claude", "claude", npmBin);

        Assert.Equal(NpmCliInstallState.MissingShimWithPackagePresent, missingCommandShim.State);

        File.WriteAllText(Path.Combine(npmBin, "claude.cmd"), "command shim");
        var inspection = LocalCliRepairService.Inspect("claude", "claude", npmBin);

        Assert.Equal(NpmCliInstallState.PackagePresentWithShim, inspection.State);
    }

    [Theory]
    [InlineData(NpmGlobalInstallMode.Install, false)]
    [InlineData(NpmGlobalInstallMode.ForceRelink, true)]
    public void Installer_arguments_distinguish_install_from_force_relink(
        NpmGlobalInstallMode mode,
        bool expectsForce)
    {
        var arguments = NpmGlobalInstaller.BuildArguments("@openai/codex", mode);

        Assert.Equal(expectsForce, arguments.Contains("--force"));
        Assert.Equal("@openai/codex", arguments[2]);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Installer_resolution_returns_an_explicit_npm_that_passed_version_preflight()
    {
        var resolution = await NpmGlobalInstaller.ResolveNpmExecutableAsync(CancellationToken.None);

        var command = Assert.IsType<NpmExecutable>(resolution.Command);
        Assert.True(Path.IsPathRooted(command.ExecutablePath));
        Assert.True(File.Exists(command.ExecutablePath));
        Assert.True(Directory.Exists(command.WorkingDirectory));
        Assert.False(string.IsNullOrWhiteSpace(command.Version));
    }

    [Fact]
    public void Installer_resolution_prefers_npm_shipped_with_the_active_node_install()
    {
        using var temp = new TempDirectory();
        var nodeDirectory = Path.Combine(temp.Path, "nodejs");
        var npmCli = Path.Combine(nodeDirectory, "node_modules", "npm", "bin", "npm-cli.js");
        var appDataNpm = Path.Combine(temp.Path, "appdata", "npm", "npm.cmd");
        Directory.CreateDirectory(Path.GetDirectoryName(npmCli)!);
        Directory.CreateDirectory(Path.GetDirectoryName(appDataNpm)!);
        File.WriteAllText(Path.Combine(nodeDirectory, "node.exe"), "node");
        File.WriteAllText(npmCli, "npm");
        File.WriteAllText(appDataNpm, "npm");

        var candidates = NpmGlobalInstaller.NpmExecutableCandidates(
            true,
            nodeDirectory,
            Path.Combine(temp.Path, "appdata"),
            null,
            null,
            null);

        var preferred = Assert.IsType<NpmExecutable>(candidates.First());
        Assert.Equal(Path.Combine(nodeDirectory, "node.exe"), preferred.ExecutablePath);
        Assert.Equal([npmCli], preferred.PrefixArguments);
        Assert.Equal(nodeDirectory, preferred.WorkingDirectory);
    }

    [Fact]
    public async Task Installer_reports_typed_npm_unavailable_without_leaking_module_errors()
    {
        var installer = new NpmGlobalInstaller(_ => Task.FromResult(
            new NpmExecutableResolution(
                null,
                "npm unavailable: no candidate passed 'npm --version'.")));

        var result = await installer.InstallAsync(
            "@openai/codex",
            NpmGlobalInstallMode.ForceRelink,
            CancellationToken.None);

        Assert.Equal(NpmGlobalInstallOutcome.NpmUnavailable, result.Outcome);
        Assert.False(result.Succeeded);
        Assert.Null(result.ExitCode);
        Assert.Contains("npm unavailable", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("MODULE_NOT_FOUND", result.StandardError, StringComparison.Ordinal);
    }

    /// <summary>
    /// AGT-2706: Claude Code 2.1.263 ships as a launcher whose npm postinstall
    /// replaces a 500-byte placeholder with the platform binary from the nested
    /// native package. When that step is skipped the package directory and the
    /// command shim both look healthy while <c>claude --version</c> fails.
    /// </summary>
    [Fact]
    public void Inspect_classifies_a_launcher_stub_left_by_a_partial_autoupdate()
    {
        using var temp = new TempDirectory();
        var npmBin = Path.Combine(temp.Path, "npm");
        var packageDir = Path.Combine(npmBin, "node_modules", "@anthropic-ai", "claude-code");
        WriteLauncherPackage(packageDir, "2.1.263", launcherBytes: 500);
        File.WriteAllText(Path.Combine(npmBin, "claude.cmd"), "command shim");

        var stub = LocalCliRepairService.Inspect("claude", "claude", npmBin);

        Assert.Equal(NpmCliInstallState.LauncherStubWithPackagePresent, stub.State);
        Assert.Equal("2.1.263", stub.PackageVersion);
        Assert.Equal(500, stub.LauncherBinaryLength);
        Assert.Contains("500 bytes", stub.DetectionEvidence!, StringComparison.Ordinal);
        Assert.Contains(
            "@anthropic-ai/claude-code-win32-x64",
            stub.DetectionEvidence!,
            StringComparison.Ordinal);

        File.WriteAllBytes(
            Path.Combine(packageDir, "bin", "claude.exe"),
            new byte[LocalCliRepairService.LauncherStubMaxBytes * 2]);
        var healed = LocalCliRepairService.Inspect("claude", "claude", npmBin);

        Assert.Equal(NpmCliInstallState.PackagePresentWithShim, healed.State);
        Assert.Null(healed.DetectionEvidence);
    }

    [Fact]
    public void Inspect_reads_the_launcher_stub_from_a_failing_version_probe()
    {
        using var temp = new TempDirectory();
        var npmBin = Path.Combine(temp.Path, "npm");
        var packageDir = Path.Combine(npmBin, "node_modules", "@anthropic-ai", "claude-code");
        WriteLauncherPackage(
            packageDir,
            "2.1.263",
            launcherBytes: LocalCliRepairService.LauncherStubMaxBytes * 2);
        File.WriteAllText(Path.Combine(npmBin, "claude.cmd"), "command shim");

        var reported = LocalCliRepairService.Inspect(
            "claude",
            "claude",
            npmBin,
            "Error: claude native binary not installed.");

        Assert.Equal(NpmCliInstallState.LauncherStubWithPackagePresent, reported.State);
        Assert.Contains("native binary not installed", reported.DetectionEvidence!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A small JavaScript bin entry is the normal layout for an npm CLI, not a
    /// placeholder. Only native launcher targets are size-checked.
    /// </summary>
    [Fact]
    public void Inspect_does_not_read_a_small_javascript_bin_as_a_launcher_stub()
    {
        using var temp = new TempDirectory();
        var npmBin = Path.Combine(temp.Path, "npm");
        var packageDir = Path.Combine(npmBin, "node_modules", "@openai", "codex");
        Directory.CreateDirectory(Path.Combine(packageDir, "bin"));
        File.WriteAllText(
            Path.Combine(packageDir, "package.json"),
            """{"version":"0.151.0","bin":{"codex":"bin/codex.js"}}""");
        File.WriteAllText(Path.Combine(packageDir, "bin", "codex.js"), "#!/usr/bin/env node");
        File.WriteAllText(Path.Combine(npmBin, "codex.cmd"), "command shim");

        var inspection = LocalCliRepairService.Inspect("codex", "codex", npmBin);

        Assert.Equal(NpmCliInstallState.PackagePresentWithShim, inspection.State);
    }

    [Theory]
    [InlineData(NpmCliInstallState.TrulyUninstalled, NpmGlobalInstallMode.Install)]
    [InlineData(NpmCliInstallState.MissingShimWithPackagePresent, NpmGlobalInstallMode.ForceRelink)]
    [InlineData(NpmCliInstallState.LauncherStubWithPackagePresent, NpmGlobalInstallMode.Install)]
    public void Repair_plan_selects_remedy_for_package_and_shim_state(
        NpmCliInstallState state,
        NpmGlobalInstallMode expectedMode)
    {
        var plan = LocalCliRepairService.SelectRepairPlan(state);

        Assert.NotNull(plan);
        Assert.Equal(expectedMode, plan.InstallMode);
    }

    [Fact]
    public void Repair_plan_for_a_launcher_stub_reruns_the_package_postinstall()
    {
        var plan = LocalCliRepairService.SelectRepairPlan(
            NpmCliInstallState.LauncherStubWithPackagePresent);

        Assert.NotNull(plan);
        Assert.Equal(NpmCliRepairAction.PackageInstallScript, plan.Action);
        Assert.Equal("launcher-stub-with-package-present", plan.Detection);
        Assert.Equal("postinstall-rerun", plan.RepairAction);
    }

    [Theory]
    [InlineData(NpmCliInstallState.Unsupported)]
    [InlineData(NpmCliInstallState.PackagePresentWithShim)]
    public void Repair_plan_is_noop_for_unsupported_or_healthy_state(NpmCliInstallState state)
        => Assert.Null(LocalCliRepairService.SelectRepairPlan(state));

    [Fact]
    public void Inspect_does_not_repair_a_custom_missing_path()
    {
        using var temp = new TempDirectory();
        var npmBin = Path.Combine(temp.Path, "npm");
        Directory.CreateDirectory(Path.Combine(
            npmBin, "node_modules", "@openai", "codex"));

        var inspection = LocalCliRepairService.Inspect(
            "codex",
            Path.Combine(temp.Path, "custom", "codex.cmd"),
            npmBin);

        Assert.Equal(NpmCliInstallState.Unsupported, inspection.State);
    }

    [Fact]
    public void Attempt_budget_allows_only_one_attempt_per_hour()
    {
        var attemptedAt = new DateTimeOffset(2026, 8, 18, 10, 0, 0, TimeSpan.Zero);

        Assert.False(LocalCliRepairService.AttemptAllowed(
            attemptedAt.AddMinutes(59), attemptedAt));
        Assert.True(LocalCliRepairService.AttemptAllowed(
            attemptedAt.AddHours(1), attemptedAt));
    }

    [Fact]
    public async Task Probe_installs_a_missing_package_once_and_then_is_a_noop()
    {
        using var temp = new TempDirectory();
        var appData = Path.Combine(temp.Path, "appdata");
        var npmBin = Path.Combine(appData, "npm");
        var packageDir = Path.Combine(npmBin, "node_modules", "@openai", "codex");
        var shim = Path.Combine(npmBin, "codex.cmd");
        var installer = new FakeInstaller((_, mode) =>
        {
            Assert.Equal(NpmGlobalInstallMode.Install, mode);
            Directory.CreateDirectory(packageDir);
            File.WriteAllText(Path.Combine(packageDir, "package.json"), "{\"version\":\"0.151.0\"}");
            File.WriteAllText(shim, "command shim");
        });
        var service = new LocalCliRepairService(
            installer,
            NullLogger<LocalCliRepairService>.Instance,
            () => new DateTimeOffset(2026, 8, 31, 10, 0, 0, TimeSpan.Zero),
            () => true,
            () => appData,
            () => null,
            Path.Combine(temp.Path, "cli-self-heal.jsonl"));

        (bool Available, string? Version, string Path) Probe()
            => File.Exists(shim)
                ? (true, "codex-cli 0.151.0", shim)
                : (false, null, "codex");

        var repaired = await service.ProbeAndRepairAsync(
            "codex", null, Probe, CancellationToken.None);
        var healthy = await service.ProbeAndRepairAsync(
            "codex", "codex-cli 0.151.0", Probe, CancellationToken.None);

        Assert.True(repaired.Available);
        Assert.True(healthy.Available);
        Assert.True(Directory.Exists(packageDir));
        Assert.True(File.Exists(shim));
        Assert.Equal(1, installer.Calls);
        Assert.Equal(NpmGlobalInstallMode.Install, installer.LastMode);
    }

    [Fact]
    public async Task Probe_force_relinks_deleted_codex_shim_and_version_probe_succeeds()
    {
        using var temp = new TempDirectory();
        var appData = Path.Combine(temp.Path, "appdata");
        var npmBin = Path.Combine(appData, "npm");
        var packageDir = Path.Combine(npmBin, "node_modules", "@openai", "codex");
        Directory.CreateDirectory(Path.Combine(packageDir, "bin"));
        File.WriteAllText(Path.Combine(packageDir, "package.json"), "{\"version\":\"0.151.0\"}");
        File.WriteAllText(Path.Combine(packageDir, "bin", "codex.js"), "package binary");
        var shim = Path.Combine(npmBin, "codex.cmd");
        File.WriteAllText(shim, "old command shim");
        File.Delete(shim);
        var installer = new FakeInstaller((_, mode) =>
        {
            Assert.Equal(NpmGlobalInstallMode.ForceRelink, mode);
            File.WriteAllText(shim, "restored command shim");
        });
        var service = new LocalCliRepairService(
            installer,
            NullLogger<LocalCliRepairService>.Instance,
            () => new DateTimeOffset(2026, 8, 31, 11, 57, 0, TimeSpan.Zero),
            () => true,
            () => appData,
            () => null,
            Path.Combine(temp.Path, "cli-self-heal.jsonl"));
        var versionProbeCalls = 0;

        (bool Available, string? Version, string Path) Probe()
        {
            versionProbeCalls++;
            return File.Exists(shim)
                ? (true, "codex-cli 0.151.0", shim)
                : (false, null, "codex");
        }

        var repaired = await service.ProbeAndRepairAsync(
            "codex", "0.151.0", Probe, CancellationToken.None);
        var healthy = await service.ProbeAndRepairAsync(
            "codex", repaired.Version, Probe, CancellationToken.None);

        Assert.True(File.Exists(shim));
        Assert.True(repaired.Available);
        Assert.Equal("codex-cli 0.151.0", repaired.Version);
        Assert.True(healthy.Available);
        Assert.Equal(1, installer.Calls);
        Assert.Equal(3, versionProbeCalls);
    }

    [Fact]
    public async Task Probe_repairs_missing_shim_journals_versions_and_suppresses_repeat()
    {
        using var temp = new TempDirectory();
        var appData = Path.Combine(temp.Path, "appdata");
        var npmBin = Path.Combine(appData, "npm");
        var packageDir = Path.Combine(
            npmBin, "node_modules", "@anthropic-ai", "claude-code");
        Directory.CreateDirectory(packageDir);
        File.WriteAllText(
            Path.Combine(packageDir, "package.json"),
            "{\"version\":\"2.1.231\"}");
        var localAppData = Path.Combine(temp.Path, "local-appdata");
        var npmLogs = Path.Combine(localAppData, "npm-cache", "_logs");
        Directory.CreateDirectory(npmLogs);
        var npmLog = Path.Combine(npmLogs, "2026-08-18T10_00_00_000Z-debug-0.log");
        File.WriteAllText(
            npmLog,
            "10 verbose argv npm update --global @anthropic-ai/claude-code");
        File.SetLastWriteTimeUtc(npmLog, new DateTime(2026, 8, 18, 10, 0, 0, DateTimeKind.Utc));
        var shim = Path.Combine(npmBin, "claude.cmd");
        var installer = new FakeInstaller((_, _) => File.WriteAllText(shim, "shim"));
        var now = new DateTimeOffset(2026, 8, 18, 10, 0, 0, TimeSpan.Zero);
        var journal = Path.Combine(temp.Path, "cli-self-heal.jsonl");
        var service = new LocalCliRepairService(
            installer,
            NullLogger<LocalCliRepairService>.Instance,
            () => now,
            () => true,
            () => appData,
            () => localAppData,
            journal);

        (bool Available, string? Version, string Path) Probe()
            => File.Exists(shim)
                ? (true, "2.1.234", shim)
                : (false, null, "claude");

        var result = await service.ProbeAndRepairAsync(
            "claude", "2.1.231", Probe, CancellationToken.None);

        Assert.True(result.Available);
        Assert.True(File.Exists(shim));
        Assert.Equal(1, installer.Calls);
        Assert.Equal(NpmGlobalInstallMode.ForceRelink, installer.LastMode);
        Assert.Empty(service.Current());
        var journalText = File.ReadAllText(journal);
        Assert.Contains("missing-shim-with-package-present", journalText, StringComparison.Ordinal);
        Assert.Contains("2.1.231", journalText, StringComparison.Ordinal);
        Assert.Contains("2.1.234", journalText, StringComparison.Ordinal);
        Assert.Contains("npm update --global @anthropic-ai/claude-code", journalText, StringComparison.Ordinal);

        var healthyResult = await service.ProbeAndRepairAsync(
            "claude", "2.1.234", Probe, CancellationToken.None);
        Assert.True(healthyResult.Available);
        Assert.Equal(1, installer.Calls);

        File.Delete(shim);
        now = now.AddMinutes(59);
        var restartedService = new LocalCliRepairService(
            installer,
            NullLogger<LocalCliRepairService>.Instance,
            () => now,
            () => true,
            () => appData,
            () => localAppData,
            journal);
        Assert.Empty(restartedService.Current());
        await restartedService.ProbeAndRepairAsync(
            "claude", "2.1.234", Probe, CancellationToken.None);
        Assert.Equal(1, installer.Calls);
    }

    [Fact]
    public async Task Probe_reports_package_shim_and_relink_state_when_repair_fails()
    {
        using var temp = new TempDirectory();
        var appData = Path.Combine(temp.Path, "appdata");
        var npmBin = Path.Combine(appData, "npm");
        var packageDir = Path.Combine(npmBin, "node_modules", "@openai", "codex");
        Directory.CreateDirectory(packageDir);
        File.WriteAllText(Path.Combine(packageDir, "package.json"), "{\"version\":\"0.151.0\"}");
        var loggerEntries = new List<string>();
        var service = new LocalCliRepairService(
            new FakeInstaller((_, _) => { }),
            new CollectingLogger<LocalCliRepairService>(loggerEntries),
            () => new DateTimeOffset(2026, 8, 31, 11, 40, 0, TimeSpan.Zero),
            () => true,
            () => appData,
            () => null,
            Path.Combine(temp.Path, "cli-self-heal.jsonl"));

        var result = await service.ProbeAndRepairAsync(
            "codex",
            "0.151.0",
            () => (false, null, "codex"),
            CancellationToken.None);

        Assert.False(result.Available);
        var status = Assert.Single(service.Current());
        Assert.Contains("package present", status.Detail, StringComparison.Ordinal);
        Assert.Contains("command shim absent", status.Detail, StringComparison.Ordinal);
        Assert.Contains("npm action force-relink attempted", status.Detail, StringComparison.Ordinal);
        Assert.Contains(loggerEntries, entry =>
            entry.Contains("packageStateBefore=present", StringComparison.Ordinal)
            && entry.Contains("shimStateBefore=absent", StringComparison.Ordinal)
            && entry.Contains("repairAction=force-relink", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Healthy_probe_clears_out_of_band_repair_failure_and_restart_does_not_restore_it()
    {
        using var temp = new TempDirectory();
        var appData = Path.Combine(temp.Path, "appdata");
        var npmBin = Path.Combine(appData, "npm");
        var packageDir = Path.Combine(npmBin, "node_modules", "@openai", "codex");
        Directory.CreateDirectory(packageDir);
        File.WriteAllText(Path.Combine(packageDir, "package.json"), "{\"version\":\"0.151.0\"}");
        var journal = Path.Combine(temp.Path, "cli-self-heal.jsonl");
        var now = new DateTimeOffset(2026, 8, 31, 11, 40, 0, TimeSpan.Zero);
        var available = false;
        var service = new LocalCliRepairService(
            new FakeInstaller((_, _) => { }, NpmGlobalInstallOutcome.Failed),
            NullLogger<LocalCliRepairService>.Instance,
            () => now,
            () => true,
            () => appData,
            () => null,
            journal);

        (bool Available, string? Version, string Path) Probe()
            => available
                ? (true, "codex-cli 0.151.0", Path.Combine(npmBin, "codex.cmd"))
                : (false, null, "codex");

        await service.ProbeAndRepairAsync("codex", "0.151.0", Probe, CancellationToken.None);
        Assert.Equal("failed", Assert.Single(service.Current()).Outcome);

        available = true;
        now = now.AddMinutes(5);
        var healthy = await service.ProbeAndRepairAsync(
            "codex",
            "0.151.0",
            Probe,
            CancellationToken.None);

        Assert.True(healthy.Available);
        Assert.Empty(service.Current());
        Assert.Contains("\"outcome\":\"resolved\"", File.ReadAllText(journal), StringComparison.Ordinal);

        var restartedService = new LocalCliRepairService(
            new FakeInstaller((_, _) => { }),
            NullLogger<LocalCliRepairService>.Instance,
            () => now,
            () => true,
            () => appData,
            () => null,
            journal);
        Assert.Empty(restartedService.Current());
    }

    [Fact]
    public async Task Probe_repairs_a_launcher_stub_by_rerunning_the_package_postinstall()
    {
        using var temp = new TempDirectory();
        var appData = Path.Combine(temp.Path, "appdata");
        var npmBin = Path.Combine(appData, "npm");
        var packageDir = Path.Combine(npmBin, "node_modules", "@anthropic-ai", "claude-code");
        var launcher = Path.Combine(packageDir, "bin", "claude.exe");
        WriteLauncherPackage(packageDir, "2.1.263", launcherBytes: 500);
        File.WriteAllText(Path.Combine(packageDir, "install.cjs"), "// npm postinstall");
        var shim = Path.Combine(npmBin, "claude.cmd");
        File.WriteAllText(shim, "command shim");
        var journal = Path.Combine(temp.Path, "cli-self-heal.jsonl");
        // The real install.cjs hard-links the platform binary over the placeholder.
        var installer = new FakeInstaller(
            (_, _) => { },
            onScript: _ => File.WriteAllBytes(
                launcher,
                new byte[LocalCliRepairService.LauncherStubMaxBytes * 2]));
        var service = new LocalCliRepairService(
            installer,
            NullLogger<LocalCliRepairService>.Instance,
            () => new DateTimeOffset(2026, 9, 6, 16, 32, 0, TimeSpan.Zero),
            () => true,
            () => appData,
            () => null,
            journal);

        (bool Available, string? Version, string Path) Probe()
            => new FileInfo(launcher).Length > LocalCliRepairService.LauncherStubMaxBytes
                ? (true, "2.1.263 (Claude Code)", shim)
                : (false, "Error: claude native binary not installed.", shim);

        var repaired = await service.ProbeAndRepairAsync(
            "claude", "2.1.261", Probe, CancellationToken.None);

        Assert.True(repaired.Available);
        Assert.Equal("2.1.263 (Claude Code)", repaired.Version);
        Assert.Equal(1, installer.ScriptCalls);
        Assert.Equal(packageDir, installer.LastScriptDirectory);
        Assert.Equal("install.cjs", installer.LastScript);
        Assert.Equal(0, installer.Calls);
        Assert.Empty(service.Current());

        var journalText = File.ReadAllText(journal);
        Assert.Contains("launcher-stub-with-package-present", journalText, StringComparison.Ordinal);
        Assert.Contains("postinstall-rerun", journalText, StringComparison.Ordinal);
        Assert.Contains("500 bytes", journalText, StringComparison.Ordinal);
        Assert.Contains("claude-code-win32-x64", journalText, StringComparison.Ordinal);
        Assert.Contains("\"outcome\":\"repaired\"", journalText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Probe_falls_back_to_a_version_pinned_reinstall_without_an_install_script()
    {
        using var temp = new TempDirectory();
        var appData = Path.Combine(temp.Path, "appdata");
        var npmBin = Path.Combine(appData, "npm");
        var packageDir = Path.Combine(npmBin, "node_modules", "@anthropic-ai", "claude-code");
        var launcher = Path.Combine(packageDir, "bin", "claude.exe");
        WriteLauncherPackage(packageDir, "2.1.263", launcherBytes: 500);
        var shim = Path.Combine(npmBin, "claude.cmd");
        File.WriteAllText(shim, "command shim");
        var installer = new FakeInstaller((_, _) => File.WriteAllBytes(
            launcher,
            new byte[LocalCliRepairService.LauncherStubMaxBytes * 2]));
        var service = new LocalCliRepairService(
            installer,
            NullLogger<LocalCliRepairService>.Instance,
            () => new DateTimeOffset(2026, 9, 6, 16, 32, 0, TimeSpan.Zero),
            () => true,
            () => appData,
            () => null,
            Path.Combine(temp.Path, "cli-self-heal.jsonl"));

        (bool Available, string? Version, string Path) Probe()
            => new FileInfo(launcher).Length > LocalCliRepairService.LauncherStubMaxBytes
                ? (true, "2.1.263 (Claude Code)", shim)
                : (false, null, shim);

        var repaired = await service.ProbeAndRepairAsync(
            "claude", "2.1.261", Probe, CancellationToken.None);

        Assert.True(repaired.Available);
        Assert.Equal(0, installer.ScriptCalls);
        Assert.Equal(1, installer.Calls);
        Assert.Equal("@anthropic-ai/claude-code@2.1.263", installer.LastPackage);
        Assert.Equal(NpmGlobalInstallMode.Install, installer.LastMode);
        Assert.Empty(service.Current());
    }

    /// <summary>
    /// The stub is journalled on sight, so a suppressed repair still leaves the
    /// operator with an active failure instead of a silently broken CLI.
    /// </summary>
    [Fact]
    public async Task Launcher_stub_stays_an_active_failure_while_repair_is_suppressed()
    {
        using var temp = new TempDirectory();
        var appData = Path.Combine(temp.Path, "appdata");
        var npmBin = Path.Combine(appData, "npm");
        var packageDir = Path.Combine(npmBin, "node_modules", "@anthropic-ai", "claude-code");
        var launcher = Path.Combine(packageDir, "bin", "claude.exe");
        WriteLauncherPackage(packageDir, "2.1.263", launcherBytes: 500);
        File.WriteAllText(Path.Combine(packageDir, "install.cjs"), "// npm postinstall");
        var shim = Path.Combine(npmBin, "claude.cmd");
        File.WriteAllText(shim, "command shim");
        var journal = Path.Combine(temp.Path, "cli-self-heal.jsonl");
        var now = new DateTimeOffset(2026, 9, 6, 7, 45, 0, TimeSpan.Zero);
        var healed = false;
        // The postinstall re-run itself fails; the operator alarm must survive.
        var installer = new FakeInstaller(
            (_, _) => { },
            onScript: _ => { },
            scriptOutcome: NpmGlobalInstallOutcome.Failed);
        var service = new LocalCliRepairService(
            installer,
            NullLogger<LocalCliRepairService>.Instance,
            () => now,
            () => true,
            () => appData,
            () => null,
            journal);

        (bool Available, string? Version, string Path) Probe()
            => healed ? (true, "2.1.263 (Claude Code)", shim) : (false, null, shim);

        await service.ProbeAndRepairAsync("claude", "2.1.261", Probe, CancellationToken.None);
        Assert.Equal("failed", Assert.Single(service.Current()).Outcome);

        // Next probe cycle, eight minutes later: inside the attempt budget.
        now = now.AddMinutes(8);
        await service.ProbeAndRepairAsync("claude", "2.1.261", Probe, CancellationToken.None);
        Assert.Equal(1, installer.ScriptCalls);
        var active = Assert.Single(service.Current());
        Assert.Equal("failed", active.Outcome);
        Assert.Contains("postinstall-rerun", active.Detail, StringComparison.Ordinal);

        healed = true;
        now = now.AddMinutes(8);
        var recovered = await service.ProbeAndRepairAsync(
            "claude", "2.1.261", Probe, CancellationToken.None);

        Assert.True(recovered.Available);
        Assert.Empty(service.Current());
        Assert.Contains("\"outcome\":\"resolved\"", File.ReadAllText(journal), StringComparison.Ordinal);
        Assert.Empty(new LocalCliRepairService(
            installer,
            NullLogger<LocalCliRepairService>.Instance,
            () => now,
            () => true,
            () => appData,
            () => null,
            journal).Current());
    }

    [Fact]
    public async Task Launcher_stub_is_projected_when_the_attempt_budget_blocks_the_repair()
    {
        using var temp = new TempDirectory();
        var appData = Path.Combine(temp.Path, "appdata");
        var npmBin = Path.Combine(appData, "npm");
        var packageDir = Path.Combine(npmBin, "node_modules", "@anthropic-ai", "claude-code");
        WriteLauncherPackage(packageDir, "2.1.263", launcherBytes: 500);
        File.WriteAllText(Path.Combine(packageDir, "install.cjs"), "// npm postinstall");
        var shim = Path.Combine(npmBin, "claude.cmd");
        File.WriteAllText(shim, "command shim");
        var journal = Path.Combine(temp.Path, "cli-self-heal.jsonl");
        var now = new DateTimeOffset(2026, 9, 6, 8, 30, 0, TimeSpan.Zero);
        File.WriteAllText(journal, JsonSerializer.Serialize(new
        {
            timestamp = now.AddMinutes(-10),
            cliType = "claude",
            outcome = "attempting",
            detection = "launcher-stub-with-package-present",
            detail = "earlier attempt in this hour",
        }) + Environment.NewLine);
        var installer = new FakeInstaller((_, _) => { }, onScript: _ => { });
        var service = new LocalCliRepairService(
            installer,
            NullLogger<LocalCliRepairService>.Instance,
            () => now,
            () => true,
            () => appData,
            () => null,
            journal);

        var result = await service.ProbeAndRepairAsync(
            "claude", "2.1.261", () => (false, null, shim), CancellationToken.None);

        Assert.False(result.Available);
        Assert.Equal(0, installer.ScriptCalls);
        Assert.Equal(0, installer.Calls);
        var active = Assert.Single(service.Current());
        Assert.Equal("detected", active.Outcome);
        Assert.Contains("placeholder", active.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The launcher package as it sits on disk after a partial auto-update: a
    /// placeholder <c>bin/claude.exe</c> beside the extracted native package.
    /// </summary>
    private static void WriteLauncherPackage(
        string packageDirectory,
        string version,
        int launcherBytes)
    {
        Directory.CreateDirectory(Path.Combine(packageDirectory, "bin"));
        File.WriteAllText(
            Path.Combine(packageDirectory, "package.json"),
            $$$"""{"version":"{{{version}}}","bin":{"claude":"bin/claude.exe"}}""");
        File.WriteAllBytes(
            Path.Combine(packageDirectory, "bin", "claude.exe"),
            new byte[launcherBytes]);
        Directory.CreateDirectory(Path.Combine(
            packageDirectory, "node_modules", "@anthropic-ai", "claude-code-win32-x64"));
    }

    private sealed class FakeInstaller(
        Action<string, NpmGlobalInstallMode> onInstall,
        NpmGlobalInstallOutcome outcome = NpmGlobalInstallOutcome.Succeeded,
        Action<string>? onScript = null,
        NpmGlobalInstallOutcome scriptOutcome = NpmGlobalInstallOutcome.Succeeded) : NpmGlobalInstaller
    {
        public int Calls { get; private set; }
        public int ScriptCalls { get; private set; }
        public NpmGlobalInstallMode? LastMode { get; private set; }
        public string? LastPackage { get; private set; }
        public string? LastScriptDirectory { get; private set; }
        public string? LastScript { get; private set; }

        public override Task<NpmGlobalInstallResult> InstallAsync(
            string packageName,
            NpmGlobalInstallMode mode,
            CancellationToken ct)
        {
            Calls++;
            LastMode = mode;
            LastPackage = packageName;
            onInstall(packageName, mode);
            return Task.FromResult(new NpmGlobalInstallResult(
                outcome,
                outcome == NpmGlobalInstallOutcome.Succeeded ? 0 : 1,
                outcome == NpmGlobalInstallOutcome.Succeeded ? "installed" : "",
                outcome == NpmGlobalInstallOutcome.Succeeded ? "" : "install failed"));
        }

        public override Task<NpmGlobalInstallResult> RunPackageInstallScriptAsync(
            string packageDirectory,
            string scriptFileName,
            CancellationToken ct)
        {
            ScriptCalls++;
            LastScriptDirectory = packageDirectory;
            LastScript = scriptFileName;
            onScript?.Invoke(packageDirectory);
            return Task.FromResult(new NpmGlobalInstallResult(
                scriptOutcome,
                scriptOutcome == NpmGlobalInstallOutcome.Succeeded ? 0 : 1,
                "",
                scriptOutcome == NpmGlobalInstallOutcome.Succeeded ? "" : "postinstall failed"));
        }
    }

    private sealed class CollectingLogger<T>(List<string> entries) : Microsoft.Extensions.Logging.ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => entries.Add(formatter(state, exception));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "agent-studio-cli-repair-tests",
            Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* best effort */ }
        }
    }
}
