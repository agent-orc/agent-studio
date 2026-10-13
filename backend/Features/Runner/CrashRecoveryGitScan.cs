using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.Runner;

internal sealed record CrashRecoveryGitFinding(
    bool RefDrift, string Branch, string HeadSha, int BehindCommits,
    IReadOnlyList<string> UnknownBlobs, int Insertions, int Deletions,
    string? IndexLock, string ContentFingerprint);

/// <summary>Read-only worktree evidence used before offering a recovery commit.</summary>
internal static class CrashRecoveryGitScan
{
    public static IReadOnlyList<string> ExpandUntrackedDirectories(string root, IReadOnlyList<string> files)
    {
        var directories = files.Where(file => Directory.Exists(Path.Combine(root, file))).ToArray();
        if (directories.Length == 0) return files;
        var untracked = Git(root, "ls-files", "--others", "--exclude-standard", "-z").Output
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        return files.Where(file => !directories.Contains(file, StringComparer.Ordinal)
                || !untracked.Any(candidate => candidate.StartsWith(file.TrimEnd('/') + "/", StringComparison.Ordinal)))
            .Concat(untracked.Where(file => directories.Any(dir =>
                file.StartsWith(dir.TrimEnd('/') + "/", StringComparison.Ordinal))))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(file => file, StringComparer.Ordinal)
            .ToArray();
    }

    public static CrashRecoveryGitFinding Inspect(string root, IReadOnlyList<string> files, bool includeBlobs = true)
    {
        var branch = Git(root, "symbolic-ref", "--quiet", "--short", "HEAD").Output.Trim();
        var head = Git(root, "rev-parse", "HEAD").Output.Trim();
        var gitDir = Git(root, "rev-parse", "--absolute-git-dir").Output.Trim();
        var commonDirValue = Git(root, "rev-parse", "--git-common-dir").Output.Trim();
        var commonDir = Path.GetFullPath(Path.IsPathRooted(commonDirValue)
            ? commonDirValue : Path.Combine(root, commonDirValue));
        var lockPath = Path.Combine(gitDir, "index.lock");
        string? indexLock = null;
        if (File.Exists(lockPath))
        {
            var info = new FileInfo(lockPath);
            indexLock = $".git/index.lock: {info.Length} bytes, {Math.Max(0, (int)(DateTime.UtcNow - info.LastWriteTimeUtc).TotalSeconds)} seconds old";
        }

        var drift = false;
        var behind = 0;
        if (branch.Length > 0)
        {
            var headLog = LastReflog(Path.Combine(gitDir, "logs", "HEAD"));
            var branchLog = LastReflog(Path.Combine(commonDir, "logs", "refs", "heads", branch.Replace('/', Path.DirectorySeparatorChar)));
            if (headLog != null && branchLog != null && branchLog.Value.Sha == head
                && branchLog.Value.Sha != headLog.Value.Sha
                && branchLog.Value.At >= headLog.Value.At)
            {
                drift = true;
                behind = CommitCount(root, headLog.Value.Sha, head);
            }
        }

        // write-tree leaves the real index untouched. With a lock, copy it to
        // a temporary index so this probe can still run without touching the lock.
        string? tempIndex = null;
        try
        {
            if (indexLock != null && File.Exists(Path.Combine(gitDir, "index")))
            {
                tempIndex = Path.Combine(Path.GetTempPath(), "crash-recovery-index-" + Guid.NewGuid().ToString("N"));
                File.Copy(Path.Combine(gitDir, "index"), tempIndex);
            }
            var indexTree = GitWithIndex(root, tempIndex, "write-tree").Output.Trim();
            var headTree = Git(root, "rev-parse", "HEAD^{tree}").Output.Trim();
            if (indexTree.Length > 0 && indexTree != headTree && !drift)
            {
                var trees = Git(root, "log", "-n", "2000", "--format=%H %T", "HEAD").Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in trees.Skip(1))
                {
                    var parts = line.Trim().Split(' ', 2);
                    if (parts.Length != 2 || parts[1] != indexTree) continue;
                    drift = true;
                    behind = CommitCount(root, parts[0], head);
                    break;
                }
                if (!drift)
                {
                    // An index containing a few additional staged edits need
                    // not have exactly the old tree. Match the staged reversal
                    // by path and line counts against recent ancestor trees.
                    var staged = Numstat(GitWithIndex(root, tempIndex, "diff", "--cached", "--numstat", "HEAD").Output);
                    var total = staged.Values.Sum(v => v.Added + v.Deleted);
                    if (total >= 10)
                    {
                        foreach (var line in trees.Skip(1).Take(20))
                        {
                            var sha = line.Split(' ', 2)[0];
                            var reverse = Numstat(Git(root, "diff", "--numstat", "HEAD", sha).Output);
                            var matched = staged.Sum(kv => reverse.TryGetValue(kv.Key, out var value) && value == kv.Value
                                ? kv.Value.Added + kv.Value.Deleted : 0);
                            if (matched * 5 < total * 4) continue;
                            drift = true;
                            behind = CommitCount(root, sha, head);
                            break;
                        }
                    }
                }
            }
        }
        finally
        {
            if (tempIndex != null) File.Delete(tempIndex);
        }

