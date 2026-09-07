using AgentStudio.Retention;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.TaskServer;

public static class RetentionCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static async Task<int> RunAsync(RetentionCommandLine command, CancellationToken cancellationToken)
    {
        if (command.Operation == "help")
        {
            Console.WriteLine(TaskServerCommandLine.RetentionUsage);
            return 0;
        }

        try
        {
            var backup = new FileTreeFullBackupService();
            if (command.Operation == "verify-full")
            {
                var verified = await backup.VerifyAsync(command.OutputPath!, cancellationToken);
                Write(command.Json, verified, $"Verified full backup {command.OutputPath}: {verified.Files.Count} files, {verified.TotalBytes} bytes.");
                return 0;
            }
            if (command.Operation == "restore-full")
            {
                await backup.RestoreAsync(command.OutputPath!, command.Workspace!, cancellationToken);
                Write(command.Json, new { restored = true, source = command.OutputPath, destination = command.Workspace },
                    $"Restored full backup into {command.Workspace}.");
                return 0;
            }
            if (command.Operation == "backup-full")
            {
                var path = await backup.CreateAsync(command.Workspace!, command.OutputPath!, cancellationToken);
                var verified = await backup.VerifyAsync(path, cancellationToken);
                Write(command.Json, new { path, verified }, $"Created and verified full backup {path}: {verified.TotalBytes} bytes.");
                return 0;
            }

            var policy = await LoadPolicyAsync(command.Policy, cancellationToken);
            var store = new FileTreeRetentionStore(command.Workspace!, command.ArchivePath);
            if (command.Operation == "restore")
            {
                await store.RestoreAsync(command.Task!, cancellationToken);
                Write(command.Json, new { restored = command.Task, workspace = command.Workspace }, $"Restored {command.Task}.");
                return 0;
            }

            var before = Measure(store);
            var inventory = Filter(await store.EnumerateTasksAndFilesAsync(cancellationToken), command);
            var plan = new RetentionPlanner().Plan(inventory, policy, DateTimeOffset.UtcNow);
            if (command.Operation == "plan")
            {
                var report = BuildReport("plan", plan, before, before, null);
                var path = await WriteReportAsync(command.Workspace!, report, cancellationToken);
                Write(command.Json, new { reportPath = path, report }, HumanPlan(report, path));
                return 0;
            }

            RetentionRunResult result = null!;
            RepositoryWriteGate.Run(command.Workspace!, () =>
            {
                EnsureRuntimeIgnores(command.Workspace!);
                result = new RetentionExecutor(store).ApplyAsync(plan, policy, cancellationToken).GetAwaiter().GetResult();
                try
                {
                    CommitAppliedChanges(command.Workspace!, plan, result);
                }
                catch (Exception exception)
                {
                    result = result with { Errors = result.Errors.Append($"evidence-commit: {exception.Message}").ToList() };
                }
            });
            var after = Measure(store);
            var applyReport = BuildReport("apply", plan, before, after, result);
            var reportPath = await WriteReportAsync(command.Workspace!, applyReport, cancellationToken);
            await AppendAuditAsync(command.Workspace!, reportPath, applyReport, cancellationToken);
            Write(command.Json, new { reportPath, report = applyReport }, HumanPlan(applyReport, reportPath));
            return result.Errors.Count == 0 ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Retention command failed: {exception.Message}");
            return 1;
        }
    }

    private static IReadOnlyList<RetentionTaskInventory> Filter(
        IReadOnlyList<RetentionTaskInventory> inventory,
        RetentionCommandLine command)
        => inventory.Where(item =>
                (string.IsNullOrWhiteSpace(command.Project) || string.Equals(item.Project, command.Project, StringComparison.OrdinalIgnoreCase))
                && (string.IsNullOrWhiteSpace(command.Task) || string.Equals(item.TaskKey, command.Task, StringComparison.OrdinalIgnoreCase)))
            .ToList();

    private static async Task<RetentionPolicy> LoadPolicyAsync(string value, CancellationToken cancellationToken)
    {
        if (string.Equals(value, "default", StringComparison.OrdinalIgnoreCase))
            return RetentionPolicy.Default();
        var policy = JsonSerializer.Deserialize<RetentionPolicy>(await File.ReadAllTextAsync(value, cancellationToken), JsonOptions)
                     ?? throw new InvalidDataException($"Retention policy is invalid: {value}");
        policy.Validate();
        return policy;
    }

    private static RetentionCliReport BuildReport(
        string mode,
        RetentionPlan plan,
        RetentionWorkspaceMetrics before,
        RetentionWorkspaceMetrics after,
        RetentionRunResult? run)
    {
        var actionable = plan.Actions.Where(action => action.Kind != RetentionActionKind.RefuseOversize).ToList();
        return new RetentionCliReport(
            1, mode, DateTimeOffset.UtcNow, plan.PolicyVersion,
            actionable.Count, actionable.Sum(action => action.Bytes),
            plan.Actions.Count(action => action.Kind == RetentionActionKind.RefuseOversize),
            plan.Actions.GroupBy(action => action.RuleId).OrderBy(group => group.Key)
                .Select(group => new RetentionReportGroup(group.Key, group.Count(), group.Sum(action => action.Bytes))).ToList(),
            plan.Actions.GroupBy(action => action.Task.Project).OrderBy(group => group.Key)
                .Select(group => new RetentionReportGroup(group.Key, group.Count(), group.Sum(action => action.Bytes))).ToList(),
            plan.Actions.GroupBy(action => new { action.Task.Project, action.Task.TaskKey })
                .Select(group => new RetentionTopTask(group.Key.Project, group.Key.TaskKey, group.Count(), group.Sum(action => action.Bytes)))
                .OrderByDescending(item => item.Bytes).Take(20).ToList(),
            before, after, run?.AppliedActions ?? 0, run?.AppliedBytes ?? 0, run?.Errors ?? []);
    }

    private static RetentionWorkspaceMetrics Measure(FileTreeRetentionStore store)
    {
        var taskFiles = Directory.Exists(Path.Combine(store.WorkspacePath, "projects"))
            ? Directory.EnumerateFiles(Path.Combine(store.WorkspacePath, "projects"), "*", SearchOption.AllDirectories).ToList()
            : [];
        var workspaceFiles = Directory.EnumerateFiles(store.WorkspacePath, "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)).ToList();
        var coldFiles = Directory.Exists(store.ArchivePath)
            ? Directory.EnumerateFiles(store.ArchivePath, "*", SearchOption.AllDirectories).ToList() : [];
        var gitPath = Path.Combine(store.WorkspacePath, ".git");
        return new RetentionWorkspaceMetrics(
            taskFiles.Count(path => string.Equals(Path.GetFileName(path), "task.json", StringComparison.OrdinalIgnoreCase)),
            taskFiles.Sum(path => new FileInfo(path).Length),
            coldFiles.Sum(path => new FileInfo(path).Length),
            workspaceFiles.Sum(path => new FileInfo(path).Length),
            Directory.Exists(gitPath) ? Directory.EnumerateFiles(gitPath, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length) : 0);
    }

    private static void EnsureRuntimeIgnores(string workspace)
    {
        var ignorePath = Path.Combine(workspace, ".gitignore");
        var existing = File.Exists(ignorePath) ? File.ReadAllText(ignorePath) : string.Empty;
        var additions = new[] { "/logs/bus/", "/.metadata/attempt-authority*", "/.runtime/" }
            .Where(pattern => !existing.Split('\n').Any(line => string.Equals(line.Trim(), pattern, StringComparison.Ordinal)))
            .ToList();
        if (additions.Count > 0)
            File.AppendAllText(ignorePath, (existing.Length > 0 && !existing.EndsWith('\n') ? Environment.NewLine : string.Empty)
                + "# Runtime retention data is never committed." + Environment.NewLine
                + string.Join(Environment.NewLine, additions) + Environment.NewLine);
        if (Directory.Exists(Path.Combine(workspace, ".git")))
            _ = RetentionGitCommand.Run(workspace, ["rm", "-r", "--cached", "--ignore-unmatch", "--", "logs/bus"]);
    }

    private static void CommitAppliedChanges(string workspace, RetentionPlan plan, RetentionRunResult run)
    {
        if (!Directory.Exists(Path.Combine(workspace, ".git"))) return;
        var archived = plan.Actions.Where(action => action.Kind is RetentionActionKind.ArchiveHeavy or RetentionActionKind.ArchiveTask)
            .GroupBy(action => action.Task.Project, StringComparer.OrdinalIgnoreCase);
        foreach (var project in archived)
        {
            var path = $"projects/{project.Key}";
            if (RetentionGitCommand.Run(workspace, ["add", "-A", "--", path]).Code != 0) continue;
            if (RetentionGitCommand.Run(workspace, ["diff", "--cached", "--quiet", "--", path]).Code == 0) continue;
            var count = project.Select(action => action.Task.TaskKey).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var bytes = project.Sum(action => action.Bytes);
            var commit = RetentionGitCommand.Run(workspace, ["-c", "user.name=agent-orchestrator", "-c", "user.email=agent-orchestrator@local",
                "commit", "-m", $"retention: archived {count} tasks, {bytes} bytes", "--", path]);
            if (commit.Code != 0) throw new InvalidOperationException($"Retention evidence commit failed for {project.Key}: {commit.Error}");
        }
        var runtimePaths = new[] { ".gitignore", "logs/bus", ".metadata/attempt-authority.archive-*.json" };
        _ = RetentionGitCommand.Run(workspace, ["add", "-A", "--", .. runtimePaths]);
        if (RetentionGitCommand.Run(workspace, ["diff", "--cached", "--quiet", "--", .. runtimePaths]).Code == 1)
        {
            var commit = RetentionGitCommand.Run(workspace, ["-c", "user.name=agent-orchestrator", "-c", "user.email=agent-orchestrator@local",
                "commit", "-m", "retention: rotate runtime artifacts", "--", .. runtimePaths]);
            if (commit.Code != 0) throw new InvalidOperationException($"Runtime retention commit failed: {commit.Error}");
        }
    }

    private static async Task<string> WriteReportAsync(string workspace, RetentionCliReport report, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(workspace, ".metadata", "retention-runs");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{report.CreatedAt:yyyyMMddTHHmmssfffZ}-{report.Mode}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, JsonOptions) + Environment.NewLine, cancellationToken);
        return path;
    }

    private static Task AppendAuditAsync(string workspace, string reportPath, RetentionCliReport report, CancellationToken cancellationToken)
    {
        var path = Path.Combine(workspace, ".metadata", "retention-audit.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var line = JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow, actor = "retention-cli", mode = report.Mode,
            reportPath, report.AppliedActions, report.AppliedBytes, errors = report.Errors.Count }, JsonOptions) + Environment.NewLine;
        return File.AppendAllTextAsync(path, line, cancellationToken);
    }

    private static string HumanPlan(RetentionCliReport report, string path)
        => $"Retention {report.Mode}: {report.ActionCount} actions, {report.PlannedBytes} bytes, "
           + $"{report.RefusedOversizeFiles} oversized files refused. Report: {path}";

    private static void Write(bool json, object value, string human)
        => Console.WriteLine(json ? JsonSerializer.Serialize(value, JsonOptions) : human);

}

public sealed record RetentionCliReport(
    int SchemaVersion,
    string Mode,
    DateTimeOffset CreatedAt,
    int PolicyVersion,
    int ActionCount,
    long PlannedBytes,
    int RefusedOversizeFiles,
    IReadOnlyList<RetentionReportGroup> ByRule,
    IReadOnlyList<RetentionReportGroup> ByProject,
    IReadOnlyList<RetentionTopTask> TopTasks,
    RetentionWorkspaceMetrics Before,
    RetentionWorkspaceMetrics After,
    int AppliedActions,
    long AppliedBytes,
    IReadOnlyList<string> Errors);

public sealed record RetentionReportGroup(string Name, int Count, long Bytes);
public sealed record RetentionTopTask(string Project, string TaskKey, int Actions, long Bytes);
public sealed record RetentionWorkspaceMetrics(int Tasks, long HotTaskBytes, long ColdBytes, long WorkspaceBytes, long GitDirectoryBytes);
