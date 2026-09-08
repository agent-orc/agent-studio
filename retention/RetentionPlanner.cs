namespace AgentStudio.Retention;

public sealed class RetentionPlanner
{
    public RetentionPlan Plan(
        IReadOnlyList<RetentionTaskInventory> inventory,
        RetentionPolicy policy,
        DateTimeOffset now)
    {
        policy.Validate();
        var actions = new List<RetentionAction>();
        foreach (var task in inventory.OrderBy(item => item.Project, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.TaskKey, StringComparer.OrdinalIgnoreCase))
        {
            PlanRuntime(task, policy, now, actions);
            PlanOversize(task, policy, actions);

            var heavyRule = policy.RuleFor(task.Project, ArtifactClass.HeavyWorkingData);
            if (heavyRule.NeverArchiveLanes.Contains(task.Lane) || task.TerminalAt is null)
                continue;

            var age = now - task.TerminalAt.Value;
            var archived = task.Files.Where(file => file.IsArchived).ToList();
            if (heavyRule.DeleteArchiveEnabled
                && heavyRule.DeleteArchiveAfterDaysTerminal is int deleteAfterDays
                && age >= TimeSpan.FromDays(deleteAfterDays))
            {
                Add(actions, RetentionActionKind.DeleteCold, heavyRule.Id, task, archived, 3,
                    $"terminal for at least {deleteAfterDays} days; explicit cold-delete confirmation required");
                if (archived.Count > 0)
                    continue;
            }

            var heavy = task.Files.Where(file => !file.IsArchived
                && file.Classification.ArtifactClass == ArtifactClass.HeavyWorkingData).ToList();
            var taskDays = heavyRule.ArchiveTaskAfterDaysTerminal;
            if (taskDays.HasValue && age >= TimeSpan.FromDays(taskDays.Value))
            {
                var stageTwo = SelectWholeTaskArchiveFiles(task.Files);
                if (stageTwo.Count > 0 || archived.Count > 0)
                    actions.Add(new RetentionAction(
                        RetentionActionKind.ArchiveTask,
                        heavyRule.Id,
                        task,
                        stageTwo,
                        stageTwo.Sum(file => file.Size),
                        2,
                        $"terminal for at least {taskDays.Value} days"));
                continue;
            }

            var archiveDays = heavyRule.ArchiveAfterDaysTerminal;
            if (archiveDays.HasValue && age >= TimeSpan.FromDays(archiveDays.Value))
            {
                Add(actions, RetentionActionKind.ArchiveHeavy, heavyRule.Id, task, heavy, 1,
                    $"terminal for at least {archiveDays.Value} days");
                continue;
            }

            if (heavyRule.HotBudgetBytesPerTask <= 0)
                continue;
            var total = heavy.Sum(file => file.Size);
            if (total <= heavyRule.HotBudgetBytesPerTask)
                continue;
            var overflow = total - heavyRule.HotBudgetBytesPerTask;
            var selected = new List<RetentionFile>();
            long selectedBytes = 0;
            foreach (var file in heavy.OrderBy(file => file.LastWriteAt).ThenBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                selected.Add(file);
                selectedBytes += file.Size;
                if (selectedBytes >= overflow)
                    break;
            }
            Add(actions, RetentionActionKind.ArchiveHeavy, heavyRule.Id, task, selected, 1,
                $"hot budget exceeded by {overflow} bytes; oldest heavy files first");
        }

        return new RetentionPlan(now, policy.Version, actions);
    }

