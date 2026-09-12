using System.Diagnostics;

namespace AgentStudio.Cli;

/// <summary>
/// Abstraction over a spawned CLI child process so the runner does not care
/// whether the child was launched via plain pipe-redirection
/// (<see cref="System.Diagnostics.Process"/>) or via a pseudo-terminal.
///
/// <para>
/// Why this exists. Node-based CLIs (Claude Code, Codex, Gemini) on Windows
/// block-buffer <c>process.stdout</c> when stdout is a pipe instead of a
/// terminal: only the first init frame slips through, then everything else
/// stays in the Node side's 4-8 KB write buffer until process exit. From the
/// runner's perspective the agent looks "silent after init" and the watchdog
/// kills it as hung. The fix is to spawn the CLI behind a pseudo-terminal,
/// which makes <c>process.stdout.isTTY === true</c> and Node flushes per
/// newline (which is exactly what <c>--output-format stream-json</c> emits).
/// </para>
/// <para>
/// PTY-spawned children merge stderr into the main stream by construction;
/// PTY backends therefore set <see cref="Stderr"/> to a never-emitting reader
/// and let the runner's stdout loop see everything.
/// </para>
/// </summary>
public sealed record ChildHandle(
    Process Process,
    Stream Stdin,
    StreamReader Stdout,
    StreamReader Stderr,
    Action<RunStopReason>? KillOverride = null,
    string? DurableWorkerDirectory = null);
