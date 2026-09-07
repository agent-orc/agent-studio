namespace AgentStudio.Cli;

/// <summary>
/// Neutral environment helper shared by the PTY-based quota probes. It used to
/// also manage the GitHub Copilot CLI's <c>~/.copilot</c> config; that CLI has
/// been removed from the product, so the trust / terminal-setup hooks are now
/// no-ops kept only so the probe base class has a single seam to call. Each
/// surviving CLI (Claude / Codex) drives its own trust prompt over the PTY, so
/// nothing here needs to write to disk anymore.
/// </summary>
public sealed class CliEnvironment
{
    private readonly ILogger<CliEnvironment> _logger;

    public CliEnvironment(ILogger<CliEnvironment> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// No-op retained for the probe base class. Claude / Codex confirm trust via
    /// their own PTY prompt step, so there is nothing to pre-seed on disk.
    /// </summary>
    public bool EnsureFolderTrusted(string folder) => false;

    /// <summary>No-op retained for the probe base class.</summary>
    public bool EnsureTerminalSetupAcknowledged(params string[] terminalIds) => false;

    /// <summary>
    /// Environment overrides every observing PTY spawn must carry: quota probes,
    /// model discovery, and the parser-development probe endpoint read the
    /// installed CLI, they never mutate it. Without the guard an interactive
    /// start may auto-update the global npm package mid-probe and leave a
    /// half-installed CLI behind for every later run (AGT-2706). The keys mirror
    /// the run spawn path in <c>CliExecutionServiceBase</c> and are layered on
    /// top of the terminal settings in <see cref="PtySession.SpawnAsync"/>.
    /// </summary>
    public static IDictionary<string, string> ProbeEnvironment()
        => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CLAUDE_CODE_DISABLE_AUTOUPDATER"] = "1",
            ["DISABLE_AUTOUPDATER"] = "1",
        };
}
