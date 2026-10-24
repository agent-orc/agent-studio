using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;

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
    bool? ProtectedSecretFile,
    bool? AuthorityReachable = null,
    bool? AuthorityUrlKnown = null,
    bool? GitInstalled = null,
    bool? WindowsVersionSupported = null,
    bool? GitAvailable = null,
    bool? NativePortsAvailable = null,
    bool? ExistingInstallation = null);

/// <summary>Pure preflight policy: observed facts in, findings with recovery actions out.</summary>
internal static class PreflightPolicy
{
    public const long MinimumFreeDiskBytes = 20L * 1024 * 1024 * 1024;

    public static IReadOnlyList<PreflightFinding> Evaluate(InstallationJourney journey, string target, HostFacts facts)
    {
        var remote = journey is InstallationJourney.JoinHost or InstallationJourney.AttachStudio or InstallationJourney.RelocateAuthority;
        var authority = journey is InstallationJourney.OneBox or InstallationJourney.RelocateAuthority;
        var findings = new List<PreflightFinding>
        {
            Check("Platform", facts.X64, "x64 host",
                "Use a Linux x64 or Windows x64 machine; other architectures are not released."),
            Check("VM / virtualization", facts.Windows && target == "docker" ? facts.Virtualization : null, "hardware virtualization available",
                facts.Windows
                    ? "Enable virtualization (VT-x/AMD-V) in firmware and the Virtual Machine Platform Windows feature, then reboot."
                    : "On a VM, enable nested virtualization or use a host with KVM; Docker containers need a working kernel namespace setup."),
        };
        if (facts.Windows && target == "native" && journey == InstallationJourney.OneBox)
        {
            findings.Add(Check("Windows version", facts.WindowsVersionSupported, "Windows 10 22H2, Windows 11, or Windows Server 2022 or newer",
                "Update Windows to a supported x64 release and retry."));
            findings.Add(Check("Git for Windows", facts.GitAvailable, "git.exe available",
                "Install Git for Windows and reopen setup so git.exe is on PATH."));
            findings.Add(Check("Local ports", facts.NativePortsAvailable, "4011, 5031 and 5071 available",
                "Close the processes using 4011, 5031 or 5071, then retry. Existing Agent Studio services are reused during repair."));
            findings.Add(new("Existing installation", PreflightStatus.Pass,
                facts.ExistingInstallation == true ? "repair or update available" : "fresh install", null));
        }

        var docker = target == "docker" && journey is (InstallationJourney.OneBox or InstallationJourney.RelocateAuthority);
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

        var needsStorage = journey is InstallationJourney.OneBox or InstallationJourney.JoinHost or InstallationJourney.RelocateAuthority;
        findings.Add(!needsStorage || facts.FreeDiskBytes is null
            ? new("Storage", PreflightStatus.NotApplicable, "not probed", null)
            : facts.FreeDiskBytes >= MinimumFreeDiskBytes
                ? new("Storage", PreflightStatus.Pass, $"{facts.FreeDiskBytes / (1024 * 1024 * 1024)} GiB free", null)
                : new("Storage", PreflightStatus.Fail, $"{facts.FreeDiskBytes / (1024 * 1024 * 1024)} GiB free",
                    "Free at least 20 GiB for repositories, run worktrees and backups, or choose --install-dir on a larger volume."));

        findings.Add(Check("Authority URL", remote ? facts.AuthorityUrlKnown : null, "existing authority URL known",
            "Pass --server-url with the authority's HTTPS URL; a join-host run reads its selected route from the join token, including loopback HTTP for a reverse tunnel."));
        findings.Add(Check("DNS", remote ? facts.DnsResolves : null, "authority name resolves",
            "Resolve the selected Task Server name through DNS or the host's hosts file, then retry."));
        findings.Add(Check("WireGuard", journey == InstallationJourney.RelocateAuthority ? facts.WireGuardInterface : null, "WireGuard interface up",
            "Install WireGuard, import this peer's configuration from the control-plane administrator and bring the tunnel up ('wg-quick up wg0')."));
        findings.Add(Check("Connectivity", remote ? facts.AuthorityReachable : null, "authority reachable",
            "Check the selected URL, listener, firewall and tunnel or LAN route from this host, then retry."));
        findings.Add(Check("TLS", remote ? facts.TlsTrusted : null, "authority certificate trusted",
            "Install the private CA certificate or pin the published certificate fingerprint; never disable certificate validation."));
        findings.Add(Check("Backup mount", authority ? facts.BackupMountWritable : null, "off-host backup mount writable",
            "Mount the off-host backup destination (NFS, SMB or block device) and make it writable for the installer before retrying."));
        findings.Add(Check("Provider", journey == InstallationJourney.JoinHost ? facts.ProviderCli : null, "provider CLI installed (login is proven at host enrolment)",
            "Install Codex or Claude for the execution user and complete its login on this host; setup never copies provider credentials."));
        findings.Add(Check("Git", journey is InstallationJourney.OneBox or InstallationJourney.JoinHost ? facts.GitInstalled : null,
            "Git installed", "Install Git and make 'git --version' work for the installation user, then rerun setup."));
        findings.Add(Check("Secret file", journey is InstallationJourney.JoinHost or InstallationJourney.AttachStudio or InstallationJourney.RelocateAuthority ? facts.ProtectedSecretFile : null, "token file owner-only",
            "Restrict the token file to its owner ('chmod 600 FILE' or an owner-only ACL) and deliver it over a trusted channel, not chat or task text."));
        return findings;
    }

