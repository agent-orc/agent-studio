using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace AgentStudio.Tests.Architecture;

/// <summary>
/// Repository-wide Windows console-window guard. A console-subsystem child
/// started by a service without a console receives a visible console unless
/// <see cref="System.Diagnostics.ProcessStartInfo.CreateNoWindow"/> is set.
/// Redirecting the standard streams does not change that Windows behavior.
/// </summary>
public sealed partial class ProcessStartGuardTests
{
    private static readonly string[] ScannedRoots =
    [
        "backend",
        "runner",
        "task-server",
        "update-service",
        "retention",
        "setup",
        "orchestrator-engine",
        "studio-bff",
    ];

    /// <summary>
    /// Explicit exceptions for start-info factories hardened by their shared
    /// caller. Keys are file:line so an unrelated new spawn cannot inherit an
    /// exception merely by landing in the same file.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, Allowance> Allowlist =
        new Dictionary<string, Allowance>(StringComparer.Ordinal)
        {
            ["backend/Features/Cli/Execution/BuiltInCliBehaviors.cs:137"] =
                SharedCliHardeningAllowance(),
            ["backend/Features/Cli/Execution/BuiltInCliBehaviors.cs:757"] =
                SharedCliHardeningAllowance(),
            ["backend/Features/Cli/Execution/BuiltInCliBehaviors.cs:1256"] =
                SharedCliHardeningAllowance(),
        };

    private static Allowance SharedCliHardeningAllowance()
        => new(
            "GenericCliExecutionService sets CreateNoWindow on every behavior-built ProcessStartInfo before it delegates to a spawner.",
            "backend/Features/Cli/Execution/CliExecutionServiceBase.cs",
            @"\bpsi\.CreateNoWindow\s*=\s*true\b");

    [Fact]
    public void Every_process_start_is_hidden_on_windows()
    {
        var sites = ScanRepository();
        var violations = sites
            .Where(site => !site.Guarded && !Allowlist.ContainsKey(site.Location))
            .Select(site => $"{site.Location} ({site.Kind})")
            .ToList();

        Assert.True(
            violations.Count == 0,
            "Every ProcessStartInfo must set CreateNoWindow = true in its initializer, "
            + "or be listed with a narrow reason when a shared caller applies the guard. "
            + "Every Process object must receive StartInfo before Start. Unguarded sites:\n  "
            + string.Join("\n  ", violations));
    }

    [Fact]
    public void Guard_rejects_an_unguarded_start_info()
    {
        const string source = """
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("git")
                {
                    UseShellExecute = false,
                    RedirectStandardError = true,
                },
            };
            process.Start();
            """;

        var sites = ScanSource("backend/Example.cs", source);

        Assert.Contains(sites, site => site.Kind == "ProcessStartInfo" && !site.Guarded);
    }

    [Fact]
    public void Guard_accepts_a_hidden_start_info_and_assigned_process()
    {
        const string source = """
            var startInfo = new ProcessStartInfo("git")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = new Process();
            process.StartInfo = startInfo;
            process.Start();
            """;

        Assert.All(ScanSource("backend/Example.cs", source), site => Assert.True(site.Guarded));
    }

