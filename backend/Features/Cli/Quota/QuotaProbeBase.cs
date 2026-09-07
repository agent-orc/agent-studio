using System.Text.RegularExpressions;

namespace AgentStudio.Cli;

/// <summary>
/// Shared scaffolding for PTY-based quota probes:
/// <list type="bullet">
///   <item>Resolves the CLI binary (handles Windows .cmd shims).</item>
///   <item>Spawns it in a scratch directory so we never trigger workspace-trust prompts on the real repo.</item>
///   <item>Pre-seeds CLI environment trust via <see cref="CliEnvironment"/> (no-op today; each CLI confirms trust over its own PTY prompt).</item>
///   <item>Exposes <see cref="ProbeWithSnapshotAsync"/>: spawn, send keys, return ANSI-stripped snapshot, clean exit.</item>
/// </list>
/// </summary>
public abstract class QuotaProbeBase : IQuotaProbe
{
    protected readonly ILogger _logger;
    protected readonly CliRouter _router;
    protected readonly CliEnvironment _env;

    protected QuotaProbeBase(ILogger logger, CliRouter router, CliEnvironment env)
    {
        _logger = logger;
        _router = router;
        _env = env;
    }

    public abstract string CliType { get; }
    public abstract Task<QuotaSnapshot> ProbeAsync(CancellationToken ct);

    /// <summary>
    /// Read the CLI version through the same centrally resolved executable used
    /// by the probe. Version detection is best-effort so an unavailable binary
    /// is still reported through the normal probe error path.
    /// </summary>
    protected string? DetectCliVersion()
    {
        try { return _router.Get(CliType).TestCliPath().Version; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read {Cli} version before quota probe", CliType);
            return null;
        }
    }

    /// <summary>Keep cancellation implementation details out of operator-facing quota errors.</summary>
    protected static string DescribeProbeFailure(Exception ex)
        => ex is OperationCanceledException
            ? "Quota probe timed out before the CLI panel rendered."
            : ex.Message;

    /// <summary>
    /// Spawn the CLI, optionally send a slash-command sequence, wait for output to settle,
    /// return the ANSI-stripped snapshot. Always sends two Esc presses at the end so
    /// modal pickers close before the process is torn down.
    /// </summary>
    protected async Task<string> ProbeWithSnapshotAsync(
        string? sendKeys,
        int initialIdleMs,
        int settleAfterSendMs,
        CancellationToken ct)
    {
        var cli = _router.Get(CliType);
        var (available, _, resolvedPath) = cli.TestCliPath();
        if (!available)
            throw new InvalidOperationException($"{CliType} CLI not available");

        var scratch = Path.Combine(Path.GetTempPath(), "agent-taskboard-quota", CliType);
        Directory.CreateDirectory(scratch);
        try
        {
            _env.EnsureFolderTrusted(scratch);
            _env.EnsureTerminalSetupAcknowledged("vscode", "vscode-insiders", "windows-terminal");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Pre-probe environment setup failed (best-effort)");
        }

        await using var pty = await PtySession.SpawnAsync(
            app: resolvedPath,
            cwd: scratch,
            extraEnv: CliEnvironment.ProbeEnvironment(),
            ct: ct);
        await pty.WaitForIdleAsync(idleMs: 1500, timeoutMs: initialIdleMs, ct);

        if (!string.IsNullOrEmpty(sendKeys))
        {
            await pty.SendKeysAsync(sendKeys, ct);
            await pty.WaitForIdleAsync(idleMs: 700, timeoutMs: settleAfterSendMs, ct);
        }

        var snap = pty.SnapshotStripped();
        try { await pty.SendKeysAsync("<Esc><Esc>", ct); } catch (Exception __ex) { SilentCatch.Note(__ex, "QuotaProbeBase:71"); }
        return snap;
    }

