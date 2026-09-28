namespace AgentStudio.Setup;

/// <summary>Where a product command is carried out.</summary>
internal enum ProductProfile
{
    /// <summary>One-box Compose stack from the verified Compose bundle.</summary>
    StudioDocker,

    /// <summary>D6 Task Server, Engine, and Studio connector as Windows scheduled tasks.</summary>
    StudioWindowsServices,

    /// <summary>Windows Studio connector pointed at a remote Task Server.</summary>
    ConnectorWindows,

    /// <summary>Linux native and remote flows owned by <see cref="SetupApplication"/>.</summary>
    Delegated,
}

internal sealed record ProductPlan(
    ProductProfile Profile,
    string Mode,
    string Target,
    IReadOnlyList<string> DelegatedArguments);

/// <summary>
/// Parsed product command line. Every invocation except the legacy-only
/// <c>demo</c> and <c>single</c> modes is parsed here, so the mode and target
/// vocabulary is translated in exactly one place whatever other flags are set.
/// </summary>
internal sealed record ProductCommand(
    string Verb,
    IReadOnlyDictionary<string, string> Values,
    IReadOnlySet<string> Flags,
    IReadOnlyList<string> Passthrough)
{
    private static readonly HashSet<string> FlagOptions = new(StringComparer.Ordinal)
    {
        "--unattended", "--purge", "--dry-run", "--uninstall", "--offline",
        "--help", "-h", "--version",
    };

    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        "--mode", "--target", "--answer-file", "--release-version", "--release-dir",
        "--install-dir", "--ui-port", "--server-url", "--join-token-file", "--token-file",
    };

    // Options only the delegated Linux flows understand. They are forwarded
    // verbatim and rejected for the Docker and Windows profiles.
    private static readonly HashSet<string> DelegatedValueOptions = new(StringComparer.Ordinal)
    {
        "--listen-url", "--runner-name", "--execution-user", "--agent-cli", "--git-remote",
        "--git-push-remote", "--wg-address", "--offhost-backup-path", "--role",
        "--max-parallelism",
    };

    private static readonly string[] Verbs = ["update", "rollback", "uninstall"];

    public bool Has(string flag) => Flags.Contains(flag);

    public bool Unattended => Has("--unattended");

    public bool DryRun => Has("--dry-run");

    /// <summary>The demo and single modes keep their original parser and prompts.</summary>
    internal static bool IsLegacyOnly(string[] args)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (args[index] == "--mode" && args[index + 1].Trim().ToLowerInvariant() is
                "demo" or "single" or "single-machine")
                return true;
        }
        return false;
    }

    public static ProductCommand Parse(string[] args)
    {
        var verb = args.FirstOrDefault() is { } first && Verbs.Contains(first) ? first : "install";
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        var passthrough = new List<string>();
        for (var index = verb == "install" ? 0 : 1; index < args.Length; index++)
        {
            var option = args[index];
            if (FlagOptions.Contains(option))
            {
                flags.Add(option);
                continue;
            }
            if (option == "--non-interactive")
            {
                flags.Add("--unattended");
                continue;
            }
            if (option == "--join")
            {
                values.TryAdd("--mode", "agent-host");
                continue;
            }
            var delegated = DelegatedValueOptions.Contains(option);
            if (!delegated && !ValueOptions.Contains(option))
                throw new ArgumentException($"Unknown option: {option}");
            if (++index == args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"{option} requires a value.");
            if (delegated) passthrough.AddRange([option, args[index]]);
            else values[option] = args[index];
        }
        if (flags.Contains("--uninstall")) verb = "uninstall";
        if (flags.Contains("--purge") && verb != "uninstall")
            throw new ArgumentException("--purge requires uninstall or --uninstall.");
        return new ProductCommand(verb, values, flags, passthrough);
    }

    public static string NormalizeMode(string? mode, bool joinTokenPresent)
        => mode?.Trim().ToLowerInvariant() switch
        {
            null or "" => joinTokenPresent ? "agent-host" : "studio",
            "studio" => "studio",
            "control" or "control-plane" => "control-plane",
            "host" or "agent-host" => "agent-host",
            "connector" => "connector",
            _ => throw new ArgumentException(
                "--mode must be studio, control-plane, agent-host, or connector."),
        };

    /// <summary><c>systemd</c> stays accepted as the Linux spelling of <c>native</c>.</summary>
    public static string NormalizeTarget(string? target, string mode)
        => target?.Trim().ToLowerInvariant() switch
        {
            null or "" => mode is "agent-host" or "connector" ? "native" : "docker",
            "docker" => "docker",
            "native" or "systemd" => "native",
            _ => throw new ArgumentException("--target must be docker or native."),
        };
}

internal static class ProductPlanner
{
    /// <summary>
    /// Pure routing decision. <paramref name="forwarded"/> holds the resolved
    /// product values (command line first, then answer file) that the
    /// delegated flows understand.
    /// </summary>
    public static ProductPlan Plan(
        ProductCommand command,
        string mode,
        string target,
        bool windows,
        IReadOnlyList<(string Option, string Value)> forwarded)
    {
        var profile = (mode, target) switch
        {
            ("studio", "docker") => ProductProfile.StudioDocker,
            ("studio", "native") => windows
                ? ProductProfile.StudioWindowsServices
                : ProductProfile.Delegated,
            ("connector", "native") => windows
                ? ProductProfile.ConnectorWindows
                : throw new PlatformNotSupportedException(
                    "Connector mode connects a Windows device to a remote Task Server. " +
                    "On Linux, use --mode agent-host to join a Task Server."),
            ("connector", _) => throw new ArgumentException(
                "Connector mode installs a Windows service; use --target native."),
            ("agent-host", "docker") => throw new PlatformNotSupportedException(
                "Docker agent-host setup is not available in this release. Use --target native on Linux."),
            ("control-plane" or "agent-host", _) => ProductProfile.Delegated,
            _ => throw new ArgumentException($"Unsupported mode {mode}."),
        };

        if (profile != ProductProfile.Delegated)
        {
            if (command.Passthrough.Count > 0)
                throw new ArgumentException(
                    $"Unknown option for --mode {mode} --target {target}: {command.Passthrough[0]}");
            return new ProductPlan(profile, mode, target, []);
        }

        if (windows)
            throw new PlatformNotSupportedException(
                $"--mode {mode} installs Linux services. Run the Linux x64 setup binary on the target host.");
        if (command.Verb != "install")
            throw new ArgumentException(mode == "studio"
                ? $"{command.Verb} for the Linux native profile uses update.sh and rollback.sh in /opt/agent-orchestrator/current; see docs/operations/setup/multi-machine.md."
                : $"{command.Verb} is supported for the Studio and connector installations only.");

        var arguments = new List<string>
        {
            "--mode", mode == "studio" ? "single" : mode,
        };
        if (mode == "control-plane")
            arguments.AddRange(["--target", target == "native" ? "systemd" : "docker"]);
        foreach (var (option, value) in forwarded)
            arguments.AddRange([option, value]);
        arguments.AddRange(command.Passthrough);
        if (command.Unattended) arguments.Add("--non-interactive");
        if (command.DryRun) arguments.Add("--dry-run");
        return new ProductPlan(profile, mode, target, arguments);
    }
}
