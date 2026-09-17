namespace AgentStudio.TestSupport;

/// <summary>
/// Where the redirected test temp root is placed, and how long it may get
/// (AGT-2858).
///
/// Moving the temp root is what stops the suite from leaking fixture
/// directories into the shared <c>/tmp</c> (see <see cref="TestTempRoot"/>), but
/// a redirect that *lengthens* the root breaks a different class of test. Paths
/// are a budget: Windows resolves a bare path against MAX_PATH (260), and git
/// writes <c>.git/refs/remotes/origin/&lt;branch&gt;.lock</c> below the
/// repository a fixture created inside the temp root. A fixture that sizes a
/// branch name against that budget - or any test that nests a repository, a
/// clone and a worktree - fails as soon as the root it starts from is long.
/// A first cut of this delivery nested the suite root under the review
/// attempt's own temp directory and produced roots of ~190 characters, which is
/// exactly how that surfaced.
///
/// So the length is a policy with a documented bound rather than an accident of
/// whichever directory the host handed us: the suite root must fit into
/// <see cref="MaxSuiteRootLengthUnix"/> characters on Unix and
/// <see cref="MaxSuiteRootLengthWindows"/> on Windows, which leaves every test
/// below it the same budget it had before the redirect existed.
///
/// Pure by construction - the candidate list and the choice between candidates
/// are the only decisions here, so they are tested as a matrix instead of
/// through a filesystem.
/// </summary>
public static class TempRootLengthPolicy
{
    /// <summary>
    /// Name prefix of a suite root. Deliberately terse: the name is spent from
    /// the same budget as the base directory, and it only has to be recognisable
    /// to the sweep in <see cref="TestTempRoot.SweepStaleSuiteRoots"/>.
    /// </summary>
    public const string SuiteRootNamePrefix = "ats-";

    /// <summary>
    /// Random hex characters after the prefix. Eight is 2^32 per base directory,
    /// far beyond the number of test hosts that ever share one, and it keeps the
    /// whole name at twelve characters.
    /// </summary>
    public const int SuiteTokenLength = 8;

    /// <summary>
    /// Bound on Linux and macOS. A fixture that builds a git repository, clones
    /// it and writes a long ref needs roughly 120 characters of the 260 MAX_PATH
    /// budget for its own paths; 60 leaves that intact with margin, and
    /// <c>/tmp</c> fits with room to spare. A macOS <c>$TMPDIR</c>
    /// (<c>/var/folders/...</c>) does not, so the suite root lands in
    /// <c>/tmp</c> there.
    /// </summary>
    public const int MaxSuiteRootLengthUnix = 60;

    /// <summary>
    /// Bound on Windows. Larger than the Unix one only because a Windows temp
    /// root is inherently longer - <c>%LOCALAPPDATA%\Temp</c> is already 33 to
    /// 40 characters on a normal profile, and a bound the platform's own temp
    /// root cannot meet would fail every Windows developer's suite instead of
    /// bounding anything. It still leaves roughly 195 of the 260 MAX_PATH
    /// characters to the tests below it, which is more than the ~120 a
    /// repository-building fixture needs.
    /// </summary>
    public const int MaxSuiteRootLengthWindows = 64;

    /// <summary>Length of a suite root directory name.</summary>
    public static int SuiteRootNameLength => SuiteRootNamePrefix.Length + SuiteTokenLength;

    /// <summary>The bound for <paramref name="windows"/>, as a number.</summary>
    public static int MaxSuiteRootLength(bool windows)
        => windows ? MaxSuiteRootLengthWindows : MaxSuiteRootLengthUnix;

    /// <summary>The bound on the host this process runs on.</summary>
    public static int MaxSuiteRootLength() => MaxSuiteRootLength(OperatingSystem.IsWindows());

    /// <summary>The suite root directory name for one token.</summary>
    public static string SuiteRootName(string token) => SuiteRootNamePrefix + token;

