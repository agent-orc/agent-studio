using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.Git;

public static class CommitGateDecisions
{
    public const string Allow = "allow";
    public const string Warn = "warn";
    public const string Block = "block";
}

public static class CommitGateSeverities
{
    /// <summary>
    /// Recorded for the operator, never for the decision. An informational
    /// finding leaves <see cref="CommitGateResult.CanCommit"/> and the decision
    /// untouched, so the gate can explain WHY a candidate needed no manual
    /// review (AGT-2828: declared evidence assets) without turning the
    /// explanation into a pause.
    /// </summary>
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Block = "block";
}

public sealed record CommitGateFinding(
    string Code,
    string Severity,
    string Path,
    string Message,
    string Scanner,
    bool ExcludesCandidate = false);

public sealed record CommitCandidateManifestEntry(
    string Path,
    string Status,
    long Size,
    string? Sha256,
    string? GitBlobOid,
    bool Binary,
    bool Included,
    string? ExclusionReason = null);

public sealed record CommitGateProvenance(
    string Operation,
    string ProjectId,
    string? TaskId,
    string? RunnerId,
    string RepositoryRoot,
    string? Branch,
    DateTime InspectedAtUtc);

public sealed record CommitGateResult(
    string Decision,
    bool CanCommit,
    CommitGateProvenance Provenance,
    IReadOnlyList<CommitCandidateManifestEntry> Candidates,
    IReadOnlyList<CommitGateFinding> Findings,
    IReadOnlyList<string> ScannerSources,
    string? EvidencePath = null)
{
    public IReadOnlyList<string> IncludedPaths => Candidates.Where(c => c.Included).Select(c => c.Path).ToArray();
}

public sealed record CommitGateRequest(
    string Operation,
    string ProjectId,
    string RepositoryRoot,
    string? TaskId,
    string? RunnerId,
    IReadOnlyCollection<string>? ExpectedPaths = null,
    bool RequireTaskWorktree = false,
    string? ExpectedBranch = null,
    bool ExplicitlyReviewed = false,
    string? EvidenceDirectory = null,
    bool RequireExplicitPaths = false,
    IReadOnlyCollection<string>? EvidenceAssetPaths = null);

public sealed class CommitBoundIndex : IDisposable
{
    public string FilePath { get; }

    public CommitBoundIndex(string filePath) => FilePath = filePath;

    public void Dispose()
    {
        try { File.Delete(FilePath); }
        catch (Exception ex) { SilentCatch.Note(ex, "Temporary commit index cleanup failed."); }
    }
}

/// <summary>
/// Scanner seam for Quality Studio, Gitleaks, or another candidate-content
/// scanner. Implementations receive bytes only in memory and must return safe
/// metadata. Findings must never contain the matched value.
/// </summary>
public interface ICommitCandidateScanner
{
    string Name { get; }
    IReadOnlyList<CommitGateFinding> Scan(string repositoryRoot, string relativePath, ReadOnlyMemory<byte> content, bool binary);
}

public sealed class BuiltInCommitCandidateScanner : ICommitCandidateScanner
{
    private static readonly Regex PrivateKey = new(
        @"-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HighConfidenceToken = new(
        @"(?<![A-Za-z0-9])(?:AKIA[0-9A-Z]{16}|ghp_[A-Za-z0-9]{36}|github_pat_[A-Za-z0-9_]{50,}|sk-(?:proj-)?[A-Za-z0-9_-]{32,})(?![A-Za-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    public string Name => "built-in";

    public IReadOnlyList<CommitGateFinding> Scan(
        string repositoryRoot,
        string relativePath,
        ReadOnlyMemory<byte> content,
        bool binary)
    {
        if (binary || content.IsEmpty) return [];
        var text = Encoding.UTF8.GetString(content.Span);
        var findings = new List<CommitGateFinding>();

        if (PrivateKey.IsMatch(text))
        {
            findings.Add(new CommitGateFinding(
                "private-key-material", CommitGateSeverities.Block, relativePath,
                "Private-key material detected. The matched value was redacted.", Name));
        }

        foreach (Match match in HighConfidenceToken.Matches(text))
        {
            if (LooksLikePlaceholderToken(match.Value)) continue;
            findings.Add(new CommitGateFinding(
                "high-confidence-token", CommitGateSeverities.Block, relativePath,
                "High-confidence credential signature detected. The matched value was redacted.", Name));
            break;
        }
        return findings;
    }

    private static bool LooksLikePlaceholderToken(string value)
    {
        var payload = value[(value.LastIndexOf('_') + 1)..]
            .Replace("proj-", "", StringComparison.OrdinalIgnoreCase)
            .Replace("sk-", "", StringComparison.OrdinalIgnoreCase);
        return payload.Contains("placeholder", StringComparison.OrdinalIgnoreCase)
            || payload.Contains("example", StringComparison.OrdinalIgnoreCase)
            || payload.Contains("redacted", StringComparison.OrdinalIgnoreCase)
            || payload.Distinct().Count() <= 3;
    }
}

/// <summary>
/// Deterministic pre-stage gate. It inventories the complete dirty set, scans
/// the exact working-tree bytes, applies policy, and returns an immutable
/// manifest that the commit operation must bind to.
/// </summary>
public sealed class CommitCandidateGate
{
    internal const long OversizedBytes = 5 * 1024 * 1024;
    private readonly IReadOnlyList<ICommitCandidateScanner> _scanners;

