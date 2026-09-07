using System.Runtime.CompilerServices;
using AgentStudio.Cli;
using Xunit;

namespace AgentStudio.Tests.Architecture;

/// <summary>
/// AGT-2706: a PTY spawn observes the installed CLI, it never mutates it. On
/// 2026-09-06 the Claude quota probe started the CLI without the updater guard,
/// the CLI auto-updated to a package layout whose postinstall did not run, and
/// every probe, model discovery, and coding run on that host was broken for
/// nine hours. Every <c>PtySession.SpawnAsync</c> call must therefore pass
/// <see cref="CliEnvironment.ProbeEnvironment"/> as <c>extraEnv</c>; the run
/// spawn path sets the same guard in <c>CliExecutionServiceBase</c>.
/// </summary>
public sealed class PtyProbeUpdaterGuardTests
{
    private const string SpawnMarker = "PtySession.SpawnAsync(";

    /// <summary>Where PTY spawns legitimately live.</summary>
    private static readonly string[] ScannedRoots = ["backend/Features", "backend/Host"];

    /// <summary>
    /// The five known spawn sites: two in <c>QuotaProbeBase</c>, Claude and Codex
    /// model discovery, and the parser-development probe endpoint. A lower count
    /// means the scanner stopped seeing them, not that the risk went away.
    /// </summary>
    private const int KnownSpawnSites = 5;

    [Fact]
    public void Probe_environment_disables_both_cli_self_updaters()
    {
        var environment = CliEnvironment.ProbeEnvironment();

        Assert.Equal("1", environment["CLAUDE_CODE_DISABLE_AUTOUPDATER"]);
        Assert.Equal("1", environment["DISABLE_AUTOUPDATER"]);
    }

    [Fact]
    public void Every_pty_spawn_site_carries_the_updater_guard()
    {
        var sites = ScanRepository();

        Assert.True(
            sites.Count >= KnownSpawnSites,
            $"Expected at least {KnownSpawnSites} PtySession.SpawnAsync sites but found {sites.Count}. "
            + "If a spawn site really was removed, lower KnownSpawnSites in the same change.");
        var unguarded = sites.Where(site => !site.Guarded).Select(site => site.ToString()).ToList();
        Assert.True(
            unguarded.Count == 0,
            "A PTY spawn must never let the CLI update itself. Pass "
            + "extraEnv: CliEnvironment.ProbeEnvironment() at:\n  "
            + string.Join("\n  ", unguarded));
    }

    [Fact]
    public void Guard_fires_on_a_spawn_without_the_probe_environment()
    {
        const string source = """
            await using var pty = await PtySession.SpawnAsync(
                app: resolvedPath,
                cwd: scratch,
                ct: ct);
            """;

        var site = Assert.Single(ScanSource("backend/Features/Cli/Probe.cs", source));

        Assert.False(site.Guarded);
    }

    [Fact]
    public void Guard_accepts_a_spawn_that_layers_the_probe_environment_on_top()
    {
        const string source = """
            await using var pty = await PtySession.SpawnAsync(
                app: resolvedPath,
                cwd: scratch,
                extraEnv: CliEnvironment.ProbeEnvironment(),
                cols: 220,
                ct: ct);
            """;

        Assert.True(Assert.Single(ScanSource("backend/Features/Cli/Probe.cs", source)).Guarded);
    }

    private sealed record SpawnSite(string File, int Line, bool Guarded)
    {
        public override string ToString() => $"{File}:{Line}";
    }

    /// <summary>
    /// Reads the argument list of every spawn call by balancing parentheses from
    /// the opening one, so a guard three arguments down still counts and a guard
    /// in the next call does not.
    /// </summary>
    private static IReadOnlyList<SpawnSite> ScanSource(string relativePath, string source)
    {
        var text = source.Replace("\r\n", "\n");
        var sites = new List<SpawnSite>();
        var from = 0;
        while (true)
        {
            var marker = text.IndexOf(SpawnMarker, from, StringComparison.Ordinal);
            if (marker < 0) break;
            var open = marker + SpawnMarker.Length - 1;
            var arguments = ArgumentList(text, open);
            sites.Add(new SpawnSite(
                relativePath,
                text.Take(marker).Count(character => character == '\n') + 1,
                arguments.Contains("extraEnv:", StringComparison.Ordinal)
                && arguments.Contains("CliEnvironment.ProbeEnvironment", StringComparison.Ordinal)));
            from = marker + SpawnMarker.Length;
        }
        return sites;
    }

    private static string ArgumentList(string text, int openParenthesis)
    {
        var depth = 0;
        for (var index = openParenthesis; index < text.Length; index++)
        {
            if (text[index] == '(') depth++;
            else if (text[index] == ')' && --depth == 0)
                return text[(openParenthesis + 1)..index];
        }
        return text[(openParenthesis + 1)..];
    }

    private static IReadOnlyList<SpawnSite> ScanRepository()
    {
        var root = RepoRoot();
        var found = new List<SpawnSite>();
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