    /// <summary>The suite root path one base directory would produce.</summary>
    public static string SuiteRootIn(string baseDirectory, string token)
        => Path.Combine(baseDirectory, SuiteRootName(token));

    /// <summary>
    /// Length of the suite root <paramref name="baseDirectory"/> would produce,
    /// computed through <see cref="Path.Combine(string,string)"/> so a base with
    /// a trailing separator is measured exactly as it will be created.
    /// </summary>
    public static int PredictedSuiteRootLength(string baseDirectory)
        => Path.Combine(baseDirectory, new string('0', SuiteRootNameLength)).Length;

    /// <summary>
    /// True when <paramref name="name"/> is a suite root this code wrote. The
    /// sweep deletes by this name shape alone, so it can run in a base directory
    /// it shares with the rest of the host - a neighbour's directory can never
    /// match.
    /// </summary>
    public static bool IsSuiteRootName(string? name)
        => name is not null
           && name.Length == SuiteRootNameLength
           && name.StartsWith(SuiteRootNamePrefix, StringComparison.Ordinal)
           && name.Skip(SuiteRootNamePrefix.Length).All(IsLowerHex);

    /// <summary>
    /// Base directories to try, most preferred first: the operator's override,
    /// the temp root this process inherited, and the platform's shortest temp
    /// root as the last resort.
    ///
    /// The inherited root comes before the short one on purpose. Under a review
    /// attempt it is the attempt's own fenced temp directory, and staying inside
    /// the fence is worth more than brevity - until it costs more than the
    /// bound, which is what <see cref="Choose"/> decides.
    /// </summary>
    public static IReadOnlyList<string> CandidateBases(
        string? configuredOverride,
        string hostTempRoot,
        string shortestTempRoot)
    {
        var candidates = new List<string>(3);
        Add(configuredOverride);
        Add(hostTempRoot);
        Add(shortestTempRoot);
        return candidates;

        void Add(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return;
            var trimmed = candidate.Trim();
            if (candidates.Any(known => string.Equals(known, trimmed, StringComparison.Ordinal))) return;
            candidates.Add(trimmed);
        }
    }

    /// <summary>
    /// The base directory to redirect into: the first candidate whose suite root
    /// fits the bound, otherwise the shortest candidate with
    /// <see cref="TempRootChoice.WithinBound"/> false.
    ///
    /// Falling back rather than throwing is deliberate. A test host on a machine
    /// whose every temp root is long must still run; what it must not do is
    /// pretend the bound held, because a silently long root is what broke the
    /// path-length-sensitive tests in the first place. The caller reports the
    /// overrun and the hygiene guard fails on it.
    /// </summary>
    public static TempRootChoice Choose(IReadOnlyList<string> baseDirectories, bool windows)
    {
        ArgumentNullException.ThrowIfNull(baseDirectories);
        if (baseDirectories.Count == 0)
            throw new ArgumentException("At least one base directory is required.", nameof(baseDirectories));

        var bound = MaxSuiteRootLength(windows);
        foreach (var candidate in baseDirectories)
        {
            if (PredictedSuiteRootLength(candidate) <= bound)
                return new TempRootChoice(candidate, WithinBound: true);
        }

        var shortest = baseDirectories
            .OrderBy(PredictedSuiteRootLength)
            .ThenBy(candidate => candidate, StringComparer.Ordinal)
            .First();
        return new TempRootChoice(shortest, WithinBound: false);
    }

    private static bool IsLowerHex(char character)
        => character is >= '0' and <= '9' or >= 'a' and <= 'f';
}

/// <summary>Outcome of <see cref="TempRootLengthPolicy.Choose"/>.</summary>
/// <param name="BaseDirectory">Directory the suite root is created in.</param>
/// <param name="WithinBound">
/// False when not even the shortest candidate fits the documented bound, so the
/// redirect happened but the length guarantee did not hold.
/// </param>
public readonly record struct TempRootChoice(string BaseDirectory, bool WithinBound);
