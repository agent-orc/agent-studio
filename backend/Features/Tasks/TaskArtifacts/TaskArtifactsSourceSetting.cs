namespace AgentStudio.Tasks;

/// <summary>
/// Where task-folder artefacts live (pipeline delivery target, section 05
/// "Task-folder artefacts" and "Release card attribution", migration M3 and
/// M6, decision D9 A, AGT-3320).
/// </summary>
public enum TaskArtifactsSourceMode
{
    /// <summary>Task folders in the workspace repository; transitions and run boundaries commit and push it.</summary>
    Workspace,
    /// <summary>As <see cref="Workspace"/>, and task files are mirrored into the Task Server off the request path.</summary>
    TaskServerShadow,
    /// <summary>
    /// The Task Server holds the artefacts and answers release card attribution;
    /// the workspace repository is an export written by the Task Server's export
    /// job, so the monolith neither commits nor pushes it. Task files are still
    /// mirrored until the write cutover.
    /// </summary>
    TaskServer,
}

public sealed record TaskArtifactsSourceResolution(TaskArtifactsSourceMode Mode, string Configured, string? Rejected);

/// <summary>
/// Pure resolution of <c>TaskArtifacts:Source</c>. Rollback is this setting, so
/// a value it cannot read falls back to the workspace instead of failing.
/// </summary>
public static class TaskArtifactsSourceSetting
{
    public const string SectionKey = "TaskArtifacts:Source";
    public const string Workspace = "workspace";
    public const string TaskServerShadow = "task-server-shadow";
    public const string TaskServer = "task-server";

    public static TaskArtifactsSourceResolution Resolve(string? configured)
    {
        var value = configured?.Trim() ?? "";
        if (value.Length == 0 || string.Equals(value, Workspace, StringComparison.OrdinalIgnoreCase))
            return new TaskArtifactsSourceResolution(TaskArtifactsSourceMode.Workspace, Workspace, null);
        if (string.Equals(value, TaskServerShadow, StringComparison.OrdinalIgnoreCase))
            return new TaskArtifactsSourceResolution(TaskArtifactsSourceMode.TaskServerShadow, TaskServerShadow, null);
        if (string.Equals(value, TaskServer, StringComparison.OrdinalIgnoreCase))
            return new TaskArtifactsSourceResolution(TaskArtifactsSourceMode.TaskServer, TaskServer, null);
        return new TaskArtifactsSourceResolution(TaskArtifactsSourceMode.Workspace, Workspace, value);
    }

    /// <summary>True when the workspace repository is an export the monolith must not commit or push.</summary>
    public static bool WorkspaceRepositoryIsExport(IConfiguration configuration)
        => Resolve(configuration[SectionKey]).Mode == TaskArtifactsSourceMode.TaskServer;

    /// <summary>
    /// True when <paramref name="path"/> lies in the configured
    /// <c>TaskRepository</c> and that repository is an export. Decided from the
    /// paths alone, so the guard itself never spawns git.
    /// </summary>
    public static bool IsExportedWorkspacePath(IConfiguration configuration, string? path)
    {
        if (!WorkspaceRepositoryIsExport(configuration)) return false;
        var repository = configuration["TaskRepository"];
        var candidate = string.IsNullOrWhiteSpace(path) ? repository : path;
        if (string.IsNullOrWhiteSpace(repository) || string.IsNullOrWhiteSpace(candidate)) return false;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repository));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
               || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || full.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Skip reason of a workspace repository commit or push while the repository is an export.</summary>
    public const string ExportSkipReason = "workspace-repository-export";
}

/// <summary>Reads <c>TaskArtifacts:Source</c> per call, so a configuration reload switches or rolls back without a restart.</summary>
public sealed class TaskArtifactsSourceSettings(IConfiguration configuration)
{
    public TaskArtifactsSourceResolution Current => TaskArtifactsSourceSetting.Resolve(configuration[TaskArtifactsSourceSetting.SectionKey]);

    public TaskArtifactsSourceMode Mode => Current.Mode;

    public bool MirrorsToTaskServer => Mode is TaskArtifactsSourceMode.TaskServerShadow or TaskArtifactsSourceMode.TaskServer;

    public bool AttributesFromTaskServer => Mode == TaskArtifactsSourceMode.TaskServer;
}