    /// <summary>
    /// AGT-W51 owns execution isolation. A Windows workstation runs execution in
    /// Linux containers and advertises that Linux platform; setup grants no
    /// native Windows execution exemption.
    /// </summary>
    public static string? ExecutionPlatformNote(InstallationJourney journey, bool windows, string target = "docker")
        => windows && target == "docker" && journey == InstallationJourney.OneBox
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
    /// <param name="authorityUrl">The existing authority this run contacts. It is the
    /// join token's URL for join-host, never a URL the run is about to create.</param>
    /// <param name="contactsAuthority">True when the run needs an existing authority;
    /// a missing or unusable URL is then a failure, not a skipped probe.</param>
    public static async Task<HostFacts> ObserveAsync(
        InstallationJourney journey,
        string target,
        string? authorityUrl,
        bool contactsAuthority,
        string? backupPath,
        string? secretFile,
        string installRoot,
        CancellationToken cancellationToken,
        string? executionUser = null)
    {
        var windows = OperatingSystem.IsWindows();
        var docker = target == "docker" && journey is (InstallationJourney.OneBox or InstallationJourney.RelocateAuthority);
        var host = contactsAuthority && Uri.TryCreate(authorityUrl, UriKind.Absolute, out var uri)
                   && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri : null;
        var authority = host is not null
            ? await AuthorityAsync(host, cancellationToken) : (Reachable: (bool?)null, Trusted: (bool?)null);
        var processes = new ProcessRunner(false);

        return new HostFacts(
            windows,
            RuntimeInformation.OSArchitecture == Architecture.X64,
            windows && docker ? await VirtualizationAsync(windows, processes) : null,
            docker ? await SucceedsAsync(processes, "docker", ["info", "--format", "{{.ServerVersion}}"]) : null,
            docker ? await SucceedsAsync(processes, "docker", ["compose", "version"]) : null,
            windows && docker ? await SucceedsAsync(processes, "wsl", ["--status"]) : null,
            journey is InstallationJourney.OneBox or InstallationJourney.JoinHost or InstallationJourney.RelocateAuthority
                ? FreeBytes(installRoot) : null,
            host is not null && !host.IsLoopback ? await ResolvesAsync(host.Host, cancellationToken) : null,
            journey == InstallationJourney.RelocateAuthority && contactsAuthority
                ? WireGuardUp(windows)
                : null,
            authority.Trusted,
            journey is (InstallationJourney.OneBox or InstallationJourney.RelocateAuthority) && backupPath is not null
                ? Writable(backupPath) : null,
            // Check the selected user's PATH when known. Guided setup selects
            // the user later and checks version and login before enrolment.
            journey == InstallationJourney.JoinHost && !string.IsNullOrWhiteSpace(executionUser)
                ? await processes.FindCommandAsync("codex", executionUser) is not null
                  || await processes.FindCommandAsync("claude", executionUser) is not null
                : null,
            journey is (InstallationJourney.JoinHost or InstallationJourney.AttachStudio or InstallationJourney.RelocateAuthority) && secretFile is not null
                ? SetupSecrets.IsProtected(secretFile) : null,
            authority.Reachable,
            contactsAuthority ? host is not null : null,
            journey is InstallationJourney.OneBox or InstallationJourney.JoinHost
                ? await SucceedsAsync(processes, "git", ["--version"])
                : null,
            windows && target == "native" && journey == InstallationJourney.OneBox
                ? Environment.OSVersion.Version.Build >= 19045 : null,
            windows && target == "native" && journey == InstallationJourney.OneBox
                ? await SucceedsAsync(processes, "git", ["--version"]) : null,
            windows && target == "native" && journey == InstallationJourney.OneBox
                ? File.Exists(Path.Combine(installRoot, "install-state.json")) || NativePortsFree() : null,
            windows && target == "native" && journey == InstallationJourney.OneBox
                ? File.Exists(Path.Combine(installRoot, "install-state.json")) : null);
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

    /// <summary>
    /// Reachability is probed for every authority URL; certificate trust only
    /// applies to https and only after a TLS handshake happened.
    /// </summary>
    internal static async Task<(bool? Reachable, bool? Trusted)> AuthorityAsync(Uri uri, CancellationToken cancellationToken)
    {
        var https = uri.Scheme == Uri.UriSchemeHttps;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await http.GetAsync(new Uri(uri, "/healthz"), cancellationToken);
            return (true, https ? true : null);
        }
        catch (HttpRequestException error) when (https && error.InnerException is System.Security.Authentication.AuthenticationException)
        {
            return (true, false);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            return (false, null);
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

    private static bool NativePortsFree()
    {
        foreach (var port in new[] { 4011, 5031, 5071 })
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
            }
            catch (SocketException) { return false; }
        }
        return true;
    }
}

internal static class SetupSecrets
{
    public static void WriteOwnerOnly(string path, string content)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(path, content);
            return;
        }
        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }

    /// <summary>A secret file must exist, be non-empty and readable only by its owner.</summary>
    public static bool IsProtected(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0
            || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
        if (OperatingSystem.IsWindows()) return WindowsOwnerOnly(path);
        var mode = File.GetUnixFileMode(path);
        const UnixFileMode open = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                                  | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        return (mode & open) == 0;
    }

    [SupportedOSPlatform("windows")]
    private static bool WindowsOwnerOnly(string path)
    {
        try
        {
            var acl = new FileInfo(path).GetAccessControl();
            var owner = acl.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            var current = WindowsIdentity.GetCurrent().User;
            if (owner is null || current is null || !owner.Equals(current)) return false;
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                if (rule.FileSystemRights == 0) continue;
                var sid = (SecurityIdentifier)rule.IdentityReference;
                if (!sid.Equals(owner) && !sid.Equals(system) && !sid.Equals(administrators))
                    return false;
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or System.Security.SecurityException or PlatformNotSupportedException)
        {
            return false;
        }
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