        var unknown = new List<string>();
        var candidates = new List<string>();
        var blobIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in includeBlobs ? files : [])
        {
            var full = Path.GetFullPath(Path.Combine(root, file));
            if (!full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException($"Recovery file escapes the worktree: {file}");
            if (!File.Exists(full))
            {
                blobIds[file] = "deleted";
                continue;
            }
            if (full.Contains('\n') || full.Contains('\r'))
            {
                var hash = Git(root, "hash-object", "--", full);
                if (hash.Code != 0) throw new IOException($"Cannot hash recovery file: {file}");
                blobIds[file] = hash.Output.Trim();
                if (Git(root, "cat-file", "-e", blobIds[file] + "^{blob}").Code != 0)
                    unknown.Add(file);
                continue;
            }
            candidates.Add(file);
        }
        if (candidates.Count > 0)
        {
            try
            {
                var paths = candidates.Select(file => Path.GetFullPath(Path.Combine(root, file))).ToArray();
                var hashes = GitInput(root, string.Join('\n', paths) + "\n", "hash-object", "--stdin-paths").Output
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(hash => hash.Trim()).ToArray();
                if (hashes.Length == paths.Length)
                {
                    for (var i = 0; i < hashes.Length; i++) blobIds[candidates[i]] = hashes[i];
                    var objects = GitInput(root, string.Join('\n', hashes) + "\n", "cat-file", "--batch-check").Output
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    if (objects.Length == hashes.Length)
                    {
                        for (var i = 0; i < objects.Length; i++)
                        {
                            if (!objects[i].Contains(" blob ", StringComparison.Ordinal)) unknown.Add(candidates[i]);
                        }
                    }
                    else CheckIndividually();
                }
                else CheckIndividually();
            }
            catch (IOException) { CheckIndividually(); }

            void CheckIndividually()
            {
                foreach (var file in candidates)
                {
                    var hash = Git(root, "hash-object", "--", Path.Combine(root, file));
                    if (hash.Code != 0) throw new IOException($"Cannot hash recovery file: {file}");
                    blobIds[file] = hash.Output.Trim();
                    if (Git(root, "cat-file", "-e", blobIds[file] + "^{blob}").Code != 0)
                        unknown.Add(file);
                }
            }
        }

        var fingerprintLines = files.OrderBy(file => file, StringComparer.Ordinal)
            .Select(file => file + "\t" + blobIds[file]);
        var fingerprint = includeBlobs
            ? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', fingerprintLines))))
            : "";

        var added = 0;
        var deleted = 0;
        foreach (var line in Git(root, "diff", "--numstat", "HEAD").Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 2) continue;
            if (int.TryParse(parts[0], out var a)) added += a;
            if (int.TryParse(parts[1], out var d)) deleted += d;
        }
        var untracked = Git(root, "ls-files", "--others", "--exclude-standard", "-z").Output
            .Split('\0', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        foreach (var file in files.Where(untracked.Contains))
        {
            var path = Path.Combine(root, file);
            try
            {
                if (File.Exists(path)) added += File.ReadLines(path).Count();
            }
            catch (IOException ex) { SilentCatch.Note(ex, "Crash recovery: file changed during numstat scan."); }
            catch (UnauthorizedAccessException ex) { SilentCatch.Note(ex, "Crash recovery: file became unreadable during numstat scan."); }
        }
        return new(drift, branch, head, behind, unknown, added, deleted, indexLock, fingerprint);
    }

    private static (string Sha, long At)? LastReflog(string path)
    {
        if (!File.Exists(path)) return null;
        var line = File.ReadLines(path).LastOrDefault();
        if (line == null) return null;
        var tab = line.IndexOf('\t');
        var fields = (tab < 0 ? line : line[..tab]).Split(' ');
        if (fields.Length < 4 || !long.TryParse(fields[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var at)) return null;
        return (fields[1], at);
    }

    private static int CommitCount(string root, string ancestor, string head)
    {
        if (Git(root, "merge-base", "--is-ancestor", ancestor, head).Code != 0) return 0;
        return int.TryParse(Git(root, "rev-list", "--count", ancestor + ".." + head).Output.Trim(), out var count) ? count : 0;
    }

    private static Dictionary<string, (int Added, int Deleted)> Numstat(string output)
    {
        var result = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.TrimEnd('\r').Split('\t', 3);
            if (fields.Length == 3 && int.TryParse(fields[0], out var added) && int.TryParse(fields[1], out var deleted))
                result[fields[2]] = (added, deleted);
        }
        return result;
    }

    private static (string Output, int Code) Git(string root, params string[] args) => GitWithIndex(root, null, args);

    private static (string Output, int Code) GitWithIndex(string root, string? index, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        if (index != null) psi.Environment["GIT_INDEX_FILE"] = index;
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (output, process.ExitCode);
    }

    private static (string Output, int Code) GitInput(string root, string input, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = root, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var writer = Task.Run(async () =>
        {
            await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
        });
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Task.WhenAll(writer, output, error).GetAwaiter().GetResult();
        process.WaitForExit();
        return (output.Result, process.ExitCode);
    }
}
