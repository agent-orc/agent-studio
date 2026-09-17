using System.Runtime.CompilerServices;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentStudio.Tests.Architecture;

/// <summary>
/// Suite-level temp hygiene guard (AGT-2858).
///
/// The review host accumulated 19,313 entries and 42 GB in the shared
/// <c>/tmp</c>, 15,959 of them older than a day and all owned by the runner
/// service account. The bulk were per-test fixture directories
/// (<c>atp-crash*</c>, <c>atp-pickup-atomicity*</c>, <c>bus-bridge-fake-job*</c>,
/// <c>studio-task-artifacts*</c>, ...): every review runs the full suite, so the
/// leak compounded by roughly a hundred directories per review.
///
/// Per-fixture <c>Dispose</c> cleanup is necessary but not sufficient - the
/// dominant loss path is a test host that is killed or crashes before any
/// <c>Dispose</c> runs. What makes the guarantee hold is
/// <see cref="TestTempRoot"/>: the temp root itself is moved to one per-process
/// directory that is deleted at process exit and swept when it is left behind.
/// These tests hold that mechanism in place.
/// </summary>
public sealed class TempRootHygieneGuardTests
{
    /// <summary>
    /// Projects whose <see cref="Path.GetTempPath"/> callers rely on the
    /// redirect, so each of them must carry the module initializer that installs
    /// it. A new test project without one leaks into the shared temp root again.
    /// </summary>
    private static readonly string[] RedirectedTestProjects =
    [
        "backend.Tests",
        "runner.Tests",
        "task-server.Tests",
        "retention.Tests",
        "orchestrator-engine.Tests",
    ];

    [Fact]
    public void Suite_temp_root_is_redirected_away_from_the_shared_temp_root()
    {
        Assert.True(
            TestTempRoot.IsRedirected,
            "TestTempRoot.Redirect() did not run. Without it every fixture that calls "
            + "Path.GetTempPath() writes straight into the host's shared temp root.");

        var current = Path.GetFullPath(Path.GetTempPath());
        Assert.Equal(
            Path.GetFullPath(TestTempRoot.SuiteRoot) + Path.DirectorySeparatorChar,
            current.EndsWith(Path.DirectorySeparatorChar) ? current : current + Path.DirectorySeparatorChar);
        Assert.NotEqual(
            Path.GetFullPath(TestTempRoot.HostTempRoot),
            Path.GetFullPath(TestTempRoot.SuiteRoot));
        Assert.True(Directory.Exists(TestTempRoot.SuiteRoot));
    }

    /// <summary>
    /// The count measurement, taken from inside the run: entries under the
    /// shared temp root carrying a known fixture prefix must not grow while the
    /// suite executes. It is a live count, not a source scan, so it also catches
    /// a fixture that reaches the host root through a hardcoded path or a child
    /// process with a scrubbed environment.
    ///
    /// It observes every test that ran before it, which is what makes it useful
    /// as a cheap in-suite tripwire; the whole-run number is measured from the
    /// outside by the before/after count in
    /// <c>scripts/measure-temp-residue.sh</c>.
    ///
    /// Attribution note: under a review attempt the "shared" root is the
    /// attempt's own fenced temp directory, so the measurement is exact. On a
    /// developer host where several unfenced suites share the real <c>/tmp</c>,
    /// a neighbour's leak lands in the same count - the statement about the
    /// shared root stays true, but the failing suite may not be the one that
    /// caused it. Export <c>TMPDIR</c> before a local run to fence it.
    /// </summary>
    [Fact]
    public void Shared_temp_root_gains_no_fixture_entries_while_the_suite_runs()
    {
        var residue = TestTempRoot.HostResidueSinceStart();

        Assert.True(
            residue.Count == 0,
            $"{residue.Count} fixture entries appeared in the shared temp root "
            + $"'{TestTempRoot.HostTempRoot}' during this run. Every fixture temp path must go "
            + "through Path.GetTempPath() (redirected) or TempWorkspace, never a hardcoded root:\n  "
            + string.Join("\n  ", residue.Take(25)));
    }

    [Fact]
    public void Every_redirected_test_project_installs_the_module_initializer()
    {
        var root = RepoRoot();
        var missing = RedirectedTestProjects
            .Where(project => !HasModuleInitializer(Path.Combine(root, project, "TempRootBootstrap.cs")))
            .ToList();

        Assert.True(
            missing.Count == 0,
            "Each test project needs a TempRootBootstrap with a [ModuleInitializer] calling "
            + "TestTempRoot.Redirect(); otherwise its fixtures write into the shared temp root. "
            + "Missing in: " + string.Join(", ", missing));
    }

    [Fact]
    public void Temp_workspace_creates_inside_the_suite_root_and_removes_itself()
    {
        string path;
        using (var workspace = new TempWorkspace("atp-temp-hygiene-guard"))
        {
            path = workspace.Path;
            File.WriteAllText(workspace.Combine("evidence.txt"), "content");
            Assert.StartsWith(
                Path.GetFullPath(TestTempRoot.SuiteRoot),
                Path.GetFullPath(path),
                StringComparison.Ordinal);
            Assert.True(Directory.Exists(path));
        }

        Assert.False(Directory.Exists(path));
    }

    /// <summary>
    /// A suite root a killed test host left behind is residue after
    /// <see cref="TestTempRoot.StaleSuiteRootRetention"/> and is swept by the
    /// next process that starts - the part per-fixture cleanup can never cover.
    /// </summary>
    [Fact]
    public void Stale_suite_roots_are_swept_and_live_ones_are_kept()
    {
        using var baseDirectory = new TempWorkspace("atp-suite-root-sweep");
        var now = DateTime.UtcNow;
        var stale = baseDirectory.Directory("1234-stale");
        var live = baseDirectory.Directory("5678-live");
        File.WriteAllText(Path.Combine(stale, "leaked.txt"), "residue");
        Directory.SetLastWriteTimeUtc(stale, now - TestTempRoot.StaleSuiteRootRetention - TimeSpan.FromMinutes(1));
        Directory.SetLastWriteTimeUtc(live, now);

        TestTempRoot.SweepStaleSuiteRoots(baseDirectory.Path, now);

        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(live));
    }

    private static bool HasModuleInitializer(string bootstrapPath)
        => File.Exists(bootstrapPath)
           && File.ReadAllText(bootstrapPath) is var source
           && source.Contains("[ModuleInitializer]", StringComparison.Ordinal)
           && source.Contains("TestTempRoot.Redirect()", StringComparison.Ordinal);

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
