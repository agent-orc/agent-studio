using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

public enum TaskServerCommandKind
{
    Serve,
    Version,
    Backup,
    Inventory,
    Import,
}

public sealed record TaskServerCommandLine(
    TaskServerCommandKind Kind,
    string? BackupName,
    string? Source,
    string? InventoryPath,
    string? WorkspaceName,
    TaskServerMode? Mode,
    string[] HostArguments)
{
    public static TaskServerCommandLine Parse(string[] args)
    {
        if (args is ["--version"] or ["-V"])
            return new TaskServerCommandLine(TaskServerCommandKind.Version, null, null, null, null, null, []);
        if (args.Length == 0 || !IsCommand(args[0]))
            return new TaskServerCommandLine(TaskServerCommandKind.Serve, null, null, null, null, null, args);

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
                    throw new ArgumentException($"{args[0]} --mode must be one of: normal, draining, readOnly, maintenance.");
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
        if (mode is not null && kind != TaskServerCommandKind.Import)
            throw new ArgumentException("--mode is supported only by the import command.");
        if (kind == TaskServerCommandKind.Import && mode is not null && mode != TaskServerMode.Maintenance)
            throw new ArgumentException("import --mode supports only maintenance.");
        if (kind is TaskServerCommandKind.Inventory or TaskServerCommandKind.Import
            && string.IsNullOrWhiteSpace(source))
            throw new ArgumentException($"{args[0]} --source requires a value.");
        if (kind == TaskServerCommandKind.Import && string.IsNullOrWhiteSpace(inventory))
            throw new ArgumentException("import --inventory requires a value.");
        return new TaskServerCommandLine(
            kind,
            name,
            source,
            inventory,
            workspace,
            mode,
            hostArguments.ToArray());
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
