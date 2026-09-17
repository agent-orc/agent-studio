using System.Runtime.CompilerServices;

namespace AgentStudio.TestSupport;

/// <summary>
/// Suite-scoped temporary root for one test host process (AGT-2858).
///
/// The review host found 19,313 entries and 42 GB in the shared <c>/tmp</c>,
/// 15,959 of them older than a day. The largest groups were per-test fixture
/// directories: every suite run leaves roughly a hundred behind, and nothing
/// ever removes them. Per-fixture <c>Dispose</c> cleanup alone cannot close
/// that hole, because the dominant loss path is a test host that is killed or
/// crashes before any <c>Dispose</c> runs.
///
/// So the root is moved instead of trusted. <see cref="Redirect"/> points
/// <c>TMPDIR</c>/<c>TMP</c>/<c>TEMP</c> - the variables
/// <see cref="Path.GetTempPath"/> reads on both Linux and Windows - at one
/// per-process directory named <c>ats-&lt;token&gt;</c>. Every fixture that
/// keeps calling <c>Path.GetTempPath()</c> therefore lands inside that
/// directory, and the whole directory is removed when the process exits. A
/// killed process leaves exactly one entry instead of a hundred, and the next
/// process to start sweeps it away once it is older than
/// <see cref="StaleSuiteRootRetention"/>.
///
/// The root also stays <em>short</em>: which base directory it is created in and
/// how much of the path budget it may spend is
/// <see cref="TempRootLengthPolicy"/>, because a long redirected root breaks
/// every test that sizes paths against MAX_PATH.
///
/// Redirection happens from a <see cref="ModuleInitializerAttribute"/> in each
/// test assembly, which the CLR runs before any test type is touched.
/// </summary>
public static class TestTempRoot
{
    /// <summary>
    /// Optional operator override for the base directory. A runner host that
    /// already fences its verify commands can point this at the attempt-owned
    /// temp path so not even the suite root touches the shared temp root - as
    /// long as that path leaves the suite root inside
    /// <see cref="TempRootLengthPolicy.MaxSuiteRootLength()"/>.
    /// </summary>
    public const string BaseOverrideVariable = "AGENT_STUDIO_TEST_TEMP_ROOT";

    /// <summary>
    /// How long a suite root left behind by a killed test host is assumed to
    /// still belong to a live process. A suite run is bounded far below this,
    /// so anything older is residue.
    /// </summary>
    public static readonly TimeSpan StaleSuiteRootRetention = TimeSpan.FromHours(6);

    /// <summary>
    /// The fixture prefixes the review host measured in the shared temp root.
    /// The hygiene guard counts entries carrying them, so a fixture that finds
    /// a way around the redirect is named rather than silently tolerated.
    /// </summary>
    public static readonly IReadOnlyList<string> KnownFixturePrefixes =
    [
        "atp-",
        "agent-taskboard-tests-",
        "agent-studio-m1-pilot-cache",
        "bus-bridge-fake-job",
        "studio-task-artifacts",
        "rdo-",
        "orch-",
        "review-evidence-tests-",
    ];

    private static readonly Lock Gate = new();
    private static string? _suiteRoot;
    private static string? _hostTempRoot;
    private static bool _withinLengthBound;
    private static HashSet<string> _hostEntriesAtStart = new(StringComparer.Ordinal);

    /// <summary>
    /// The temp root the process was started with, before the redirect. This is
    /// the shared host <c>/tmp</c> the operator cleaned up by hand.
    /// </summary>
    public static string HostTempRoot
        => _hostTempRoot ?? throw new InvalidOperationException(NotRedirected);

    /// <summary>The per-process directory every fixture temp path now lands in.</summary>
    public static string SuiteRoot
        => _suiteRoot ?? throw new InvalidOperationException(NotRedirected);

    /// <summary>True once <see cref="Redirect"/> has run in this process.</summary>
    public static bool IsRedirected => _suiteRoot is not null;