    public static List<RetentionFile> SelectWholeTaskArchiveFiles(IEnumerable<RetentionFile> files)
        => files.Where(file => !file.IsArchived
                && file.Classification.ArtifactClass is ArtifactClass.Evidence or ArtifactClass.HeavyWorkingData
                && !string.Equals(file.RelativePath, "status.md", StringComparison.OrdinalIgnoreCase)
                && !file.RelativePath.EndsWith("/status.md", StringComparison.OrdinalIgnoreCase)
                && !file.RelativePath.Contains("retention-excerpt", StringComparison.OrdinalIgnoreCase)
                && !file.RelativePath.EndsWith("/archive-manifest.json", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(file.RelativePath, "archive-manifest.json", StringComparison.OrdinalIgnoreCase))
            .ToList();

    private static void PlanRuntime(
        RetentionTaskInventory task,
        RetentionPolicy policy,
        DateTimeOffset now,
        ICollection<RetentionAction> actions)
    {
        var rule = policy.RuleFor(task.Project, ArtifactClass.Runtime);
        var selected = task.Files.Where(file =>
            file.Classification.ArtifactClass == ArtifactClass.Runtime
            && file.Classification.Family != "attempt-authority-live"
            && rule.DeleteAfterDays.HasValue
            && now - file.LastWriteAt >= TimeSpan.FromDays(
                file.Classification.Family == "attempt-authority-archive" ? Math.Max(90, rule.DeleteAfterDays.Value) : rule.DeleteAfterDays.Value))
            .ToList();
        Add(actions, RetentionActionKind.DeleteRuntime, rule.Id, task, selected, 0,
            $"runtime data older than {rule.DeleteAfterDays} days");
    }

    private static void PlanOversize(
        RetentionTaskInventory task,
        RetentionPolicy policy,
        ICollection<RetentionAction> actions)
    {
        var rule = policy.RuleFor(task.Project, ArtifactClass.HeavyWorkingData);
        if (rule.RefuseAboveBytes <= 0)
            return;
        var selected = task.Files.Where(file =>
            !file.IsArchived
            && file.Classification.ArtifactClass == ArtifactClass.HeavyWorkingData
            && file.Size > rule.RefuseAboveBytes).ToList();
        Add(actions, RetentionActionKind.RefuseOversize, rule.Id, task, selected, 0,
            $"single file exceeds {rule.RefuseAboveBytes} bytes");
    }

    private static void Add(
        ICollection<RetentionAction> actions,
        RetentionActionKind kind,
        string ruleId,
        RetentionTaskInventory task,
        IReadOnlyList<RetentionFile> files,
        int stage,
        string reason)
    {
        if (files.Count == 0)
            return;
        actions.Add(new RetentionAction(kind, ruleId, task, files, files.Sum(file => file.Size), stage, reason));
    }
}

public sealed record RetentionRunResult(
    RetentionPlan Plan,
    int AppliedActions,
    long AppliedBytes,
    IReadOnlyList<string> Errors)
{
    /// <summary>Non-fatal findings. A run that only produced warnings still succeeds.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed class RetentionExecutor(IRetentionStore store)
{
    public async Task<RetentionRunResult> ApplyAsync(
        RetentionPlan plan,
        RetentionPolicy policy,
        CancellationToken cancellationToken = default,
        bool confirmColdDelete = false)
    {
        var applied = 0;
        long bytes = 0;
        var errors = new List<string>();
        var warnings = new List<string>();
        foreach (var action in plan.Actions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                switch (action.Kind)
                {
                    case RetentionActionKind.RefuseOversize:
                        continue;
                    case RetentionActionKind.DeleteCold:
                        if (!confirmColdDelete)
                        {
                            warnings.Add($"{action.Task.Project}/{action.Task.TaskKey}: cold payload deletion requires explicit confirmation.");
                            continue;
                        }
                        await store.DeleteColdAsync(action, cancellationToken);
                        break;
                    case RetentionActionKind.DeleteRuntime:
                        await store.DeleteRuntimeAsync(action, cancellationToken);
                        break;
                    case RetentionActionKind.ArchiveHeavy:
                    case RetentionActionKind.ArchiveTask:
                        if (await store.MoveToColdAsync(action, policy, cancellationToken) is null)
                            continue;
                        break;
                }
                applied++;
                bytes += action.Bytes;
            }
            catch (Exception exception)
            {
                errors.Add($"{action.Task.Project}/{action.Task.TaskKey}/{action.Kind}: {exception.Message}");
            }
        }
        return new RetentionRunResult(plan, applied, bytes, errors) { Warnings = warnings };
    }
}