    public CommitCandidateGate(ILogger logger, IEnumerable<ICommitCandidateScanner>? scanners = null)
    {
        var supplied = scanners?.ToArray() ?? [];
        // External scanners are additive. The deterministic built-in scanner
        // always runs, so Quality Studio/Gitleaks absence or adapter failure
        // can never turn into an unscanned false pass.
        _scanners = new ICommitCandidateScanner[] { new BuiltInCommitCandidateScanner() }
            .Concat(supplied.Where(s => s is not BuiltInCommitCandidateScanner))
            .ToArray();
    }

    public CommitGateResult Inspect(CommitGateRequest request)
    {
        var root = Path.GetFullPath(request.RepositoryRoot);
        var branch = Git(root, "rev-parse", "--abbrev-ref", "HEAD").Output.Trim();
        var provenance = new CommitGateProvenance(
            request.Operation, request.ProjectId, request.TaskId, request.RunnerId,
            root, string.IsNullOrWhiteSpace(branch) ? null : branch, DateTime.UtcNow);
        var findings = new List<CommitGateFinding>();

        if (request.RequireExplicitPaths && request.ExpectedPaths is not { Count: > 0 })
        {
            findings.Add(new CommitGateFinding(
                "explicit-pathspec-required", CommitGateSeverities.Block, ".",
                "Direct develop or legacy commit paths require an explicit task-owned path set.", "policy"));
        }

        if (request.RequireTaskWorktree)
            ValidateWorktree(request, root, branch, findings);

        var dirty = EnumerateDirty(root);
        var expected = request.ExpectedPaths is { Count: > 0 }
            ? request.ExpectedPaths.Select(NormalizePath).ToHashSet(StringComparer.Ordinal)
            : null;
        var snapshots = new List<(CommitCandidateManifestEntry Entry, byte[] Bytes)>();

        foreach (var (path, status) in dirty.OrderBy(x => x.Path, StringComparer.Ordinal))
        {
            var normalized = NormalizePath(path);
            var included = expected == null || expected.Contains(normalized);
            string? exclusion = included ? null : "outside-expected-task-scope";
            if (!included)
            {
                findings.Add(new CommitGateFinding(
                    "outside-expected-task-scope", CommitGateSeverities.Warning, normalized,
                    "Dirty path is outside the task-owned path set and will not be committed.",
                    "policy", ExcludesCandidate: true));
            }

            var full = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsContained(root, full))
            {
                findings.Add(new CommitGateFinding(
                    "path-escapes-repository", CommitGateSeverities.Block, normalized,
                    "Candidate path escapes the repository root.", "policy"));
                snapshots.Add((new CommitCandidateManifestEntry(normalized, status, 0, null, null, false, false,
                    "path-escapes-repository"), []));
                continue;
            }

            byte[] bytes = [];
            long size = 0;
            string? sha256 = null;
            string? blobOid = null;
            var binary = false;
            if (File.Exists(full))
            {
                try
                {
                    var attrs = File.GetAttributes(full);
                    if ((attrs & FileAttributes.ReparsePoint) != 0)
                    {
                        findings.Add(new CommitGateFinding(
                            "symbolic-link-surprise", CommitGateSeverities.Warning, normalized,
                            "Symbolic-link candidate requires explicit review.", "policy"));
                    }
                    size = new FileInfo(full).Length;
                    if (size <= OversizedBytes)
                    {
                        bytes = File.ReadAllBytes(full);
                        sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                    }
                    else
                    {
                        using (var hashStream = File.OpenRead(full))
                            sha256 = Convert.ToHexString(SHA256.HashData(hashStream)).ToLowerInvariant();
                        bytes = new byte[Math.Min(size, 1024 * 1024)];
                        using var prefixStream = File.OpenRead(full);
                        var read = prefixStream.Read(bytes, 0, bytes.Length);
                        if (read != bytes.Length) Array.Resize(ref bytes, read);
                    }
                    binary = bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).IndexOf((byte)0) >= 0;
                    blobOid = Git(root, "hash-object", "--", normalized).Output.Trim();
                }
                catch (Exception ex)
                {
                    findings.Add(new CommitGateFinding(
                        "candidate-unreadable", CommitGateSeverities.Block, normalized,
                        $"Candidate content could not be inspected ({ex.GetType().Name}).", "policy"));
                }
            }