    /// <summary>
    /// True when <see cref="SuiteRoot"/> fits
    /// <see cref="TempRootLengthPolicy.MaxSuiteRootLength()"/>. False means every
    /// temp root this host offers is too long; the suite still runs, but tests
    /// that size paths against MAX_PATH have less budget than they expect, so the
    /// hygiene guard reports it rather than letting it pass silently.
    /// </summary>
    public static bool IsWithinLengthBound => _withinLengthBound;

    private const string NotRedirected =
        "TestTempRoot.Redirect() has not run. Every test assembly needs the "
        + "[ModuleInitializer] that calls it (see TempRootBootstrap).";

    /// <summary>
    /// Creates the suite root, points the temp environment variables at it, and
    /// registers the process-exit sweep. Idempotent: the second and later calls
    /// return the root the first call created, so several test assemblies in one
    /// host share one root.
    /// </summary>
    public static string Redirect()
    {
        lock (Gate)
        {
            if (_suiteRoot is not null) return _suiteRoot;

            var host = Path.GetFullPath(Path.GetTempPath());
            _hostTempRoot = host;
            _hostEntriesAtStart = ReadEntryNames(host);

            var configured = Environment.GetEnvironmentVariable(BaseOverrideVariable);
            var candidates = TempRootLengthPolicy
                .CandidateBases(
                    string.IsNullOrWhiteSpace(configured) ? null : Path.GetFullPath(configured.Trim()),
                    host,
                    ShortestPlatformTempRoot())
                .Where(TryCreateDirectory)
                .ToArray();
            // Path.GetTempPath() itself was usable a moment ago, so an empty list
            // means the host temp root vanished mid-run; fall back to it and let
            // the directory create below produce the real error.
            var choice = candidates.Length == 0
                ? new TempRootChoice(host, WithinBound: false)
                : TempRootLengthPolicy.Choose(candidates, OperatingSystem.IsWindows());
            _withinLengthBound = choice.WithinBound;
            if (!choice.WithinBound)
            {
                Console.Error.WriteLine(
                    $"TestTempRoot: no temp root on this host keeps the suite root within "
                    + $"{TempRootLengthPolicy.MaxSuiteRootLength()} characters; using "
                    + $"'{choice.BaseDirectory}'. Path-length-sensitive tests may fail. Set "
                    + $"{BaseOverrideVariable} to a short directory.");
            }

            SweepStaleSuiteRoots(choice.BaseDirectory, DateTime.UtcNow);
            var suite = CreateSuiteRoot(choice.BaseDirectory);

            // Linux reads TMPDIR, Windows reads TMP then TEMP. Setting all three
            // keeps Path.GetTempPath() and every child process consistent.
            Environment.SetEnvironmentVariable("TMPDIR", suite);
            Environment.SetEnvironmentVariable("TMP", suite);
            Environment.SetEnvironmentVariable("TEMP", suite);

            AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDelete(suite);
            _suiteRoot = suite;
            return suite;
        }
    }

