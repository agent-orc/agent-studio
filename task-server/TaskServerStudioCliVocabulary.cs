namespace AgentStudio.TaskServer;

/// <summary>
/// CLI type/permission/context vocabulary for the P3 administration bundle
/// (<c>cli-modes</c>, <c>cli-context-modes</c>, <c>quota/model-routes</c>).
/// Owned locally rather than referencing the shared coding-CLI driver package
/// the backend and the standalone Runner use for the same ids:
/// <c>TaskServer.csproj</c> may reference only <c>TaskServer.Contracts</c>
/// and the host-neutral retention assembly (see the architecture-boundary
/// test in TaskServer.Tests), so the Task Server cannot take a process/runner
/// package dependency just to read three id strings. The values below are
/// the platform's stable, published CLI vocabulary (mirrors the driver
/// package's own CLI-type / permission-mode / context-mode / permission-flag
/// tables as of its 0.7.0 release) rather than a live probe or a
/// process-spawn concern, so a local, intentionally duplicated copy is safe.
/// </summary>
internal static class StudioCliTypes
{
    public const string Claude = "claude";
    public const string Codex = "codex";
    public const string Gemini = "gemini";

    public static readonly string[] All = [Claude, Codex, Gemini];
}

internal static class StudioCliPermissionModes
{
    public const string Yolo = "yolo";
    public const string WorkspaceWrite = "workspace-write";
    public const string ReadOnly = "read-only";
    public const string Custom = "custom";

    public static readonly string[] UserVisible = [Yolo, WorkspaceWrite, ReadOnly, Custom];
}

internal static class StudioCliContextModes
{
    public const string Clean = "clean";
    public const string Shared = "shared";

    public static readonly string[] UserVisible = [Clean, Shared];

    /// <summary>Claude and Codex redirect their CLI home to a per-run temp directory; Gemini exposes no such redirect.</summary>
    public static bool SupportsClean(string cliType) =>
        string.Equals(cliType, StudioCliTypes.Claude, StringComparison.OrdinalIgnoreCase)
        || string.Equals(cliType, StudioCliTypes.Codex, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Pure (cliType, mode) to command-line-flags lookup table. No process/runner dependency.</summary>
internal static class StudioCliPermissionFlags
{
    private static readonly IReadOnlyDictionary<(string Cli, string Mode), string[]> Table =
        new Dictionary<(string, string), string[]>
        {
            [(StudioCliTypes.Claude, StudioCliPermissionModes.Yolo)] = ["--dangerously-skip-permissions"],
            [(StudioCliTypes.Claude, StudioCliPermissionModes.WorkspaceWrite)] = ["--permission-mode", "acceptEdits"],
            [(StudioCliTypes.Claude, StudioCliPermissionModes.ReadOnly)] = ["--permission-mode", "plan"],
            [(StudioCliTypes.Claude, StudioCliPermissionModes.Custom)] = [],
            [(StudioCliTypes.Codex, StudioCliPermissionModes.Yolo)] = ["--sandbox", "danger-full-access"],
            [(StudioCliTypes.Codex, StudioCliPermissionModes.WorkspaceWrite)] = ["--sandbox", "workspace-write"],
            [(StudioCliTypes.Codex, StudioCliPermissionModes.ReadOnly)] = ["--sandbox", "read-only"],
            [(StudioCliTypes.Codex, StudioCliPermissionModes.Custom)] = [],
            [(StudioCliTypes.Gemini, StudioCliPermissionModes.Yolo)] = ["--skip-trust", "-y"],
            [(StudioCliTypes.Gemini, StudioCliPermissionModes.WorkspaceWrite)] = ["--skip-trust", "--approval-mode", "auto_edit"],
            [(StudioCliTypes.Gemini, StudioCliPermissionModes.ReadOnly)] = ["--skip-trust", "--approval-mode", "default"],
            [(StudioCliTypes.Gemini, StudioCliPermissionModes.Custom)] = ["--skip-trust"],
        };

    public static IReadOnlyList<string> For(string cliType, string mode) =>
        Table.TryGetValue((cliType, mode), out var flags) ? flags : [];
}
