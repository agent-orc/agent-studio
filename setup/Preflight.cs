using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace AgentStudio.Setup;

internal enum PreflightStatus
{
    Pass,
    Fail,
    NotApplicable,
}

internal sealed record PreflightFinding(string Check, PreflightStatus Status, string Observed, string? Recovery);

/// <summary>Observed host facts. Null means the probe did not apply to the journey.</summary>
internal sealed record HostFacts(
    bool Windows,
    bool X64,
    bool? Virtualization,
    bool? DockerEngine,
    bool? DockerCompose,
    bool? Wsl2,
    long? FreeDiskBytes,
    bool? DnsResolves,
    bool? WireGuardInterface,
    bool? TlsTrusted,
    bool? BackupMountWritable,
    bool? ProviderCli,
    bool? ProtectedSecretFile);

/// <summary>Pure preflight policy: observed facts in, findings with recovery actions out.</summary>
internal static class PreflightPolicy
{
    public const long MinimumFreeDiskBytes = 20L * 1024 * 1024 * 1024;

    public static IReadOnlyList<PreflightFinding> Evaluate(InstallationJourney journey, HostFacts facts)
    {
        var findings = new List<PreflightFinding>
        {
            Check("Platform", facts.X64, "x64 host",
                "Use a Linux x64 or Windows x64 machine; other architectures are not released."),
            Check("VM / virtualization", facts.Virtualization, "hardware virtualization available",
                facts.Windows
                    ? "Enable virtualization (VT-x/AMD-V) in firmware and the Virtual Machine Platform Windows feature, then reboot."
                    : "On a VM, enable nested virtualization or use a host with KVM; Docker containers need a working kernel namespace setup."),
        };

        var docker = journey is InstallationJourney.OneBox or InstallationJourney.RelocateAuthority;
        if (facts.Windows && docker)
        {
            findings.Add(Check("WSL", facts.Wsl2, "WSL 2 installed",
                "Run 'wsl --install' as administrator, reboot, then select the WSL 2 backend in Docker Desktop."));
        }
        if (docker)
        {
            findings.Add(Check("Docker", facts.DockerEngine, "Docker engine running",
                facts.Windows
                    ? "Install Docker Desktop (https://docs.docker.com/desktop/setup/install/windows-install/) and start it."
                    : "Install Docker Engine (https://docs.docker.com/engine/install/), then 'sudo systemctl enable --now docker' and add your user to the docker group."));
            findings.Add(Check("Docker Compose", facts.DockerCompose, "Compose v2 available",
                "Install the docker-compose-plugin package (Linux) or update Docker Desktop; 'docker compose version' must succeed."));
        }

        findings.Add(facts.FreeDiskBytes is null
            ? new("Storage", PreflightStatus.NotApplicable, "not probed", null)
            : facts.FreeDiskBytes >= MinimumFreeDiskBytes
                ? new("Storage", PreflightStatus.Pass, $"{facts.FreeDiskBytes / (1024 * 1024 * 1024)} GiB free", null)
                : new("Storage", PreflightStatus.Fail, $"{facts.FreeDiskBytes / (1024 * 1024 * 1024)} GiB free",
                    "Free at least 20 GiB for repositories, run worktrees and backups, or choose --install-dir on a larger volume."));

        findings.Add(Check("DNS", facts.DnsResolves, "authority name resolves",
            "Add the private Task Server name to the WireGuard DNS resolver or /etc/hosts (C:\\Windows\\System32\\drivers\\etc\\hosts), then retry."));
        findings.Add(Check("WireGuard", facts.WireGuardInterface, "WireGuard interface up",
            "Install WireGuard, import this peer's configuration from the control-plane administrator and bring the tunnel up ('wg-quick up wg0')."));
        findings.Add(Check("TLS", facts.TlsTrusted, "authority certificate trusted",
            "Install the private CA certificate or pin the published certificate fingerprint; never disable certificate validation."));
        findings.Add(Check("Backup mount", facts.BackupMountWritable, "off-host backup mount writable",
            "Mount the off-host backup destination (NFS, SMB or block device) and make it writable for the installer before retrying."));
        findings.Add(Check("Provider", facts.ProviderCli, "provider CLI installed (login is proven at host enrolment)",
            "Install Codex or Claude for the execution user and complete its login on this host; setup never copies provider credentials."));
        findings.Add(Check("Secret file", facts.ProtectedSecretFile, "token file owner-only",
            "Restrict the token file to its owner ('chmod 600 FILE' or an owner-only ACL) and deliver it over a trusted channel, not chat or task text."));
        return findings;
    }

    /// <summary>
    /// AGT-W51 owns execution isolation. A Windows workstation runs execution in
    /// Linux containers and advertises that Linux platform; setup grants no
    /// native Windows execution exemption.
    /// </summary>
    public static string? ExecutionPlatformNote(InstallationJourney journey, bool windows)
        => windows && journey is InstallationJourney.OneBox or InstallationJourney.JoinHost
            ? "Execution on this Windows workstation runs in Linux containers under Docker Desktop/WSL 2 and advertises linux-x64. Native Windows execution is not exempted from AGT-W51 isolation."
            : null;

    private static PreflightFinding Check(string name, bool? observed, string expectation, string recovery)
        => observed switch
        {
            null => new(name, PreflightStatus.NotApplicable, "not required for this journey", null),
            true => new(name, PreflightStatus.Pass, expectation, null),
            false => new(name, PreflightStatus.Fail, $"missing: {expectation}", recovery),
        };
}