            if (IsRootScratch(normalized))
            {
                included = false;
                exclusion = "root-scratch-artifact";
                findings.Add(new CommitGateFinding(
                    "root-scratch-artifact", CommitGateSeverities.Warning, normalized,
                    "Root scratch/debug artifact was excluded by deterministic policy.",
                    "policy", ExcludesCandidate: true));
            }
            if (IsCredentialHome(normalized))
            {
                included = false;
                exclusion = "credential-or-config-home";
                findings.Add(new CommitGateFinding(
                    "credential-or-config-home", CommitGateSeverities.Warning, normalized,
                    "Credential/config-home path was excluded and requires explicit review.",
                    "policy", ExcludesCandidate: true));
            }
            if (size > OversizedBytes)
            {
                findings.Add(new CommitGateFinding(
                    "oversized-surprise", CommitGateSeverities.Warning, normalized,
                    $"Candidate is unexpectedly large ({size} bytes).", "policy"));
            }
            if (binary)
            {
                // AGT-2828: a screenshot the task was told to produce, under a
                // path the project declared for assets, is the deliverable, not
                // a surprise. Everything outside that narrow rule keeps the
                // manual-review pause.
                var asset = EvidenceAssetPolicy.Decide(normalized, size, request.EvidenceAssetPaths);
                findings.Add(asset.Admitted
                    ? new CommitGateFinding(
                        EvidenceAssetCodes.Admitted, CommitGateSeverities.Info, normalized,
                        asset.Message, "policy")
                    : new CommitGateFinding(
                        "binary-surprise", CommitGateSeverities.Warning, normalized,
                        $"Binary candidate requires explicit review ({asset.Code}).", "policy"));
            }

            foreach (var scanner in _scanners)
            {
                try
                {
                    findings.AddRange(scanner.Scan(root, normalized, bytes, binary).Select(f =>
                        f with
                        {
                            Message = $"Scanner {scanner.Name} reported {f.Code}. Matched values are redacted.",
                            Scanner = scanner.Name,
                            Path = normalized
                        }));
                }
                catch (Exception ex)
                {
                    findings.Add(new CommitGateFinding(
                        "scanner-failed", CommitGateSeverities.Warning, normalized,
                        $"Scanner {scanner.Name} failed safely ({ex.GetType().Name}).", scanner.Name));
                }
            }

            snapshots.Add((new CommitCandidateManifestEntry(
                normalized, status, size, sha256, blobOid, binary, included, exclusion), bytes));
        }

