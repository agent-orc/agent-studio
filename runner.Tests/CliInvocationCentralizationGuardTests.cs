using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace AgentRunner.Tests;

// >>> cli-invocation-guard: mirrored region begin (backend.Tests <-> runner.Tests; keep byte-identical)
/// <summary>
/// Architecture guard for the CAR migration chain (AGT-2370 -&gt; AGT-2373,
/// <c>docs/operations/car-migration-plan.md</c> §3, tranche T3).
///
/// <para>Rule: inside <c>runner/**</c> and <c>backend/Features/{Cli,Runner}/**</c>
/// no file may start a coding-agent CLI on its own. A spawn marker
/// (<c>ProcessStartInfo</c> / <c>Process.Start</c> / <c>ProcessRunner.RunAsync</c>)
/// is a violation when it carries a CLI identity, in either of two flavours:</para>
/// <list type="bullet">
///   <item>a quoted <c>claude</c> / <c>codex</c> / <c>agentapi</c> / <c>gemini</c>
///   binary, one of the <c>Cli*</c> option knobs that resolve to one, or an
///   indirect <c>GetCliPath</c> / executable-resolver call, within
///   <see cref="ProximityLines"/> lines of the spawn;</item>
///   <item>anywhere in the same file, one of the runner types that exist solely
///   to describe a coding-agent invocation (<c>AgentCliProcess</c>,
///   <c>DetachedJobSpec</c>) — that is how the worker spawn stays caught after
///   the binary name moved into a spec record (T0b).</item>
/// </list>
/// <para>Any mention of a <c>RUNNER_CLI_*</c> environment variable is a
/// violation on its own. Git plumbing and verification commands are unaffected:
/// they carry no CLI identity.</para>
///
/// <para>Today the pre-CAR execution layer <em>is</em> the allowlist. That is the
/// point of landing the guard before the migration: it is alt-path agnostic, it
/// cannot undo what already exists, but from this moment on it stops the
/// invocation surface from spreading into new files. AGT-2370 and AGT-2371
/// remove entries, T4 (AGT-2373) drives the list to empty. The list grows
/// never — and <see cref="Allowlist_has_no_stale_entries"/> makes shrinking it
/// mandatory rather than optional.</para>
///
/// <para>Bauform follows the WikiPathCentralization / FeatureFolderBoundary
/// precedent: a deterministic source scanner, a build-breaking fact, and
/// fixtures that prove the scanner still fires
/// (<c>testdata/cli-fixtures/guard/</c>).</para>
///
/// <para>The class exists twice, once per deployable test suite, so neither the
/// backend nor the runner can ship without the check. The two copies are held
/// byte-identical by <see cref="Both_guard_instances_stay_byte_identical"/>.</para>
/// </summary>
public class CliInvocationCentralizationGuardTests
{
    private const string BackendInstancePath =
        "backend.Tests/Architecture/CliInvocationCentralizationGuardTests.cs";

    private const string RunnerInstancePath =
        "runner.Tests/CliInvocationCentralizationGuardTests.cs";

    private const string MirrorBeginMarker = "cli-invocation-guard: mirrored region begin";
    private const string MirrorEndMarker = "cli-invocation-guard: mirrored region end";

    /// <summary>Justification prefix every allowlist entry has to carry.</summary>
    private const string LegacyLayer =
        "legacy execution layer - shrinks with AGT-2370/2371, grows never. ";

    private static readonly string[] ScannedRoots =
    [
        "runner",
        "backend/Features/Cli",
        "backend/Features/Runner",
    ];

    /// <summary>Anything that turns a command line into a live operating-system process.</summary>
    private static readonly Regex SpawnMarker = new(
        @"\b(?:ProcessStartInfo|Process\.Start|ProcessRunner\.RunAsync)\b",
        RegexOptions.Compiled);