    /// <summary>
    /// Spawn the CLI and orchestrate a multi-step interactive sequence. Each <see cref="ProbeStep"/>
    /// optionally waits for a regex pattern to appear, sends keys, then waits for output to settle.
    /// <paramref name="onPatternMatched"/> observes confirmed matches before their keys are sent.
    /// Returns the final ANSI-stripped snapshot.
    /// </summary>
    protected async Task<string> ProbeWithStepsAsync(
        IEnumerable<ProbeStep> steps,
        int initialIdleMs,
        CancellationToken ct,
        Action<ProbeStep>? onPatternMatched = null)
    {
        var cli = _router.Get(CliType);
        var (available, _, resolvedPath) = cli.TestCliPath();
        if (!available)
            throw new InvalidOperationException($"{CliType} CLI not available");

        var scratch = Path.Combine(Path.GetTempPath(), "agent-taskboard-quota", CliType);
        Directory.CreateDirectory(scratch);
        try
        {
            _env.EnsureFolderTrusted(scratch);
            _env.EnsureTerminalSetupAcknowledged("vscode", "vscode-insiders", "windows-terminal");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Pre-probe environment setup failed (best-effort)");
        }

        await using var pty = await PtySession.SpawnAsync(
            app: resolvedPath,
            cwd: scratch,
            extraEnv: CliEnvironment.ProbeEnvironment(),
            ct: ct);
        await pty.WaitForIdleAsync(idleMs: 1500, timeoutMs: initialIdleMs, ct);

        foreach (var step in steps)
        {
            if (step.ClearBufferBefore) pty.ClearBuffer();
            bool patternMatched = step.WaitForPattern == null;
            if (step.WaitForPattern != null)
            {
                var match = await pty.WaitForPatternAsync(step.WaitForPattern, timeoutMs: step.WaitTimeoutMs, ct);
                patternMatched = match != null;
                if (patternMatched) onPatternMatched?.Invoke(step);
                if (match == null)
                {
                    _logger.LogDebug("Probe step '{Step}' did not see expected pattern within {Ms}ms",
                        step.Name, step.WaitTimeoutMs);
                    if (step.RequirePattern) break; // give up; downstream parser will report best-effort failure
                }
            }
            if (step.PreSendDelayMs > 0)
            {
                try { await Task.Delay(step.PreSendDelayMs, ct); } catch (Exception __ex) { SilentCatch.Note(__ex, "QuotaProbeBase:122"); }
            }
            if (!string.IsNullOrEmpty(step.SendKeys))
            {
                if (step.SendKeysOnlyIfMatched && !patternMatched)
                {
                    _logger.LogDebug("Probe step '{Step}' skipping SendKeys because pattern did not match",
                        step.Name);
                }
                else
                {
                    await pty.SendKeysAsync(step.SendKeys, ct);
                    await pty.WaitForIdleAsync(idleMs: step.SettleIdleMs, timeoutMs: step.SettleTimeoutMs, ct);
                }
            }
        }

        var snap = pty.SnapshotStripped();
        try { await pty.SendKeysAsync("<Esc><Esc>", ct); } catch (Exception __ex) { SilentCatch.Note(__ex, "QuotaProbeBase:140"); }
        return snap;
    }

    public sealed record ProbeStep(
        string Name,
        Regex? WaitForPattern = null,
        int WaitTimeoutMs = 6000,
        bool RequirePattern = false,
        string? SendKeys = null,
        int SettleIdleMs = 700,
        int SettleTimeoutMs = 5000,
        /// <summary>Delay between pattern match and SendKeys, useful when the CLI needs
        /// a moment after rendering before it accepts new input (rare but observed in Codex).</summary>
        int PreSendDelayMs = 0,
        /// <summary>Wipe the PTY buffer before this step's pattern wait. Lets you scope
        /// pattern matches to "what the CLI prints from now on" instead of accidentally
        /// matching residual content from earlier screens.</summary>
        bool ClearBufferBefore = false,
        /// <summary>When true, only send <see cref="SendKeys"/> if <see cref="WaitForPattern"/>
        /// actually matched. Required for trust-prompt steps that, once dismissed once,
        /// never reappear: blindly sending the dismissal keys after the timeout leaks them
        /// into the chat input as a stray message and corrupts the next slash command.</summary>
        bool SendKeysOnlyIfMatched = false);

    /// <summary>Truncate snapshots before storing them on a QuotaSnapshot to keep payloads small.</summary>
    protected static string TruncateForDebug(string? snapshot, int max = 1500)
    {
        if (string.IsNullOrEmpty(snapshot)) return "";
        return snapshot.Length <= max ? snapshot : snapshot[^max..];
    }
}
