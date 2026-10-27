namespace AgentStudio.Tasks;

/// <summary>Where the board read answers from (pipeline delivery target, M1 and M2, decision D7 A-B).</summary>
public enum BoardSourceMode
{
    /// <summary>The workspace task index answers; nothing reaches the Task Server.</summary>
    Workspace,
    /// <summary>The workspace answers; the Task Server is mirrored and compared off the request path.</summary>
    TaskServerShadow,
    /// <summary>The Task Server answers lanes and card core; writes still reach both stores.</summary>
    TaskServer,
}

public sealed record BoardSourceResolution(BoardSourceMode Mode, string Configured, string? Rejected);

/// <summary>
/// Pure resolution of <c>Board:Source</c>. Rollback is this setting, so a
/// value it cannot read falls back to the workspace instead of failing the
/// board; the rejected value is reported on the metrics card.
/// </summary>
public static class BoardSourceSetting
{
    public const string SectionKey = "Board:Source";
    public const string Workspace = "workspace";
    public const string TaskServerShadow = "task-server-shadow";
    public const string TaskServer = "task-server";

    public static BoardSourceResolution Resolve(string? configured)
    {
        var value = configured?.Trim() ?? "";
        if (value.Length == 0 || string.Equals(value, Workspace, StringComparison.OrdinalIgnoreCase))
            return new BoardSourceResolution(BoardSourceMode.Workspace, Workspace, null);
        if (string.Equals(value, TaskServerShadow, StringComparison.OrdinalIgnoreCase))
            return new BoardSourceResolution(BoardSourceMode.TaskServerShadow, TaskServerShadow, null);
        if (string.Equals(value, TaskServer, StringComparison.OrdinalIgnoreCase))
            return new BoardSourceResolution(BoardSourceMode.TaskServer, TaskServer, null);
        return new BoardSourceResolution(BoardSourceMode.Workspace, Workspace, value);
    }

    public static string Format(BoardSourceMode mode) => mode switch
    {
        BoardSourceMode.TaskServerShadow => TaskServerShadow,
        BoardSourceMode.TaskServer => TaskServer,
        _ => Workspace,
    };
}

/// <summary>Reads <c>Board:Source</c> per call, so a configuration reload switches or rolls back without a restart.</summary>
public sealed class BoardSourceSettings(IConfiguration configuration)
{
    public BoardSourceResolution Current => BoardSourceSetting.Resolve(configuration[BoardSourceSetting.SectionKey]);

    public BoardSourceMode Mode => Current.Mode;

    /// <summary>The mirror runs whenever the Task Server takes part, so writes reach both stores.</summary>
    public bool MirrorsToTaskServer => Mode is BoardSourceMode.TaskServerShadow or BoardSourceMode.TaskServer;
}
