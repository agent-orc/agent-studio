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
            if (command.Operation == "re-excerpt")
                return await ReExcerptAsync(command, store, cancellationToken);
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
                    var warnings = CommitAppliedChanges(command.Workspace!, plan, result);
                    result = result with { Warnings = [.. result.Warnings, .. warnings] };
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

    /// <summary>
    /// Rebuilds hot excerpts from the cold payloads and stages them, so a corrected excerpt writer can be
    /// applied to tasks whose originals already left the hot tree.
    /// </summary>
    private static async Task<int> ReExcerptAsync(
        RetentionCommandLine command,
        FileTreeRetentionStore store,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ReExcerptResult> results = [];
        var staged = 0;
        var warnings = new List<string>();
        RepositoryWriteGate.Run(command.Workspace!, () =>
        {
            results = store.ReExcerptAsync(command.Task, cancellationToken).GetAwaiter().GetResult();
            var paths = results.Where(item => item.Succeeded).Select(item => item.ExcerptPath!).ToList();
            foreach (var chunk in paths.Chunk(200))
            {
                var add = RetentionGitCommand.Run(command.Workspace!, ["add", "--", .. chunk]);
                if (add.Code == 0) staged += chunk.Length;
                else warnings.Add($"re-excerpt: could not stage {chunk.Length} excerpts: {add.Error.Trim()}");
            }
        });

        var rebuilt = results.Where(item => item.Succeeded).ToList();
        var failed = results.Where(item => !item.Succeeded).ToList();
        var summary = new
        {
            rebuilt = rebuilt.Count,
            failed = failed.Count,
            staged,
            previousBytes = rebuilt.Sum(item => item.PreviousBytes),
            bytes = rebuilt.Sum(item => item.Bytes),
            largestBytes = rebuilt.Count == 0 ? 0 : rebuilt.Max(item => item.Bytes),
            warnings,
            errors = failed.Select(item => $"{item.Project}/{item.TaskKey}: {item.Error}").ToList(),
            results = rebuilt,
        };
        Write(command.Json, summary,
            $"Re-excerpt: {rebuilt.Count} excerpts rebuilt ({summary.previousBytes} -> {summary.bytes} bytes), "
            + $"{staged} staged, {failed.Count} failed.");
        return failed.Count == 0 ? 0 : 1;
    }

    /// <summary>The workspace-wide runtime pseudo task belongs to no project; a scoped run must not report it.</summary>
    public const string RuntimePseudoProject = "_workspace";

    private static IReadOnlyList<RetentionTaskInventory> Filter(
        IReadOnlyList<RetentionTaskInventory> inventory,
        RetentionCommandLine command)
    {
        var scoped = !string.IsNullOrWhiteSpace(command.Project) || !string.IsNullOrWhiteSpace(command.Task);
        return inventory.Where(item =>
                (!scoped || !string.Equals(item.Project, RuntimePseudoProject, StringComparison.OrdinalIgnoreCase))
                && (string.IsNullOrWhiteSpace(command.Project) || string.Equals(item.Project, command.Project, StringComparison.OrdinalIgnoreCase))
                && (string.IsNullOrWhiteSpace(command.Task) || string.Equals(item.TaskKey, command.Task, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

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
            before, after, run?.AppliedActions ?? 0, run?.AppliedBytes ?? 0, run?.Errors ?? [])
        {
            Warnings = run?.Warnings ?? [],
        };
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

        // Excerpts are what retention adds, not what it leaves behind: reporting them inside hotTaskBytes
        // made an archive run look like growth. They are counted on their own line instead.
        var excerpts = taskFiles.Where(IsExcerptPath).ToList();
        return new RetentionWorkspaceMetrics(
            taskFiles.Count(path => string.Equals(Path.GetFileName(path), "task.json", StringComparison.OrdinalIgnoreCase)),
            taskFiles.Where(path => !IsExcerptPath(path)).Sum(path => new FileInfo(path).Length),
            excerpts.Sum(path => new FileInfo(path).Length),
            coldFiles.Sum(path => new FileInfo(path).Length),
            workspaceFiles.Sum(path => new FileInfo(path).Length),
            Directory.Exists(gitPath) ? Directory.EnumerateFiles(gitPath, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length) : 0);
    }

    private static bool IsExcerptPath(string path)
        => Path.GetFileName(path).StartsWith("retention-excerpt", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>
    /// Commits the archive evidence per project, then the runtime rotation. Runtime failures are warnings:
    /// the archive commit is the valuable part and must never be lost because a rotation path was untracked.
    /// </summary>
    private static IReadOnlyList<string> CommitAppliedChanges(string workspace, RetentionPlan plan, RetentionRunResult run)
    {
        if (!Directory.Exists(Path.Combine(workspace, ".git"))) return [];
        var warnings = new List<string>();
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

        try
        {
            warnings.AddRange(CommitRuntimeRotation(workspace));
        }
        catch (Exception exception)
        {
            warnings.Add($"runtime-commit: {exception.Message}");
        }
        return warnings;
    }

    /// <summary>
    /// Stages the runtime rotation by tracked path. Git pathspecs are literal, so a glob like
    /// <c>attempt-authority.archive-*.json</c> aborts the commit; ignored and untracked paths carry
    /// nothing to commit at all. Both are resolved through <c>git ls-files</c> instead.
    /// </summary>
    private static IReadOnlyList<string> CommitRuntimeRotation(string workspace)
    {
        var warnings = new List<string>();

        var ignoreAdd = RetentionGitCommand.Run(workspace, ["add", "--", ".gitignore"]);
        if (ignoreAdd.Code != 0)
            warnings.Add($"runtime-commit: could not stage .gitignore: {ignoreAdd.Error.Trim()}");

        // Only tracked files can carry a deletion into a commit; untracked and ignored paths are skipped.
        var tracked = TrackedFiles(workspace).Where(IsRotatableRuntimePath).ToList();
        foreach (var chunk in tracked.Chunk(200))
        {
            var add = RetentionGitCommand.Run(workspace, ["add", "-A", "--", .. chunk]);
            if (add.Code != 0)
                warnings.Add($"runtime-commit: could not stage {chunk.Length} runtime paths: {add.Error.Trim()}");
        }

        // Read the staged set back rather than re-deriving pathspecs: EnsureRuntimeIgnores already staged
        // the logs/bus index removal, so those paths are no longer tracked but still need committing.
        var staged = StagedFiles(workspace)
            .Where(path => IsRotatableRuntimePath(path) || string.Equals(path, ".gitignore", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (staged.Count == 0) return warnings;

        var commit = RetentionGitCommand.Run(workspace, ["-c", "user.name=agent-orchestrator", "-c", "user.email=agent-orchestrator@local",
            "commit", "-m", $"retention: rotate runtime artifacts ({staged.Count} paths)", "--", .. staged]);
        if (commit.Code != 0)
            warnings.Add($"runtime-commit: rotation commit failed: {commit.Error.Trim()}");
        return warnings;
    }

    private static bool IsRotatableRuntimePath(string path)
        => path.StartsWith("logs/bus/", StringComparison.OrdinalIgnoreCase)
           || path.StartsWith(".metadata/attempt-authority", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> TrackedFiles(string workspace)
        => NulSeparated(RetentionGitCommand.Run(workspace, ["ls-files", "-z"]));

    private static IReadOnlyList<string> StagedFiles(string workspace)
        => NulSeparated(RetentionGitCommand.Run(workspace, ["diff", "--cached", "--name-only", "-z"]));

    private static IReadOnlyList<string> NulSeparated(RetentionGitResult result)
        => result.Code != 0
            ? []
            : result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Select(path => path.Trim()).Where(path => path.Length > 0).ToList();

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
    IReadOnlyList<string> Errors)
{
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record RetentionReportGroup(string Name, int Count, long Bytes);
public sealed record RetentionTopTask(string Project, string TaskKey, int Actions, long Bytes);

public sealed record RetentionWorkspaceMetrics(
    int Tasks,
    long HotTaskBytes,
    long ExcerptBytes,
    long ColdBytes,
    long WorkspaceBytes,
    long GitDirectoryBytes);
