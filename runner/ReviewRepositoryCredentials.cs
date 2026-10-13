using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>One read-only credential scoped to exactly one repository.</summary>
public sealed record ReviewRepositoryCredential(
    string Canonical,
    string CloneUrl,
    string Scheme,
    string Authority,
    string Username,
    string Password)
{
    public string CapabilityKey => ReviewRepositoryAccessPolicy.ReadCapabilityPrefix + Canonical;

    /// <summary>Path below the authority, lower case and without <c>.git</c>.</summary>
    public string RepositoryPath => Canonical[(Canonical.IndexOf('/') + 1)..];

    // The secret never reaches a log line or an exception message.
    public override string ToString() => $"ReviewRepositoryCredential {{ {Canonical} }}";
}

/// <summary>
/// The review executor profile's private-repository credentials (AGT-3077),
/// read from <c>RUNNER_REVIEW_GIT_CREDENTIALS_FILE</c>. The file uses the
/// <c>git credential-store</c> line format,
/// <c>https://&lt;user&gt;:&lt;token&gt;@github.com/&lt;owner&gt;/&lt;repo&gt;</c>, one
/// repository per line. A host-only line would hand one token to every
/// repository on that host, so every line must name a repository path.
/// Only the clone and fetch child processes of the review workspace ever see a
/// credential; build, test and aspect commands do not.
/// </summary>
public sealed class ReviewRepositoryCredentials
{
    public const string FileVariable = "RUNNER_REVIEW_GIT_CREDENTIALS_FILE";

    public static ReviewRepositoryCredentials Empty { get; } = new([]);

    private ReviewRepositoryCredentials(IReadOnlyList<ReviewRepositoryCredential> entries)
        => Entries = entries;

    public IReadOnlyList<ReviewRepositoryCredential> Entries { get; }

    public IReadOnlyList<string> CapabilityKeys
        => Entries.Select(entry => entry.CapabilityKey).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>The credential scoped to <paramref name="repositoryUrl"/>, or null.</summary>
    public ReviewRepositoryCredential? Find(string? repositoryUrl)
        => ReviewRepositoryAccessPolicy.CanonicalRepository(repositoryUrl) is { } canonical
            ? Entries.FirstOrDefault(entry => string.Equals(entry.Canonical, canonical, StringComparison.Ordinal))
            : null;

    /// <summary>Empty when <paramref name="path"/> is not configured.</summary>
    public static ReviewRepositoryCredentials Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Empty;
        if (!File.Exists(path)) throw new ArgumentException($"{FileVariable} does not exist: {path}");
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(path);
            if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                         | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) != 0)
                throw new ArgumentException($"{FileVariable} must be readable by its owner only (chmod 600).");
        }
        return Parse(File.ReadAllLines(path));
    }

    public static ReviewRepositoryCredentials Parse(IEnumerable<string> lines)
    {
        var entries = new List<ReviewRepositoryCredential>();
        var number = 0;
        foreach (var raw in lines)
        {
            number++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var entry = ParseLine(line)
                        ?? throw new ArgumentException(
                            $"{FileVariable} line {number} is not an https://<user>:<token>@<host>/<owner>/<repo> entry.");
            if (entries.Any(existing => existing.Canonical == entry.Canonical))
                throw new ArgumentException($"{FileVariable} line {number} repeats repository '{entry.Canonical}'.");
            entries.Add(entry);
        }
        return new ReviewRepositoryCredentials(entries);
    }

    private static ReviewRepositoryCredential? ParseLine(string line)
    {
        if (!Uri.TryCreate(line, UriKind.Absolute, out var uri)) return null;
        // A token travels in clear text over http, so plain http is accepted
        // only for a loopback host (local test servers).
        if (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) return null;
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return null;
        var separator = uri.UserInfo.IndexOf(':');
        if (separator <= 0 || separator == uri.UserInfo.Length - 1) return null;
        var username = Uri.UnescapeDataString(uri.UserInfo[..separator]);
        var password = Uri.UnescapeDataString(uri.UserInfo[(separator + 1)..]);
        if (username.Any(char.IsControl) || password.Any(char.IsControl)) return null;
        var cloneUrl = $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}";
        var canonical = ReviewRepositoryAccessPolicy.CanonicalRepository(cloneUrl);
        // Repository scope: at least <owner>/<repo> below the authority.
        if (canonical is null || canonical.Count(c => c == '/') < 2) return null;
        return new ReviewRepositoryCredential(
            canonical,
            cloneUrl,
            uri.Scheme,
            uri.Authority.ToLowerInvariant(),
            username,
            password);
    }
}
