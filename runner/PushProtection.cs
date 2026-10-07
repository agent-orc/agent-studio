using System.Text.RegularExpressions;

namespace AgentRunner;

/// <summary>A provider-shaped value rejected by GitHub or found before a salvage commit.</summary>
public sealed record PushProtectionCause(string SecretType, string? Commit, string Path, int Line)
{
    public string Summary => $"GitHub push protection: {SecretType}-like literal at {Path}:{Line}" +
                             (Commit is null ? string.Empty : $" in commit {Commit[..Math.Min(7, Commit.Length)]}");
}

public static class PushProtection
{
    private static readonly Regex Commit = new(@"\bcommit:\s*(?<sha>[0-9a-f]{7,64})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Path = new(@"\bpath:\s*(?<path>.+):(?<line>\d+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SecretHeading = new(@"[-\u2500\u2014\u2013]{2,}\s*(?<type>[A-Za-z][A-Za-z0-9 ._+/()-]{2,80}?)\s*[-\u2500\u2014\u2013]{2,}", RegexOptions.Compiled);
    private static readonly (string Name, Regex Pattern)[] Patterns =
    [
        ("Mailgun API Key", new Regex(@"(?<![0-9A-Za-z])key-[0-9a-z]{32}(?![0-9A-Za-z])", RegexOptions.Compiled)),
        ("AWS Access Key ID", new Regex(@"(?<![A-Z0-9])(?:AKIA|ASIA)[A-Z0-9]{16}(?![A-Z0-9])", RegexOptions.Compiled)),
        ("GitHub Personal Access Token", new Regex(@"(?<![A-Za-z0-9_])gh[pousr]_[A-Za-z0-9_]{36,}(?![A-Za-z0-9_])", RegexOptions.Compiled)),
        ("Stripe Secret Key", new Regex(@"(?<![A-Za-z0-9_])sk_live_[A-Za-z0-9]{24,}(?![A-Za-z0-9])", RegexOptions.Compiled)),
        ("Slack Bot Token", new Regex(@"(?<![A-Za-z0-9-])xoxb-\d+-\d+-[A-Za-z0-9]+(?![A-Za-z0-9])", RegexOptions.Compiled)),
    ];

    public static PushProtectionCause? ParseGitHubRejection(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr) ||
            (!stderr.Contains("GH013", StringComparison.OrdinalIgnoreCase) &&
             !stderr.Contains("Push cannot contain secrets", StringComparison.OrdinalIgnoreCase)))
            return null;

        var lines = stderr.Split('\n');
        string? secretType = null;
        string? commit = null;
        string? path = null;
        int? pathLine = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("remote:", StringComparison.OrdinalIgnoreCase))
                line = line[7..].Trim();
            foreach (var (name, _) in Patterns)
                if (line.Contains(name, StringComparison.OrdinalIgnoreCase)) secretType = name;
            if (SecretHeading.Match(line) is { Success: true } heading &&
                !heading.Groups["type"].Value.Contains("PUSH PROTECTION", StringComparison.OrdinalIgnoreCase))
                secretType = heading.Groups["type"].Value.Trim();
            var commitMatch = Commit.Match(line);
            if (commitMatch.Success) commit = commitMatch.Groups["sha"].Value;
            var pathMatch = Path.Match(line);
            if (pathMatch.Success && int.TryParse(pathMatch.Groups["line"].Value, out var number))
            {
                path = pathMatch.Groups["path"].Value.Trim();
                pathLine = number;
            }
        }
        return secretType is not null && path is not null && pathLine is not null
            ? new PushProtectionCause(secretType, commit, path, pathLine.Value)
            : null;
    }

    /// <summary>Inspect only added lines in a zero-context Git diff.</summary>
    public static PushProtectionCause? ScanAddedLines(string diff)
    {
        var scanner = new PushProtectionDiffScanner();
        foreach (var line in diff.Split('\n'))
            scanner.AddLine(line);
        return scanner.Cause;
    }

    internal sealed class PushProtectionDiffScanner
    {
        private string? _path;
        private int _lineNumber;
        public PushProtectionCause? Cause { get; private set; }

        public void AddLine(string line)
        {
            if (Cause is not null) return;
            if (line.StartsWith("+++ b/", StringComparison.Ordinal))
                _path = line[6..].TrimEnd('\r');
            else if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                var match = Regex.Match(line, @"\+(?<line>\d+)");
                if (match.Success && int.TryParse(match.Groups["line"].Value, out var number))
                    _lineNumber = number;
            }
            else if (_path is not null && line.StartsWith('+') &&
                     !line.StartsWith("+++ b/", StringComparison.Ordinal))
            {
                foreach (var (name, pattern) in Patterns)
                    if (pattern.IsMatch(line[1..]))
                    {
                        Cause = new PushProtectionCause(name, null, _path, _lineNumber);
                        return;
                    }
                _lineNumber++;
            }
            else if (line.StartsWith(' ')) _lineNumber++;
        }
    }
}
