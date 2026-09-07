namespace AgentStudio.TaskServer;

public enum TaskServerCommandKind
{
    Serve,
    Version,
    Backup,
    Retention,
}

public sealed record RetentionCommandLine(
    string Operation,
    string? Workspace,
    string Policy,
    string? ArchivePath,
    string? Project,
    string? Task,
    string? OutputPath,
    bool Json);

public sealed record TaskServerCommandLine(
    TaskServerCommandKind Kind,
    string? BackupName,
    string[] HostArguments,
    RetentionCommandLine? Retention = null)
{
    public const string RetentionUsage = """
        Usage:
          task-server retention plan --workspace <path> [--policy <file|default>] [--archive <path>] [--project <project>] [--task <key>] [--json]
          task-server retention apply --workspace <path> [--policy <file|default>] [--archive <path>] [--project <project>] [--task <key>] [--json]
          task-server retention restore --workspace <path> --task <key> [--archive <path>] [--json]
          task-server retention re-excerpt --workspace <path> [--archive <path>] [--task <key>] [--json]
          task-server retention backup-full --workspace <path> --out <backup-root> [--json]
          task-server retention verify-full --out <backup-directory> [--json]
          task-server retention restore-full --workspace <empty-path> --out <backup-directory> [--json]
        """;

    public static TaskServerCommandLine Parse(string[] args)
    {
        if (args is ["--version"] or ["-V"])
            return new TaskServerCommandLine(TaskServerCommandKind.Version, null, []);
        if (args.Length > 0 && string.Equals(args[0], "retention", StringComparison.OrdinalIgnoreCase))
            return ParseRetention(args);
        if (args.Length == 0 || !string.Equals(args[0], "backup", StringComparison.OrdinalIgnoreCase))
            return new TaskServerCommandLine(TaskServerCommandKind.Serve, null, args);

        string? name = null;
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
            hostArguments.Add(args[index]);
        }
        return new TaskServerCommandLine(
            TaskServerCommandKind.Backup,
            name,
            hostArguments.ToArray());
    }

    private static TaskServerCommandLine ParseRetention(string[] args)
    {
        if (args.Length < 2 || args[1] is "--help" or "-h" || string.Equals(args[1], "help", StringComparison.OrdinalIgnoreCase))
            return new TaskServerCommandLine(TaskServerCommandKind.Retention, null, [],
                new RetentionCommandLine("help", null, "default", null, null, null, null, false));
        var operation = args[1].ToLowerInvariant();
        if (operation is not ("plan" or "apply" or "restore" or "re-excerpt" or "backup-full" or "verify-full" or "restore-full"))
            throw new ArgumentException($"Unknown retention operation '{args[1]}'.");
        string? workspace = null, archive = null, project = null, task = null, output = null;
        var policy = "default";
        var json = false;
        for (var index = 2; index < args.Length; index++)
        {
            if (string.Equals(args[index], "--json", StringComparison.OrdinalIgnoreCase)) { json = true; continue; }
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
                default: throw new ArgumentException($"Unknown retention option '{args[index - 1]}'.");
            }
        }
        if (operation is ("plan" or "apply" or "restore" or "re-excerpt" or "backup-full") && string.IsNullOrWhiteSpace(workspace))
            throw new ArgumentException($"retention {operation} requires --workspace.");
        if (operation == "restore" && string.IsNullOrWhiteSpace(task))
            throw new ArgumentException("retention restore requires --task.");
        if (operation is ("backup-full" or "verify-full" or "restore-full") && string.IsNullOrWhiteSpace(output))
            throw new ArgumentException($"retention {operation} requires --out.");
        if (operation == "restore-full" && string.IsNullOrWhiteSpace(workspace))
            throw new ArgumentException("retention restore-full requires --workspace as the empty destination.");
        return new TaskServerCommandLine(TaskServerCommandKind.Retention, null, [],
            new RetentionCommandLine(operation, workspace, policy, archive, project, task, output, json));
    }
}