    /// <summary>
    /// Evidence near the spawn that the command is a coding-agent CLI: a quoted
    /// binary name, or one of the runner option knobs that resolve to one.
    /// </summary>
    private static readonly Regex NearbyCliIdentity = new(
        """
        "[^"\r\n]*\b(?:claude|codex|agentapi|gemini)\b[^"\r\n]*"|\b(?:Claude|Codex|Gemini|AgentApi)?CliBin\b|\bCliArgs\b|\bCliResumeArgs\b|\b(?:GetCliPath|ResolveExecutable|ResolveCliPath|ResolveCliExecutable)\s*\(
        """,
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The legacy local availability check starts the configured binary with
    /// exactly <c>--version</c>. It does not execute an agent request and stays
    /// host-owned. Keep this exception tied to that method and argument so it
    /// cannot hide another indirect CLI launch in the same file.
    /// </summary>
    private static readonly Regex VersionOnlyArgument = new(
        @"\bArguments\s*=\s*""--version""",
        RegexOptions.Compiled);

    /// <summary>
    /// Types that exist for no other purpose than describing a coding-agent
    /// invocation. Their presence anywhere in a file makes every spawn in that
    /// file a CLI spawn — the binary name may well sit in a serialised spec by
    /// then, far away from the process start (that is exactly what T0b did to
    /// <c>DurableAgentProcess</c>). Deliberately narrow: these names cannot turn
    /// up by accident, so no file gets flagged for merely running git.
    /// </summary>
    private static readonly Regex FileScopedCliIdentity = new(
        @"\b(?:AgentCliProcess|DetachedJobSpec)\b",
        RegexOptions.Compiled);

    /// <summary>The pre-CAR configuration surface; AGT-2373 deletes it outright.</summary>
    private static readonly Regex LegacyCliEnvironment = new(
        @"\bRUNNER_CLI_[A-Z_]*",
        RegexOptions.Compiled);

    /// <summary>
    /// How far a CLI identity may sit from the spawn marker and still count as
    /// the same invocation. Generous on purpose: a property initialiser block
    /// plus an argument list easily spans twenty lines, and a guard that only
    /// looks at the same line is trivially side-stepped.
    /// </summary>
    private const int ProximityLines = 30;

    /// <summary>
    /// One legitimate pre-CAR invocation site. <paramref name="Justification"/>
    /// must say why it is there and which card removes it.
    /// </summary>
    private sealed record AllowedFile(string Path, string Justification);

    private sealed record CliInvocationViolation(string File, int Line, string Kind, string Evidence)
    {
        public override string ToString() => $"{File}:{Line}  [{Kind}]  {Evidence}";
    }

    /// <summary>
    /// The execution layer as it stands before AGT-2370. Every entry is a file
    /// that legitimately starts a coding-agent CLI today; adding one is a
    /// migration regression, removing one is migration progress.
    /// </summary>
    private static readonly AllowedFile[] Allowlist =
    [
        // AGT-2370 (T1) removed two entries: runner/AgentCliProcess.cs no longer
        // spawns (pure invocation resolution since the dead instance path was
        // deleted), and runner/RemoteProjectChatRunner.cs runs through CAR with
        // PermissionMode=read-only (T1c) with no legacy fallback.

        new("runner/DurableAgentProcess.cs",
            LegacyLayer
            + "Detached-worker launch (re-execs the runner binary) plus the worker's legacy raw "
            + "ProcessRunner.RunAsync branch behind RUNNER_EXEC_ENGINE=legacy - the default engine is "
            + "CAR (ICliDriver) since AGT-2370. The detached-worker half is host responsibility and "
            + "stays; AGT-2373 deletes the legacy branch."),

        new("runner/RunnerOptions.cs",
            LegacyLayer
            + "Parses RUNNER_CLI_BIN / RUNNER_CLI_ARGS / RUNNER_CLI_RESUME_ARGS - since AGT-2370 the "
            + "binary-path and fallback surface for both engines (RUNNER_EXEC_ENGINE selects "
            + "car|legacy; a new RUNNER_CLI_TYPE knob was deliberately not added so this ratchet can "
            + "reach empty). AGT-2373 removes the trio."),

        new("runner/RemoteRunnerDaemon.cs",
            LegacyLayer
            + "AGT-2778 invokes the host-owned agent-runner-deploy update-clis boundary through sudo; "
            + "the allowlisted script installs only Task Server-pinned packages and never starts an "
            + "agent run. AGT-2373 decides the permanent non-run process boundary."),

        new("runner/RunnerCapabilityProbe.cs",
            LegacyLayer
            + "AGT-2778 directly runs --version while building the host capability snapshot; this is "
            + "a bounded read-only installation probe, not an agent run. AGT-2373 decides the permanent "
            + "non-run probe boundary."),

        new("runner/Program.cs",
            LegacyLayer
            + "Operator help text naming the RUNNER_CLI_* knobs. Goes away together with the knobs in AGT-2373."),

        new("backend/Features/Cli/Execution/BuiltInCliBehaviors.cs",
            LegacyLayer
            + "Local argv construction for claude / codex / agentapi. AGT-2371 replaces it with the CAR "
            + "descriptors (BuiltInDescriptors); --append-system-prompt-file stays studio-specific."),

        new("backend/Features/Cli/Execution/NpmShimHealer.cs",
            LegacyLayer
            + "Temporary repair helper for the explicit local rollback and non-agent ClaudeOneShot; "
            + "CAR owns repair on CAR-backed runs and AGT-2373 deletes this exception."),

        new("backend/Features/Cli/Routing/OneShot/ClaudeOneShot.cs",
            LegacyLayer
            + "Short-lived non-agent claude call (summaries, classification, verdict extraction). "
            + "AGT-2371 decides whether it moves onto CAR or stays as a documented exception."),

        new("backend/Features/Cli/Routing/OneShot/CodexOneShot.cs",
            LegacyLayer
            + "Same as ClaudeOneShot, for codex."),

        new("backend/Features/Runner/OrchestratorRunner.cs",
            LegacyLayer
            + "Legacy-test-only inline Claude fallback resolves GetCliPath and starts the CLI directly; "
            + "production uses ClaudeOneShot. AGT-2373 deletes the inline fallback."),
    ];

    [Fact]
    public void No_cli_invocation_outside_the_allowlisted_execution_layer()
    {
        var allowed = Allowlist.Select(entry => entry.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var violations = ScanRepository()
            .Where(violation => !allowed.Contains(violation.File))
            .Select(violation => violation.ToString())
            .ToList();

        Assert.True(
            violations.Count == 0,
            "A coding-agent CLI must not be started outside the execution layer. Route the run "
            + "through the execution layer (CodingAgentRunner ICliDriver after AGT-2370/2371) or, if "
            + "this really is a new legitimate site, add it to the allowlist in BOTH guard instances "
            + "with a justification naming the card that removes it again:\n  "
            + string.Join("\n  ", violations));
    }

    [Fact]
    public void Allowlist_has_no_stale_entries()
    {
        var found = ScanRepository()
            .Select(violation => violation.File)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var stale = Allowlist
            .Where(entry => !found.Contains(entry.Path))
            .Select(entry => entry.Path)
            .ToList();

        Assert.True(
            stale.Count == 0,
            "These allowlist entries no longer invoke a CLI (migration progress) or no longer exist. "
            + "Delete them from BOTH guard instances so the ratchet keeps its grip:\n  "
            + string.Join("\n  ", stale));
    }

    [Fact]
    public void Every_allowlist_entry_says_why_it_exists_and_what_removes_it()
    {
        var unjustified = Allowlist
            .Where(entry => !entry.Justification.StartsWith(LegacyLayer, StringComparison.Ordinal)
                            || !entry.Justification.Contains("AGT-23", StringComparison.Ordinal))
            .Select(entry => entry.Path)
            .ToList();

        Assert.True(
            unjustified.Count == 0,
            "Every allowlist entry must open with \"" + LegacyLayer.Trim()
            + "\" and name the AGT-23xx card that removes it:\n  "
            + string.Join("\n  ", unjustified));
    }

    [Fact]
    public void Guard_fires_on_the_deliberately_broken_fixture()
    {
        var fixture = File.ReadAllText(
            Path.Combine(RepoRoot(), "testdata", "cli-fixtures", "guard", "violating-spawn.cs.txt"));

        var violations = ScanSource("runner/NewCodingAgentLauncher.cs", fixture);

        Assert.Contains(violations, violation => violation.Kind == "cli-spawn");
        Assert.Contains(violations, violation => violation.Kind == "legacy-env");
        // The spec-driven spawn carries no binary literal at all; only the
        // file-scoped identity rule catches it. Losing that check would let the
        // whole post-T0b worker path slip past the guard.
        Assert.Contains(
            violations,
            violation => violation.Evidence.Contains("ProcessRunner.RunAsync", StringComparison.Ordinal));
        Assert.Contains(
            violations,
            violation => violation.Kind == "cli-spawn"
                         && (violation.Evidence.Contains("GetCliPath", StringComparison.Ordinal)
                             || violation.Evidence.Contains("ResolveExecutable", StringComparison.Ordinal)));
        Assert.True(
            violations.Count >= 6,
            "The sharpness fixture encodes six violations (ProcessStartInfo with a claude binary, "
            + "Process.Start on it, a spec-driven ProcessRunner.RunAsync, RUNNER_CLI_BIN from the "
            + "environment, and an indirect GetCliPath launch through ProcessStartInfo and Process.Start) "
            + "but the scanner reported " + violations.Count + ": "
            + string.Join(" | ", violations));
    }

    [Fact]
    public void Guard_stays_silent_on_the_compliant_fixture()
    {
        var fixture = File.ReadAllText(
            Path.Combine(RepoRoot(), "testdata", "cli-fixtures", "guard", "compliant-spawn.cs.txt"));

        var violations = ScanSource("runner/CompliantExecution.cs", fixture);

        Assert.True(
            violations.Count == 0,
            "Starting git is legal and a CLI name inside a comment is not an invocation. "
            + "The scanner over-reported:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void Both_guard_instances_stay_byte_identical()
    {
        var root = RepoRoot();
        var backend = MirroredRegion(Path.Combine(root, BackendInstancePath.Replace('/', Path.DirectorySeparatorChar)));
        var runner = MirroredRegion(Path.Combine(root, RunnerInstancePath.Replace('/', Path.DirectorySeparatorChar)));

        Assert.True(
            string.Equals(backend, runner, StringComparison.Ordinal),
            "The rule, the allowlist and the scanner exist twice on purpose - once per deployable "
            + "test suite. They have drifted apart. Copy the mirrored region from one instance to the "
            + "other so a relaxation cannot hide in the suite that was not run:\n  "
            + BackendInstancePath + "\n  " + RunnerInstancePath);
    }

    private static string MirroredRegion(string file)
    {
        var text = File.ReadAllText(file).Replace("\r\n", "\n");
        var begin = text.IndexOf(MirrorBeginMarker, StringComparison.Ordinal);
        var end = text.LastIndexOf(MirrorEndMarker, StringComparison.Ordinal);
        Assert.True(begin >= 0 && end > begin, $"Mirror markers missing or reversed in {file}.");
        var from = text.IndexOf('\n', begin) + 1;
        var to = text.LastIndexOf('\n', end - 1) + 1;
        return text[from..to];
    }

    /// <summary>
    /// Deterministic single-file scan. Comment-only lines are blanked first, so
    /// a doc comment that merely mentions <c>Process.Start</c> with a CLI name
    /// is not an invocation.
    /// </summary>
    private static IReadOnlyList<CliInvocationViolation> ScanSource(string relativePath, string source)
    {
        var raw = source.Replace("\r\n", "\n").Split('\n');
        var code = new string[raw.Length];
        for (var i = 0; i < raw.Length; i++)
        {
            var trimmed = raw[i].TrimStart();
            code[i] = trimmed.StartsWith("//", StringComparison.Ordinal)
                      || trimmed.StartsWith("*", StringComparison.Ordinal)
                      || trimmed.StartsWith("/*", StringComparison.Ordinal)
                ? string.Empty
                : raw[i];
        }

        var declaresInvocationSpec = code.Any(line => FileScopedCliIdentity.IsMatch(line));

        var found = new List<CliInvocationViolation>();
        for (var i = 0; i < code.Length; i++)
        {
            if (SpawnMarker.IsMatch(code[i])
                && !IsVersionOnlyAvailabilityProbe(relativePath, code, i))
            {
                string? evidence = null;
                var from = Math.Max(0, i - ProximityLines);
                var to = Math.Min(code.Length - 1, i + ProximityLines);
                for (var distance = 0; distance <= ProximityLines && evidence is null; distance++)
                {
                    foreach (var j in distance == 0 ? new[] { i } : new[] { i - distance, i + distance })
                    {
                        if (j < from || j > to) continue;
                        var identity = NearbyCliIdentity.Match(code[j]);
                        if (!identity.Success) continue;
                        evidence = $"CLI identity on line {j + 1}: {identity.Value.Trim()}";
                        break;
                    }
                }
                evidence ??= declaresInvocationSpec ? "the file declares a coding-agent invocation spec" : null;

                if (evidence is not null)
                    found.Add(new CliInvocationViolation(
                        relativePath, i + 1, "cli-spawn", $"{code[i].Trim()}   <- {evidence}"));
            }

            var legacy = LegacyCliEnvironment.Match(code[i]);
            if (legacy.Success)
                found.Add(new CliInvocationViolation(relativePath, i + 1, "legacy-env", legacy.Value));
        }

        return found;
    }

    private static bool IsVersionOnlyAvailabilityProbe(
        string relativePath,
        IReadOnlyList<string> code,
        int markerLine)
    {
        if (!string.Equals(
                relativePath,
                "backend/Features/Cli/Execution/CliExecutionServiceBase.cs",
                StringComparison.OrdinalIgnoreCase))
            return false;

        var methodFrom = Math.Max(0, markerLine - 8);
        var belongsToProbe = Enumerable.Range(methodFrom, markerLine - methodFrom + 1)
            .Any(line => code[line].Contains("DefaultTestCliPath(", StringComparison.Ordinal));
        if (!belongsToProbe) return false;

        var argumentTo = Math.Min(code.Count - 1, markerLine + 8);
        return Enumerable.Range(markerLine, argumentTo - markerLine + 1)
            .Any(line => VersionOnlyArgument.IsMatch(code[line]));
    }

    private static IReadOnlyList<CliInvocationViolation> ScanRepository()
    {
        var root = RepoRoot();
        var found = new List<CliInvocationViolation>();
        foreach (var relativeRoot in ScannedRoots)
        {
            var absolute = Path.Combine(root, relativeRoot.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(absolute)) continue;
            var files = Directory
                .EnumerateFiles(absolute, "*.cs", SearchOption.AllDirectories)
                .Where(file => !IsBuildOutput(file))
                .OrderBy(file => file, StringComparer.Ordinal);
            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                found.AddRange(ScanSource(relative, File.ReadAllText(file)));
            }
        }
        return found;
    }

    private static bool IsBuildOutput(string file)
        => file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
           || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    /// <summary>
    /// Repository root. The compile-time source path is authoritative so the
    /// guard still finds the tree when the test binary is built into an
    /// out-of-tree output directory; the base-directory walk is the fallback.
    /// </summary>
    private static string RepoRoot([CallerFilePath] string sourceFile = "")
    {
        foreach (var start in new[] { Path.GetDirectoryName(sourceFile), AppContext.BaseDirectory })
        {
            var current = start;
            while (!string.IsNullOrEmpty(current))
            {
                if (File.Exists(Path.Combine(current, "agent-taskboard.sln"))) return current;
                current = Path.GetDirectoryName(current);
            }
        }

        throw new InvalidOperationException(
            "agent-taskboard.sln not found above the guard source file or the test base directory.");
    }
}
// <<< cli-invocation-guard: mirrored region end