        var hasBlock = findings.Any(f => f.Severity == CommitGateSeverities.Block);
        var unresolvedWarnings = findings.Any(f =>
            f.Severity == CommitGateSeverities.Warning && !f.ExcludesCandidate);
        var hasWarning = findings.Any(f => f.Severity == CommitGateSeverities.Warning);
        var canCommit = !hasBlock && (!unresolvedWarnings || request.ExplicitlyReviewed);
        var decision = hasBlock ? CommitGateDecisions.Block : hasWarning ? CommitGateDecisions.Warn : CommitGateDecisions.Allow;
        var result = new CommitGateResult(
            decision, canCommit, provenance, snapshots.Select(s => s.Entry).ToArray(), findings,
            _scanners.Select(s => s.Name).Distinct(StringComparer.Ordinal).ToArray());
        return PersistEvidence(result, request.EvidenceDirectory);
    }

    public bool VerifyUnchangedAndStage(CommitGateResult gate, out string? error)
    {
        error = null;
        if (!gate.CanCommit)
        {
            error = $"Commit candidate gate returned {gate.Decision}.";
            return false;
        }
        var included = gate.Candidates.Where(c => c.Included).ToArray();
        if (included.Length == 0)
        {
            error = "Nothing to commit after candidate policy exclusions.";
            return false;
        }

        var root = gate.Provenance.RepositoryRoot;
        foreach (var candidate in included)
        {
            var full = Path.Combine(root, candidate.Path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full))
            {
                using var stream = File.OpenRead(full);
                var current = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                if (!string.Equals(current, candidate.Sha256, StringComparison.Ordinal))
                {
                    error = $"Candidate changed after inspection: {candidate.Path}";
                    return false;
                }
            }
            else if (candidate.Sha256 != null)
            {
                error = $"Candidate disappeared after inspection: {candidate.Path}";
                return false;
            }
        }

        var add = new List<string> { "add", "-A", "--" };
        add.AddRange(included.Select(c => c.Path));
        var staged = Git(root, add.ToArray());
        if (staged.Code != 0)
        {
            error = $"git add failed: {staged.Error.Trim()}";
            return false;
        }

        foreach (var candidate in included.Where(c => c.GitBlobOid != null))
        {
            var indexed = Git(root, "ls-files", "-s", "--", candidate.Path).Output.Trim();
            var parts = indexed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !string.Equals(parts[1], candidate.GitBlobOid, StringComparison.Ordinal))
            {
                Unstage(root, included.Select(c => c.Path));
                error = $"Staged content did not match inspected manifest: {candidate.Path}";
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Builds a private index from HEAD plus only the inspected manifest paths.
    /// The caller can write and commit this index without re-reading the working
    /// tree or inheriting unrelated entries from the user's real index.
    /// </summary>
    public bool TryPrepareBoundIndex(
        CommitGateResult gate,
        out CommitBoundIndex? boundIndex,
        out string? error)
    {
        boundIndex = null;
        error = null;
        if (!gate.CanCommit)
        {
            error = $"Commit candidate gate returned {gate.Decision}.";
            return false;
        }

        var included = gate.Candidates.Where(c => c.Included).ToArray();
        if (included.Length == 0)
        {
            error = "Nothing to commit after candidate policy exclusions.";
            return false;
        }

        var root = gate.Provenance.RepositoryRoot;
        foreach (var candidate in included)
        {
            var full = Path.Combine(root, candidate.Path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full))
            {
                using var stream = File.OpenRead(full);
                var current = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                if (!string.Equals(current, candidate.Sha256, StringComparison.Ordinal))
                {
                    error = $"Candidate changed after inspection: {candidate.Path}";
                    return false;
                }
            }
            else if (candidate.Sha256 != null)
            {
                error = $"Candidate disappeared after inspection: {candidate.Path}";
                return false;
            }
        }

        var indexPath = Path.Combine(
            Path.GetTempPath(), $"agent-studio-commit-index-{Guid.NewGuid():N}");
        try
        {
            var readTree = GitWithIndex(root, indexPath, "read-tree", "HEAD");
            if (readTree.Code != 0)
            {
                error = $"Could not initialize manifest-bound index: {readTree.Error.Trim()}";
                return false;
            }

            var add = new List<string> { "add", "-A", "--" };
            add.AddRange(included.Select(c => c.Path));
            var staged = GitWithIndex(root, indexPath, add.ToArray());
            if (staged.Code != 0)
            {
                error = $"Could not populate manifest-bound index: {staged.Error.Trim()}";
                return false;
            }

            foreach (var candidate in included)
            {
                var indexed = GitWithIndex(root, indexPath, "ls-files", "-s", "--", candidate.Path);
                if (candidate.GitBlobOid == null)
                {
                    if (!string.IsNullOrWhiteSpace(indexed.Output))
                    {
                        error = $"Deleted candidate reappeared after inspection: {candidate.Path}";
                        return false;
                    }
                    continue;
                }

                var parts = indexed.Output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !string.Equals(parts[1], candidate.GitBlobOid, StringComparison.Ordinal))
                {
                    error = $"Bound index content did not match inspected manifest: {candidate.Path}";
                    return false;
                }
            }

            boundIndex = new CommitBoundIndex(indexPath);
            indexPath = "";
            return true;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(indexPath))
            {
                try { File.Delete(indexPath); }
                catch (Exception ex) { SilentCatch.Note(ex, "Failed commit index cleanup."); }
            }
        }
    }

    private static void ValidateWorktree(
        CommitGateRequest request, string root, string branch, List<CommitGateFinding> findings)
    {
        var common = Git(root, "rev-parse", "--git-common-dir").Output.Trim();
        var gitDir = Git(root, "rev-parse", "--git-dir").Output.Trim();
        if (string.IsNullOrWhiteSpace(common) || string.IsNullOrWhiteSpace(gitDir)
            || PathsEqual(Path.GetFullPath(Path.Combine(root, common)), Path.GetFullPath(Path.Combine(root, gitDir))))
        {
            findings.Add(new CommitGateFinding(
                "not-isolated-task-worktree", CommitGateSeverities.Block, ".",
                "Add-all is permitted only in an owned linked task worktree.", "policy"));
        }
        if (string.IsNullOrWhiteSpace(request.ExpectedBranch))
        {
            findings.Add(new CommitGateFinding(
                "expected-worktree-branch-required", CommitGateSeverities.Block, ".",
                "An explicit task branch is required to validate worktree ownership.", "policy"));
        }
        else if (!string.Equals(branch, request.ExpectedBranch, StringComparison.Ordinal))
        {
            findings.Add(new CommitGateFinding(
                "unexpected-worktree-branch", CommitGateSeverities.Block, ".",
                $"Worktree branch does not match expected task branch {request.ExpectedBranch}.", "policy"));
        }
    }

    private static IReadOnlyList<(string Path, string Status)> EnumerateDirty(string root)
    {
        var result = Git(root, "status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignored=no");
        if (result.Code != 0) return [];
        var tokens = result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.Length < 4) continue;
            var status = token[..2];
            found[NormalizePath(token[3..])] = status;
            if ((status.Contains('R') || status.Contains('C')) && i + 1 < tokens.Length)
                found[NormalizePath(tokens[++i])] = status;
        }
        return found.Select(kv => (kv.Key, kv.Value)).ToArray();
    }

    private static bool IsRootScratch(string path)
    {
        if (path.Contains('/')) return false;
        var name = path.ToLowerInvariant();
        return name.StartsWith(".tmp-") || name.StartsWith("tmp-")
            || name.StartsWith("debug-") || name.EndsWith(".tmp")
            || name.EndsWith(".bak") || name.EndsWith(".orig")
            || name.EndsWith(".swp") || name == "debug.log";
    }

    private static bool IsCredentialHome(string path)
    {
        var first = path.Split('/', 2)[0];
        return first is ".ssh" or ".aws" or ".azure" or ".gnupg" or ".kube"
            or ".config" or ".docker" || path is ".env" or ".npmrc" or ".pypirc";
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');

    private static bool IsContained(string root, string full)
    {
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static bool PathsEqual(string left, string right) => string.Equals(
        left.TrimEnd(Path.DirectorySeparatorChar), right.TrimEnd(Path.DirectorySeparatorChar),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static CommitGateResult PersistEvidence(CommitGateResult result, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return result;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "commit-candidate-gate.json");
            var persisted = result with { EvidencePath = path };
            var json = JsonSerializer.Serialize(persisted, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            var operation = Regex.Replace(result.Provenance.Operation, "[^A-Za-z0-9_.-]", "-");
            var historyPath = Path.Combine(directory,
                $"commit-candidate-gate-{result.Provenance.InspectedAtUtc:yyyyMMddTHHmmssfffZ}-{operation}.json");
            File.WriteAllText(historyPath, json);
            return persisted;
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "Commit gate evidence persistence failed.");
            return result;
        }
    }

    private static void Unstage(string root, IEnumerable<string> paths)
    {
        var args = new List<string> { "reset", "-q", "HEAD", "--" };
        args.AddRange(paths);
        Git(root, args.ToArray());
    }

    private static (string Output, string Error, int Code) Git(string root, params string[] args)
        => GitCore(root, indexPath: null, args);

    private static (string Output, string Error, int Code) GitWithIndex(
        string root, string indexPath, params string[] args)
        => GitCore(root, indexPath, args);

    private static (string Output, string Error, int Code) GitCore(
        string root, string? indexPath, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git", WorkingDirectory = root, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        if (!string.IsNullOrWhiteSpace(indexPath))
            psi.Environment["GIT_INDEX_FILE"] = indexPath;
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi);
        if (process == null) return ("", "Could not start git.", -1);
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (output, error, process.ExitCode);
    }
}
