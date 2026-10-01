using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

public enum TaskServerCommandKind
{
    Serve,
    Version,
    Backup,
    Retention,
    FullBackup,
    Inventory,
    Import,
    Recovery,
}

/// <summary>Installation recovery set workflow; options are validated per operation by the runner.</summary>
public sealed record RecoveryCommandLine(string Operation, IReadOnlyDictionary<string, string> Options, IReadOnlySet<string> Flags)
{
    public string? Option(string name) => Options.TryGetValue(name, out var value) ? value : null;
    public bool Flag(string name) => Flags.Contains(name);
}

public sealed record FullBackupCommandLine(string Operation, string? BackupId, bool Json);

public sealed record RetentionCommandLine(
    string Operation,
    string? Workspace,
    string Policy,
    string? ArchivePath,
    string? Project,
    string? Task,
    string? OutputPath,
    bool Json,
    string? Store = null,
    bool ConfirmColdDelete = false);

public sealed record TaskServerCommandLine(
    TaskServerCommandKind Kind,
    string? BackupName,
    string? Source,
    string? InventoryPath,
    string? WorkspaceName,
    TaskServerMode? Mode,
    string[] HostArguments,
    RetentionCommandLine? Retention = null,
    FullBackupCommandLine? FullBackup = null,
    RecoveryCommandLine? Recovery = null)
{
    public const string RecoveryUsage = """
        Usage:
          task-server recovery capture [--custody <custody.json>] [host options]
          task-server recovery copy --backup <backup-id> --to <off-host-directory> [host options]
          task-server recovery verify --from <copy-directory> [--secret-bundle <path>] [--git-refs <origins.json>] [--no-git]
          task-server recovery restore --from <copy-directory> [--secret-bundle <path>] [--loss-at <utc>] [host options]
          task-server recovery fence-hosts [host options]
          task-server recovery reenrol --principal <runner-principal> --credential-out <file> [host options]
          task-server recovery resume [--check-only] [--old-writer-closed] [--obligations-retained] [--secret-bundle <path>] [host options]
        """;


    public const string RetentionUsage = """
        Usage:
          task-server retention plan --workspace <path> [--policy <file|default>] [--archive <path>] [--project <project>] [--task <key>] [--json]
          task-server retention apply --workspace <path> [--policy <file|default>] [--archive <path>] [--project <project>] [--task <key>] [--json]
          task-server retention restore --workspace <path> --task <key> [--archive <path>] [--json]
          task-server retention re-excerpt --workspace <path> [--archive <path>] [--task <key>] [--json]
          task-server retention backup-full --workspace <path> --out <backup-root> [--json]
          task-server retention verify-full --out <backup-directory> [--json]
          task-server retention restore-full --workspace <empty-path> --out <backup-directory> [--json]

          task-server retention plan --store <STORE_PATH> [--project <project>] [--task <key>] [--json]
          task-server retention apply --store <STORE_PATH> [--project <project>] [--task <key>] [--confirm-cold-delete] [--json]
          task-server retention restore --store <STORE_PATH> --task <key> [--json]

          task-server backup full [--TaskServer:DataDirectory <path>] [--json]
          task-server backup verify-full <backup-id> [--TaskServer:DataDirectory <path>] [--json]
          task-server backup restore-full <backup-id> [--TaskServer:DataDirectory <path>] [--json]
        """;

    public static TaskServerCommandLine Parse(string[] args)
    {
        if (args is ["--version"] or ["-V"])
            return new TaskServerCommandLine(TaskServerCommandKind.Version, null, null, null, null, null, []);
        if (args.Length > 0 && string.Equals(args[0], "retention", StringComparison.OrdinalIgnoreCase))
            return ParseRetention(args);
        if (args.Length > 0 && string.Equals(args[0], "recovery", StringComparison.OrdinalIgnoreCase))
            return ParseRecovery(args);
        if (args.Length == 0 || !IsCommand(args[0]))
            return new TaskServerCommandLine(TaskServerCommandKind.Serve, null, null, null, null, null, args);

        if (args.Length > 1 && args[1] is "full" or "verify-full" or "restore-full")
        {
            var operation = args[1];
            var requiresId = operation is "verify-full" or "restore-full";
            string? backupId = requiresId && args.Length > 2 && !args[2].StartsWith("--", StringComparison.Ordinal)
                ? args[2]
                : null;
            var json = false;
            var hostArgs = new List<string>();
            for (var index = backupId is null ? 2 : 3; index < args.Length; index++)
            {
                if (string.Equals(args[index], "--json", StringComparison.OrdinalIgnoreCase)) { json = true; continue; }
                hostArgs.Add(args[index]);
            }
            if (requiresId && string.IsNullOrWhiteSpace(backupId))
                throw new ArgumentException($"backup {operation} requires a backup id.");
            return new TaskServerCommandLine(
                TaskServerCommandKind.FullBackup, null, null, null, null, null, hostArgs.ToArray(),
                FullBackup: new FullBackupCommandLine(operation, backupId, json));
        }

        string? name = null;
        string? source = null;
        string? inventory = null;
        string? workspace = null;
        TaskServerMode? mode = null;
        var hostArguments = new List<string>();
        for (var index = 1; index < args.Length; index++)
        {
            if (string.Equals(args[index], "--name", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("backup --name requires a value.");
                name = args[++index];
                continue;
            }
            if (ReadOption(args, ref index, "--source", out var sourceValue))
            {
                source = sourceValue;
                continue;
            }
            if (ReadOption(args, ref index, "--inventory", out var inventoryValue))
            {
                inventory = inventoryValue;
                continue;
            }
            if (ReadOption(args, ref index, "--workspace", out var workspaceValue))
            {
                workspace = workspaceValue;
                continue;
            }
            if (ReadOption(args, ref index, "--mode", out var modeValue))
            {
                if (!Enum.TryParse<TaskServerMode>(modeValue, true, out var parsedMode))
                    throw new ArgumentException($"{args[0]} --mode must be normal, draining, readonly, or maintenance.");
                mode = parsedMode;
                continue;
            }
            hostArguments.Add(args[index]);
        }

        var kind = args[0].ToLowerInvariant() switch
        {
            "backup" => TaskServerCommandKind.Backup,
            "inventory" => TaskServerCommandKind.Inventory,
            "import" => TaskServerCommandKind.Import,
            _ => throw new ArgumentException($"Unknown Task Server command '{args[0]}'."),
        };
        if (kind is TaskServerCommandKind.Inventory or TaskServerCommandKind.Import
            && string.IsNullOrWhiteSpace(source))
            throw new ArgumentException($"{args[0]} --source requires a value.");
        if (kind == TaskServerCommandKind.Import && string.IsNullOrWhiteSpace(inventory))
            throw new ArgumentException("import --inventory requires a value.");
        if (kind == TaskServerCommandKind.Import && mode is not null and not TaskServerMode.Maintenance)
            throw new ArgumentException("import --mode only accepts maintenance.");
        return new TaskServerCommandLine(
            kind,
            name,
            source,
            inventory,
            workspace,
            mode,
            hostArguments.ToArray());
    }

    private static TaskServerCommandLine ParseRetention(string[] args)
    {
        if (args.Length < 2 || args[1] is "--help" or "-h" || string.Equals(args[1], "help", StringComparison.OrdinalIgnoreCase))
            return new TaskServerCommandLine(TaskServerCommandKind.Retention, null, null, null, null, null, [],
                new RetentionCommandLine("help", null, "default", null, null, null, null, false));
        var operation = args[1].ToLowerInvariant();
        if (operation is not ("plan" or "apply" or "restore" or "re-excerpt" or "backup-full" or "verify-full" or "restore-full"))
            throw new ArgumentException($"Unknown retention operation '{args[1]}'.");
        string? workspace = null, archive = null, project = null, task = null, output = null, store = null;
        var policy = "default";
        var json = false;
        var confirmColdDelete = false;
        for (var index = 2; index < args.Length; index++)
        {
            if (string.Equals(args[index], "--json", StringComparison.OrdinalIgnoreCase)) { json = true; continue; }
            if (string.Equals(args[index], "--confirm-cold-delete", StringComparison.OrdinalIgnoreCase)) { confirmColdDelete = true; continue; }
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"{args[index]} requires a value.");
            var value = args[++index];
            switch (args[index - 1].ToLowerInvariant())
            {
                case "--workspace": workspace = value; break;
                case "--policy": policy = value; break;
                case "--archive": archive = value; break;
                case "--project": project = value; break;
                case "--task": task = value; break;
                case "--out": output = value; break;
                case "--store": store = value; break;
                default: throw new ArgumentException($"Unknown retention option '{args[index - 1]}'.");
            }
        }
        if (operation is ("plan" or "apply" or "restore" or "re-excerpt")
            && string.IsNullOrWhiteSpace(workspace) && string.IsNullOrWhiteSpace(store))
            throw new ArgumentException($"retention {operation} requires --workspace or --store.");
        if (operation == "backup-full" && string.IsNullOrWhiteSpace(workspace))
            throw new ArgumentException("retention backup-full requires --workspace.");
        if (operation == "restore" && string.IsNullOrWhiteSpace(task))
            throw new ArgumentException("retention restore requires --task.");
        if (operation is ("backup-full" or "verify-full" or "restore-full") && string.IsNullOrWhiteSpace(output))
            throw new ArgumentException($"retention {operation} requires --out.");
        if (operation == "restore-full" && string.IsNullOrWhiteSpace(workspace))
            throw new ArgumentException("retention restore-full requires --workspace as the empty destination.");
        return new TaskServerCommandLine(TaskServerCommandKind.Retention, null, null, null, null, null, [],
            new RetentionCommandLine(operation, workspace, policy, archive, project, task, output, json, store, confirmColdDelete));
    }

    private static readonly HashSet<string> RecoveryOperations = new(StringComparer.Ordinal)
        { "help", "capture", "copy", "verify", "restore", "fence-hosts", "reenrol", "resume" };
    private static readonly HashSet<string> RecoveryValueOptions = new(StringComparer.Ordinal)
        { "--custody", "--backup", "--to", "--from", "--secret-bundle", "--loss-at", "--principal", "--credential-out", "--git-refs" };
    private static readonly HashSet<string> RecoveryFlags = new(StringComparer.Ordinal)
        { "--no-git", "--check-only", "--old-writer-closed", "--obligations-retained" };

    private static TaskServerCommandLine ParseRecovery(string[] args)
    {
        var operation = args.Length < 2 || args[1] is "--help" or "-h" ? "help" : args[1].ToLowerInvariant();
        if (!RecoveryOperations.Contains(operation))
            throw new ArgumentException($"Unknown recovery operation '{args[1]}'.");
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        var hostArguments = new List<string>();
        for (var index = 2; index < args.Length; index++)
        {
            var name = args[index].ToLowerInvariant();
            if (RecoveryFlags.Contains(name)) { flags.Add(name); continue; }
            if (RecoveryValueOptions.Contains(name))
            {
                if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"recovery {operation} {name} requires a value.");
                options[name] = args[++index];
                continue;
            }
            hostArguments.Add(args[index]);
        }
        string[] required = operation switch
        {
            "copy" => ["--backup", "--to"],
            "verify" or "restore" => ["--from"],
            "reenrol" => ["--principal", "--credential-out"],
            _ => [],
        };
        foreach (var name in required.Where(name => !options.ContainsKey(name)))
            throw new ArgumentException($"recovery {operation} requires {name}.");
        return new TaskServerCommandLine(TaskServerCommandKind.Recovery, null, null, null, null, null, hostArguments.ToArray(),
            Recovery: new RecoveryCommandLine(operation, options, flags));
    }

    private static bool IsCommand(string value)
        => value.Equals("backup", StringComparison.OrdinalIgnoreCase)
           || value.Equals("inventory", StringComparison.OrdinalIgnoreCase)
           || value.Equals("import", StringComparison.OrdinalIgnoreCase);

    private static bool ReadOption(string[] args, ref int index, string option, out string? value)
    {
        value = null;
        if (!string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase)) return false;
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"{args[0]} {option} requires a value.");
        value = args[++index];
        return true;
    }
}
