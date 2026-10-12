using System.Text;

namespace AgentStudio.TestSupport;

/// <summary>
/// Makes the real provider CLIs unreachable by bare name inside a test host
/// (AGT-3087).
///
/// Production code falls back to the bare executable name <c>claude</c> when
/// <c>ClaudeCli:Path</c> is not configured, and most tests build those services
/// without configuring it. On a runner host that bare name is the real
/// npm-installed CLI, so the 12.10.2026 sweep measured about 90 real
/// <c>claude -p</c> spawns a minute from <c>OrchestratorApi.Tests</c>: each one a
/// TLS round trip to the provider, a leftover transcript folder under
/// <c>~/.claude/projects</c>, and a billed call as soon as the host login is
/// valid again. Converting every call site to an explicit stub does not hold,
/// so the test host itself puts a stub first on the <c>PATH</c>.
///
/// <see cref="Install"/> writes one stub per name in <see cref="GuardedClis"/>
/// into <c>&lt;suiteRoot&gt;/cli-guard/</c> and prepends that directory to the
/// process <c>PATH</c>, which every child inherits. A test that configures an
/// explicit CLI path (a <see cref="StubCli"/> script, <c>fake-cli.mjs</c>) keeps
/// using it; only the bare name is intercepted. The stub decides on argv shape
/// alone, never on a model id:
/// <list type="bullet">
///   <item><c>--version</c> as the only argument prints <see cref="VersionLine"/>
///   and exits 0, without logging. Availability probes and the npm-shim repair
///   gate stay quiet, and the number clears every model's minimum CLI
///   version.</item>
///   <item>Anything else logs one line to <see cref="LogDirectory"/>, drains
///   stdin when <c>-p</c> is present (so the caller's prompt write never hits a
///   broken pipe), prints <see cref="RefusalMessage"/> to stderr and exits 1 -
///   the same outcome as today's failed-auth call.</item>
/// </list>
///
/// The guard deliberately leaves <c>CLAUDE_CONFIG_DIR</c> alone: only an
/// absolute-path call can bypass the <c>PATH</c> stub, and in the default suite
/// those reach a missing file or run <c>--version</c> only, neither of which
/// writes a transcript. Redirecting it would change what
/// <c>ProviderCredentialMonitor</c> reads in <c>runner.Tests</c> for no gain.
///
/// This file never reads the clock (<c>TestClockGuardTests</c> scans it); the
/// log lines carry no timestamp.
/// </summary>
public static class TestCliGuard
{
    /// <summary>
    /// Optional override for the invocation log directory. A remote run points
    /// it outside the suite root, which is deleted at process exit, to keep the
    /// log for the follow-up list of unguarded callers.
    /// </summary>
    public const string LogDirectoryVariable = "AGENT_STUDIO_TEST_CLI_GUARD_LOG";

    /// <summary>
    /// What <c>claude --version</c> prints under the guard. The number has to
    /// parse (the runner probe reads a line without one as "not installed") and
    /// stay above every <c>MinimumCliVersion</c>, or model admission would
    /// reject pinned models.
    /// </summary>
    public const string VersionLine = "99.0.0 (agent-studio test guard)";

    /// <summary>Names intercepted on the <c>PATH</c>. Add <c>codex</c> here once it is measured.</summary>
    public static readonly IReadOnlyList<string> GuardedClis = ["claude"];

    private const string GuardDirectoryName = "cli-guard";
    private const string InvocationsDirectoryName = "invocations";

    private static readonly Lock Gate = new();
    private static string? _guardDirectory;
    private static string? _logDirectory;

    /// <summary>True once <see cref="Install"/> succeeded in this process.</summary>
    public static bool IsInstalled => _guardDirectory is not null;

    /// <summary>Directory holding the stubs; first entry on the process <c>PATH</c>.</summary>
    public static string GuardDirectory
        => _guardDirectory ?? throw new InvalidOperationException(NotInstalled);

    /// <summary>Absolute directory every stub writes its invocation files into.</summary>
    public static string LogDirectory
        => _logDirectory ?? throw new InvalidOperationException(NotInstalled);