    /// <summary>
    /// A fresh directory for one fixture, already created. Prefer
    /// <see cref="TempWorkspace"/>, which also removes it again.
    ///
    /// The uniqueness suffix is half a GUID rather than a whole one: the suite
    /// root is bounded, and what a fixture below it can still spend on its own
    /// nested paths is the rest of that budget.
    /// </summary>
    public static string Create(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        var root = IsRedirected ? SuiteRoot : Path.GetTempPath();
        var path = Path.Combine(root, $"{prefix.TrimEnd('-')}-{Token(16)}");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Entries in the shared host temp root that carry one of
    /// <see cref="KnownFixturePrefixes"/> and were not there when this process
    /// started - the "did the count grow across the run" measurement, taken from
    /// inside the run.
    /// </summary>
    public static IReadOnlyList<string> HostResidueSinceStart()
    {
        if (!IsRedirected) return [];
        return ReadEntryNames(HostTempRoot)
            .Where(name => !_hostEntriesAtStart.Contains(name))
            .Where(name => KnownFixturePrefixes.Any(
                prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Recursive delete that tolerates the two things a plain
    /// <see cref="Directory.Delete(string, bool)"/> trips over in a test
    /// teardown: files Git marked read-only (enforced on Windows, ignored on
    /// Linux) and a file a still-unwinding child process has not released yet.
    /// Never throws - a teardown failure must not mask the test result.
    /// </summary>
    public static bool TryDelete(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                if (!Directory.Exists(path)) return true;
                DeleteWithoutFollowingReparsePoints(path);
                return !Directory.Exists(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A Windows handle is released asynchronously after the owning
                // process exits; a short backoff is what makes the retry useful.
                Thread.Sleep(25 * (attempt + 1));
            }
        }
        return !Directory.Exists(path);
    }

    /// <summary>
    /// Removes suite roots under <paramref name="baseDirectory"/> that are older
    /// than <see cref="StaleSuiteRootRetention"/>. This is what recovers the
    /// directories a killed or crashed test host could not delete itself; a run
    /// is bounded far below the retention, so a live root is never touched.
    ///
    /// The base directory is usually shared with the rest of the host - it is
    /// <c>/tmp</c> itself in the common case - so only names of the shape this
    /// class writes are candidates at all
    /// (<see cref="TempRootLengthPolicy.IsSuiteRootName"/>). Everything else in
    /// the directory belongs to someone else and is never touched, whatever its
    /// age.
    /// </summary>
    public static void SweepStaleSuiteRoots(string baseDirectory, DateTime utcNow)
    {
        var deadline = utcNow - StaleSuiteRootRetention;
        try
        {
            foreach (var candidate in Directory.EnumerateDirectories(baseDirectory))
            {
                var info = new DirectoryInfo(candidate);
                if (!TempRootLengthPolicy.IsSuiteRootName(info.Name)) continue;
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (info.LastWriteTimeUtc > deadline) continue;
                TryDelete(candidate);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Another test host is sweeping the same base directory. Losing the
            // race is harmless: the next process retries.
        }
    }

    /// <summary>
    /// Shortest temp root the platform offers, used as the last candidate when
    /// the inherited one is too long: the FHS temp directory on Unix, and a
    /// directory below the user's own temp root on Windows, where writing to the
    /// drive root is not the test host's business.
    /// </summary>
    private static string ShortestPlatformTempRoot()
    {
        if (!OperatingSystem.IsWindows()) return "/tmp";
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(localAppData)
            ? Path.GetFullPath(Path.GetTempPath())
            : Path.Combine(localAppData, "Temp");
    }

    /// <summary>
    /// The per-process suite root, retried on the (vanishingly unlikely) case of
    /// a token collision with a live root of another test host.
    /// </summary>
    private static string CreateSuiteRoot(string baseDirectory)
    {
        for (var attempt = 0; ; attempt++)
        {
            var candidate = TempRootLengthPolicy.SuiteRootIn(
                baseDirectory,
                Token(TempRootLengthPolicy.SuiteTokenLength));
            if (Directory.Exists(candidate) && attempt < 8) continue;
            Directory.CreateDirectory(candidate);
            return candidate;
        }
    }

    private static string Token(int length)
        => Guid.NewGuid().ToString("N")[..length];

    private static bool TryCreateDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            return true;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException
                      or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static HashSet<string> ReadEntryNames(string root)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(root)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!)
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private static void DeleteWithoutFollowingReparsePoints(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            ClearReadOnly(path, attributes);
            Directory.Delete(path);
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var entryAttributes = File.GetAttributes(entry);
            if ((entryAttributes & FileAttributes.Directory) == 0)
            {
                ClearReadOnly(entry, entryAttributes);
                File.Delete(entry);
                continue;
            }

            if ((entryAttributes & FileAttributes.ReparsePoint) != 0)
            {
                ClearReadOnly(entry, entryAttributes);
                Directory.Delete(entry);
                continue;
            }

            DeleteWithoutFollowingReparsePoints(entry);
        }

        ClearReadOnly(path, attributes);
        Directory.Delete(path);
    }

    private static void ClearReadOnly(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReadOnly) == 0) return;
        File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
    }
}
