namespace AgentStudio.Setup;

internal enum SetupMode
{
    Guided,
    Demo,
    SingleMachine,
    ControlPlane,
    AgentHost,
}

internal enum SetupTarget
{
    Systemd,
    Docker,
}

internal sealed record SetupOptions(
    SetupMode Mode,
    SetupTarget Target,
    string? ReleaseVersion,
    string? ReleaseDirectory,
    string? ServerUrl,
    string? ListenUrl,
    string? JoinTokenFile,
    string? RunnerName,
    string? ExecutionUser,
    string? AgentCli,
    string? GitRemote,
    string? GitPushRemote,
    string? WireGuardAddress,
    string? OffhostBackupPath,
    string Role,
    int MaxParallelism,
    int DemoPort,
    bool NonInteractive,
    bool DryRun,
    bool ShowHelp,
    bool ShowVersion)
{
    public static SetupOptions Parse(string[] args)
    {
        var mode = SetupMode.Guided;
        var target = SetupTarget.Systemd;
        string? releaseVersion = null;
        string? releaseDirectory = null;
        string? serverUrl = null;
        string? listenUrl = null;
        string? joinTokenFile = null;
        string? runnerName = null;
        string? executionUser = null;
        string? agentCli = null;
        string? gitRemote = null;
        string? gitPushRemote = null;
        string? wireGuardAddress = null;
        string? offhostBackupPath = null;
        var role = "coding";
        var maxParallelism = 2;
        var demoPort = 4011;
        var nonInteractive = false;
        var dryRun = false;
        var showHelp = false;
        var showVersion = false;

        string ValueAt(int index, string option)
        {
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"{option} requires a value.");
            return args[index + 1];
        }

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "-h":
                case "--help":
                    showHelp = true;
                    break;
                case "--version":
                    showVersion = true;
                    break;
                case "--join":
                    mode = SetupMode.AgentHost;
                    break;
                case "--mode":
                    mode = ParseMode(ValueAt(index, "--mode"));
                    index++;
                    break;
                case "--target":
                    target = ParseTarget(ValueAt(index, "--target"));
                    index++;
                    break;
                case "--release-version":
                    releaseVersion = ValueAt(index, "--release-version");
                    index++;
                    break;
                case "--release-dir":
                    releaseDirectory = Path.GetFullPath(ValueAt(index, "--release-dir"));
                    index++;
                    break;
                case "--server-url":
                    serverUrl = ValueAt(index, "--server-url");
                    index++;
                    break;
                case "--listen-url":
                    listenUrl = ValueAt(index, "--listen-url");
                    index++;
                    break;
                case "--join-token-file":
                    joinTokenFile = Path.GetFullPath(ValueAt(index, "--join-token-file"));
                    mode = SetupMode.AgentHost;
                    index++;
                    break;
                case "--runner-name":
                    runnerName = ValueAt(index, "--runner-name");
                    index++;
                    break;
                case "--execution-user":
                    executionUser = ValueAt(index, "--execution-user");
                    index++;
                    break;
                case "--agent-cli":
                    agentCli = ValueAt(index, "--agent-cli");
                    index++;
                    break;
                case "--git-remote":
                    gitRemote = ValueAt(index, "--git-remote");
                    index++;
                    break;
                case "--git-push-remote":
                    gitPushRemote = ValueAt(index, "--git-push-remote");
                    index++;
                    break;
                case "--wg-address":
                    wireGuardAddress = ValueAt(index, "--wg-address");
                    index++;
                    break;
                case "--offhost-backup-path":
                    offhostBackupPath = ValueAt(index, "--offhost-backup-path");
                    index++;
                    break;
                case "--role":
                    role = ValueAt(index, "--role").Trim().ToLowerInvariant();
                    index++;
                    break;
                case "--max-parallelism":
                    if (!int.TryParse(ValueAt(index, "--max-parallelism"), out maxParallelism)
                        || maxParallelism < 1)
                        throw new ArgumentException("--max-parallelism must be a positive integer.");
                    index++;
                    break;
                case "--demo-port":
                    if (!int.TryParse(ValueAt(index, "--demo-port"), out demoPort)
                        || demoPort is < 1 or > 65535)
                        throw new ArgumentException("--demo-port must be between 1 and 65535.");
                    index++;
                    break;
                case "--non-interactive":
                    nonInteractive = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown option: {args[index]}");
            }
        }

        if (role is not ("coding" or "review"))
            throw new ArgumentException("--role must be coding or review.");
        if (nonInteractive && mode == SetupMode.Guided && !showHelp && !showVersion)
            throw new ArgumentException("--non-interactive requires --mode.");
        if (target == SetupTarget.Docker && mode != SetupMode.ControlPlane)
            throw new ArgumentException("--target docker is supported only with --mode control-plane.");

        return new SetupOptions(
            mode,
            target,
            NormalizeVersion(releaseVersion),
            releaseDirectory,
            serverUrl,
            listenUrl,
            joinTokenFile,
            runnerName,
            executionUser,
            agentCli,
            gitRemote,
            gitPushRemote,
            wireGuardAddress,
            offhostBackupPath,
            role,
            maxParallelism,
            demoPort,
            nonInteractive,
            dryRun,
            showHelp,
            showVersion);
    }

    internal static SetupMode ParseMode(string value)
        => value.Trim().ToLowerInvariant() switch
        {
            "demo" => SetupMode.Demo,
            "single" or "single-machine" => SetupMode.SingleMachine,
            "control" or "control-plane" => SetupMode.ControlPlane,
            "host" or "agent-host" => SetupMode.AgentHost,
            _ => throw new ArgumentException(
                "--mode must be demo, single, control-plane, or agent-host."),
        };

    internal static SetupTarget ParseTarget(string value)
        => value.Trim().ToLowerInvariant() switch
        {
            "systemd" => SetupTarget.Systemd,
            "docker" => SetupTarget.Docker,
            _ => throw new ArgumentException("--target must be systemd or docker."),
        };

    internal static string? NormalizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Trim().TrimStart('v');
        var parts = normalized.Split('.');
        if (parts.Length != 3 || parts.Any(part => !int.TryParse(part, out _)))
            throw new ArgumentException("Release version must be X.Y.Z or vX.Y.Z.");
        return normalized;
    }
}