    private const string NotInstalled =
        "TestCliGuard.Install() has not run. TestTempRoot.Redirect() installs it.";

    /// <summary>What a guarded call other than <c>--version</c> prints to stderr.</summary>
    public static string RefusalMessage(string cli)
        => $"agent-studio test guard: the real {cli} CLI is not available in tests";

    /// <summary>
    /// Writes the stubs below <paramref name="suiteRoot"/> and puts them first
    /// on the <c>PATH</c>. Idempotent. Never throws: a failure is reported on
    /// stderr once and the test host keeps running unguarded, exactly as before.
    /// </summary>
    public static void Install(string suiteRoot)
    {
        lock (Gate)
        {
            if (_guardDirectory is not null) return;
            try
            {
                var guard = Path.Combine(Path.GetFullPath(suiteRoot), GuardDirectoryName);
                var configured = Environment.GetEnvironmentVariable(LogDirectoryVariable);
                var log = string.IsNullOrWhiteSpace(configured)
                    ? Path.Combine(guard, InvocationsDirectoryName)
                    : Path.GetFullPath(configured.Trim());
                Directory.CreateDirectory(guard);
                Directory.CreateDirectory(log);

                var windows = OperatingSystem.IsWindows();
                foreach (var cli in GuardedClis)
                {
                    var stub = Path.Combine(guard, windows ? cli + ".cmd" : cli);
                    // No BOM: cmd.exe would read it as part of the first command.
                    File.WriteAllText(stub, StubScript(cli, log, windows), new UTF8Encoding(false));
                    // .NET's PATH lookup skips a file without the execute bit and
                    // would fall through to the real CLI further down the PATH.
                    if (!windows)
                    {
                        File.SetUnixFileMode(
                            stub,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    }
                }

                var path = Environment.GetEnvironmentVariable("PATH");
                Environment.SetEnvironmentVariable(
                    "PATH",
                    string.IsNullOrEmpty(path) ? guard : guard + Path.PathSeparator + path);
                _logDirectory = log;
                _guardDirectory = guard;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"TestCliGuard: could not install the CLI guard ({exception.GetType().Name}: "
                    + $"{exception.Message}); bare '{string.Join("', '", GuardedClis)}' calls in this "
                    + "test host reach the real CLI.");
            }
        }
    }