    [Fact]
    public void Every_allowlist_entry_has_a_reason_and_still_exists()
    {
        Assert.All(Allowlist, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Value.Reason)));
        var locations = ScanRepository().Select(site => site.Location).ToHashSet(StringComparer.Ordinal);
        var stale = Allowlist.Keys.Where(location => !locations.Contains(location)).ToList();

        Assert.True(
            stale.Count == 0,
            "Delete or update stale process-spawn allowlist entries:\n  " + string.Join("\n  ", stale));

        var root = RepoRoot();
        Assert.All(Allowlist, entry =>
        {
            var helperPath = Path.Combine(root, entry.Value.GuardFile.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(
                File.Exists(helperPath)
                && Regex.IsMatch(File.ReadAllText(helperPath), entry.Value.GuardPattern),
                $"Allowlisted process spawn {entry.Key} lost its shared CreateNoWindow enforcement in {entry.Value.GuardFile}.");
        });
    }

    private sealed record Allowance(string Reason, string GuardFile, string GuardPattern);

    private sealed record SpawnSite(string File, int Line, string Kind, bool Guarded)
    {
        public string Location => $"{File}:{Line}";
    }

    private static IReadOnlyList<SpawnSite> ScanRepository()
    {
        var root = RepoRoot();
        var sites = new List<SpawnSite>();
        foreach (var relativeRoot in ScannedRoots)
        {
            var absoluteRoot = Path.Combine(root, relativeRoot);
            if (!Directory.Exists(absoluteRoot)) continue;
            foreach (var file in Directory.EnumerateFiles(absoluteRoot, "*.cs", SearchOption.AllDirectories)
                         .Where(file => !IsExcluded(file))
                         .OrderBy(file => file, StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                sites.AddRange(ScanSource(relative, File.ReadAllText(file)));
            }
        }
        return sites;
    }

    private static IReadOnlyList<SpawnSite> ScanSource(string relativePath, string source)
    {
        var code = MaskCommentsAndLiterals(source.Replace("\r\n", "\n"));
        var sites = new List<SpawnSite>();

        foreach (Match match in ProcessStartInfoCreation().Matches(code))
        {
            var initializer = FindObjectInitializer(code, match.Index + match.Length);
            var guarded = initializer is { } range
                          && CreateNoWindowTrue().IsMatch(code[range.Start..range.End]);
            sites.Add(new SpawnSite(relativePath, LineAt(code, match.Index), "ProcessStartInfo", guarded));
        }

        foreach (Match match in ProcessCreation().Matches(code))
        {
            var initializer = FindObjectInitializer(code, match.Index + match.Length);
            var guarded = initializer is { } range
                ? StartInfoAssignment().IsMatch(code[range.Start..range.End])
                : HasImmediateStartInfoAssignment(code, match.Index + match.Length);
            sites.Add(new SpawnSite(relativePath, LineAt(code, match.Index), "Process", guarded));
        }

        return sites;
    }

    private static (int Start, int End)? FindObjectInitializer(string code, int from)
    {
        var index = SkipWhitespace(code, from);
        if (index < code.Length && code[index] == '(')
        {
            var close = FindBalancedEnd(code, index, '(', ')');
            if (close < 0) return null;
            index = SkipWhitespace(code, close + 1);
        }
        if (index >= code.Length || code[index] != '{') return null;
        var end = FindBalancedEnd(code, index, '{', '}');
        return end < 0 ? null : (index, end + 1);
    }

    private static bool HasImmediateStartInfoAssignment(string code, int from)
    {
        var statementEnd = code.IndexOf(';', from);
        if (statementEnd < 0) return false;
        var nextStart = statementEnd + 1;
        var nextEnd = code.IndexOf(';', nextStart);
        if (nextEnd < 0) return false;
        return StartInfoMemberAssignment().IsMatch(code[nextStart..(nextEnd + 1)]);
    }

    private static int FindBalancedEnd(string code, int open, char opening, char closing)
    {
        var depth = 0;
        for (var index = open; index < code.Length; index++)
        {
            if (code[index] == opening) depth++;
            else if (code[index] == closing && --depth == 0) return index;
        }
        return -1;
    }

    private static int SkipWhitespace(string code, int from)
    {
        while (from < code.Length && char.IsWhiteSpace(code[from])) from++;
        return from;
    }

    private static int LineAt(string code, int index)
        => code.AsSpan(0, index).Count('\n') + 1;

    private static bool IsExcluded(string file)
    {
        var segments = file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment => segment is "bin" or "obj" || segment.EndsWith(".Tests", StringComparison.Ordinal));
    }

    /// <summary>
    /// Replaces comments and literals with spaces while preserving offsets and
    /// line breaks. This keeps examples in comments from becoming spawn sites.
    /// </summary>
    private static string MaskCommentsAndLiterals(string source)
    {
        var chars = source.ToCharArray();
        for (var index = 0; index < chars.Length;)
        {
            if (index + 1 < chars.Length && chars[index] == '/' && chars[index + 1] == '/')
            {
                var end = source.IndexOf('\n', index + 2);
                Mask(chars, index, end < 0 ? chars.Length : end);
                index = end < 0 ? chars.Length : end;
                continue;
            }
            if (index + 1 < chars.Length && chars[index] == '/' && chars[index + 1] == '*')
            {
                var end = source.IndexOf("*/", index + 2, StringComparison.Ordinal);
                end = end < 0 ? chars.Length : end + 2;
                Mask(chars, index, end);
                index = end;
                continue;
            }
            if (chars[index] is '"' or '\'')
            {
                var quote = chars[index];
                var verbatim = quote == '"' && index > 0 && chars[index - 1] == '@';
                var end = index + 1;
                while (end < chars.Length)
                {
                    if (chars[end] == '\n' && quote == '\'' && !verbatim) break;
                    if (!verbatim && chars[end] == '\\')
                    {
                        end += 2;
                        continue;
                    }
                    if (chars[end] == quote)
                    {
                        if (verbatim && end + 1 < chars.Length && chars[end + 1] == quote)
                        {
                            end += 2;
                            continue;
                        }
                        end++;
                        break;
                    }
                    end++;
                }
                Mask(chars, index, Math.Min(end, chars.Length));
                index = end;
                continue;
            }
            index++;
        }
        return new string(chars);
    }

    private static void Mask(char[] chars, int start, int end)
    {
        for (var index = start; index < end; index++)
            if (chars[index] != '\n') chars[index] = ' ';
    }

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

    [GeneratedRegex(@"\bnew\s+(?:(?:global::)?System\.Diagnostics\.)?ProcessStartInfo\b")]
    private static partial Regex ProcessStartInfoCreation();

    [GeneratedRegex(@"\bnew\s+(?:(?:global::)?System\.Diagnostics\.)?Process\b(?!StartInfo)")]
    private static partial Regex ProcessCreation();

    [GeneratedRegex(@"\bCreateNoWindow\s*=\s*true\b")]
    private static partial Regex CreateNoWindowTrue();

    [GeneratedRegex(@"\bStartInfo\s*=")]
    private static partial Regex StartInfoAssignment();

    [GeneratedRegex(@"\.\s*StartInfo\s*=")]
    private static partial Regex StartInfoMemberAssignment();
}