/// <summary>Side-effecting probes; each returns null when the journey does not need it.</summary>
internal static class PreflightProbe
{
    public static async Task<HostFacts> ObserveAsync(
        InstallationJourney journey,
        string? authorityUrl,
        string? backupPath,
        string? secretFile,
        string installRoot,
        CancellationToken cancellationToken)
    {
        var windows = OperatingSystem.IsWindows();
        var docker = journey is InstallationJourney.OneBox or InstallationJourney.RelocateAuthority;
        var remote = journey is InstallationJourney.JoinHost or InstallationJourney.AttachStudio;
        var host = Uri.TryCreate(authorityUrl, UriKind.Absolute, out var uri) ? uri : null;
        var processes = new ProcessRunner(false);

        return new HostFacts(
            windows,
            RuntimeInformation.OSArchitecture == Architecture.X64,
            await VirtualizationAsync(windows, processes),
            docker ? await SucceedsAsync(processes, "docker", ["info", "--format", "{{.ServerVersion}}"]) : null,
            docker ? await SucceedsAsync(processes, "docker", ["compose", "version"]) : null,
            windows && docker ? await SucceedsAsync(processes, "wsl", ["--status"]) : null,
            FreeBytes(installRoot),
            remote && host is not null && !host.IsLoopback ? await ResolvesAsync(host.Host, cancellationToken) : null,
            journey == InstallationJourney.RelocateAuthority
                || (remote && host is not null && !host.IsLoopback && IsPrivate(host.Host))
                ? WireGuardUp(windows)
                : null,
            remote && host?.Scheme == Uri.UriSchemeHttps ? await TlsTrustedAsync(host, cancellationToken) : null,
            backupPath is null ? null : Writable(backupPath),
            journey == InstallationJourney.JoinHost
                ? await SucceedsAsync(processes, "codex", ["--version"])
                  || await SucceedsAsync(processes, "claude", ["--version"])
                : null,
            secretFile is null ? null : SetupSecrets.IsProtected(secretFile));
    }

    private static async Task<bool?> VirtualizationAsync(bool windows, ProcessRunner processes)
    {
        if (!windows)
            return Directory.Exists("/sys/fs/cgroup") ? true : null;
        return await SucceedsAsync(processes, "powershell",
            ["-NoProfile", "-Command",
             "if ((Get-CimInstance Win32_ComputerSystem).HypervisorPresent) { exit 0 } else { exit 1 }"]);
    }

    private static async Task<bool> SucceedsAsync(ProcessRunner processes, string file, string[] args)
    {
        try
        {
            return (await processes.RunAsync(file, args, printOutput: false)).ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static long? FreeBytes(string path)
    {
        var existing = Path.GetFullPath(path);
        while (!Directory.Exists(existing) && Path.GetDirectoryName(existing) is { } parent)
            existing = parent;
        try { return new DriveInfo(existing).AvailableFreeSpace; }
        catch (Exception) { return null; }
    }

    private static async Task<bool> ResolvesAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out _)) return true;
        try { return (await Dns.GetHostAddressesAsync(host, cancellationToken)).Length > 0; }
        catch (SocketException) { return false; }
    }

    private static bool IsPrivate(string host)
        => !IPAddress.TryParse(host, out var address)
            ? host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
              || host.EndsWith(".wg", StringComparison.OrdinalIgnoreCase)
            : address.GetAddressBytes() is [10, ..] or [192, 168, ..] or [172, >= 16 and <= 31, ..];

    private static bool WireGuardUp(bool windows)
        => System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Any(nic =>
            nic.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
            && (nic.Name.StartsWith("wg", StringComparison.OrdinalIgnoreCase)
                || nic.Description.Contains("WireGuard", StringComparison.OrdinalIgnoreCase)))
           || (!windows && Directory.Exists("/sys/class/net")
               && Directory.EnumerateDirectories("/sys/class/net", "wg*").Any());

    private static async Task<bool> TlsTrustedAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await http.GetAsync(new Uri(uri, "/healthz"), cancellationToken);
            return true;
        }
        catch (HttpRequestException error) when (error.InnerException is System.Security.Authentication.AuthenticationException)
        {
            return false;
        }
        catch (Exception)
        {
            // Unreachable is reported by DNS/WireGuard; TLS is only judged on a completed handshake attempt.
            return false;
        }
    }

    private static bool Writable(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return false;
            var probe = Path.Combine(path, $".agent-setup-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            return true;
        }
        catch (Exception) { return false; }
    }
}

internal static class SetupSecrets
{
    /// <summary>A secret file must exist, be non-empty and readable only by its owner.</summary>
    public static bool IsProtected(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0) return false;
        if (OperatingSystem.IsWindows()) return true;
        var mode = File.GetUnixFileMode(path);
        const UnixFileMode open = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                                  | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        return (mode & open) == 0;
    }

    public static void RequireProtected(string path, string label)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"{label} not found: {path}", path);
        if (!IsProtected(path))
            throw new InvalidOperationException(
                $"{label} {path} is empty or readable by other users. Run 'chmod 600 {path}' (or restrict its ACL to the owner) and retry; setup does not accept secrets in arguments or answer files.");
    }
}