    /// <summary>
    /// Every guarded call logged so far, by this host and by any other host
    /// sharing <see cref="LogDirectory"/>. Callers filter on their own cwd or a
    /// marker argument; other tests hit the guard in parallel.
    /// </summary>
    public static IReadOnlyList<TestCliGuardInvocation> ReadInvocations()
    {
        if (_logDirectory is null || !Directory.Exists(_logDirectory)) return [];
        var invocations = new List<TestCliGuardInvocation>();
        foreach (var file in Directory.EnumerateFiles(_logDirectory, "*.log"))
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A concurrent Windows stub still holds its own file. It is not
                // the reader's call: that stub has already exited.
                continue;
            }
            foreach (var line in lines)
            {
                if (TestCliGuardInvocation.TryParse(line) is { } invocation) invocations.Add(invocation);
            }
        }
        return invocations;
    }

    /// <summary>
    /// The stub text for one CLI. Pure, so the shape is reviewable without a
    /// filesystem. Lines are LF for <c>sh</c> and CRLF for <c>cmd.exe</c>,
    /// whatever the line endings of this source file are.
    /// </summary>
    public static string StubScript(string cli, string logDirectory, bool windows)
    {
        var lines = windows ? WindowsStub(cli, logDirectory) : PosixStub(cli, logDirectory);
        return string.Join(windows ? "\r\n" : "\n", lines) + (windows ? "\r\n" : "\n");
    }

    private static string[] PosixStub(string cli, string logDirectory) =>
    [
        "#!/bin/sh",
        $"# agent-studio test guard for '{cli}' (AGT-3087), written by TestCliGuard.",
        "# Stands in for the real CLI so a test can never start it.",
        "if [ \"$#\" -eq 1 ] && [ \"$1\" = \"--version\" ]; then",
        $"  printf '%s\\n' {QuoteForShell(VersionLine)}",
        "  exit 0",
        "fi",
        $"log={QuoteForShell(logDirectory)}",
        $"line=\"{cli}\t$(pwd)\t$*\"",
        "print=",
        "for arg in \"$@\"; do",
        "  if [ \"$arg\" = \"-p\" ] || [ \"$arg\" = \"--print\" ]; then print=1; fi",
        "done",
        "mkdir -p \"$log\" 2>/dev/null",
        // One file per call, renamed into place, so a reader never sees half a
        // line and concurrent stubs never interleave.
        "tmp=$(mktemp \"$log/.$$-XXXXXXXX\" 2>/dev/null)",
        "if [ -n \"$tmp\" ] && printf '%s\\n' \"$line\" > \"$tmp\"; then",
        "  name=${tmp##*/}",
        "  mv -f \"$tmp\" \"$log/${name#.}.log\"",
        "else",
        "  printf '%s\\n' \"$line\" >> \"$log/$$.log\"",
        "fi",
        "if [ -n \"$print\" ]; then cat >/dev/null 2>&1; fi",
        $"printf '%s\\n' {QuoteForShell(RefusalMessage(cli))} >&2",
        "exit 1",
    ];

    private static string[] WindowsStub(string cli, string logDirectory) =>
    [
        "@echo off",
        $"rem agent-studio test guard for '{cli}' (AGT-3087), written by TestCliGuard.",
        "rem Stands in for the real CLI so a test can never start it.",
        "setlocal EnableExtensions DisableDelayedExpansion",
        "if not \"%~1\"==\"--version\" goto :guard",
        "if not \"%~2\"==\"\" goto :guard",
        // Outside any parenthesised block: the version line has parentheses.
        $"echo {VersionLine}",
        "exit /b 0",
        ":guard",
        $"set \"_log={logDirectory.Replace("%", "%%", StringComparison.Ordinal)}\"",
        "set \"_args=%*\"",
        "set \"_print=\"",
        "for %%A in (%*) do if /I \"%%~A\"==\"-p\" set \"_print=1\"",
        "for %%A in (%*) do if /I \"%%~A\"==\"--print\" set \"_print=1\"",
        "if not exist \"%_log%\" mkdir \"%_log%\" >nul 2>&1",
        // One file per call: a shared file would hit a Windows sharing lock.
        ":name",
        "set \"_file=%_log%\\%RANDOM%-%RANDOM%%RANDOM%.log\"",
        "if exist \"%_file%\" goto :name",
        "setlocal EnableDelayedExpansion",
        $">\"!_file!\" echo {cli}\t!CD!\t!_args!",
        "endlocal",
        "if defined _print more >nul",
        $">&2 echo {RefusalMessage(cli)}",
        "exit /b 1",
    ];

    /// <summary>Single-quote for sh, closing and reopening around embedded quotes.</summary>
    private static string QuoteForShell(string value)
        => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}

/// <summary>
/// One logged call of a guard stub: the CLI name, the working directory the
/// caller started it in, and its arguments joined by single spaces.
/// </summary>
public sealed record TestCliGuardInvocation(string Cli, string WorkingDirectory, string Arguments)
{
    /// <summary>The arguments split back on whitespace.</summary>
    public IReadOnlyList<string> Argv
        => Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>True when the call was started in <paramref name="directory"/>.</summary>
    public bool RanIn(string directory)
        => string.Equals(
            Normalize(WorkingDirectory),
            Normalize(directory),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal static TestCliGuardInvocation? TryParse(string line)
    {
        var fields = line.TrimEnd('\r').Split('\t', 3);
        return fields.Length < 2 || fields[0].Length == 0
            ? null
            : new TestCliGuardInvocation(fields[0], fields[1], fields.Length == 3 ? fields[2].Trim() : "");
    }

    private static string Normalize(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return "";
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory.Trim()));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return directory.Trim();
        }
    }
}
