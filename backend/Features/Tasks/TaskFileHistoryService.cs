using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace AgentStudio.Tasks;

public sealed class TaskFileHistoryService
{
    private const char UnitSeparator = '\x1f';
    private const char RecordSeparator = '\x1e';
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly TaskScannerService _scanner;
    private readonly GitService _git;
    private readonly ILogger<TaskFileHistoryService> _logger;

    public TaskFileHistoryService(
        TaskScannerService scanner,
        GitService git,
        ILogger<TaskFileHistoryService> logger)
    {
        _scanner = scanner;
        _git = git;
        _logger = logger;
    }

    public TaskFileLookupResult<IReadOnlyList<TaskFileHistoryEntry>> GetHistory(
        string jobId,
        string? watchPath,
        string requestPath,
        string? scope)
    {
        var resolved = ResolveCandidates(jobId, watchPath, requestPath, scope);
        if (!resolved.Success) return TaskFileLookupResult<IReadOnlyList<TaskFileHistoryEntry>>.Fail(resolved.StatusCode, resolved.Error);

        var sw = Stopwatch.StartNew();
        foreach (var candidate in resolved.Candidates)
        {
            if (candidate.GitRoot == null)
            {
                if (resolved.IsExplicitScope)
                    return TaskFileLookupResult<IReadOnlyList<TaskFileHistoryEntry>>.Fail(StatusCodes.Status400BadRequest, "The selected file source is not in a git repository.");
                continue;
            }

            var result = ReadHistory(candidate);
            if (!result.Success)
            {
                if (resolved.IsExplicitScope)
                    return TaskFileLookupResult<IReadOnlyList<TaskFileHistoryEntry>>.Fail(StatusCodes.Status400BadRequest, result.Error ?? "git log failed.");
                continue;
            }

            if (result.Value.Count > 0 || resolved.IsExplicitScope || File.Exists(candidate.LivePath))
            {
                LogSlow(sw, "history", jobId, candidate.Source, candidate.RequestPath);
                return TaskFileLookupResult<IReadOnlyList<TaskFileHistoryEntry>>.Ok(result.Value, candidate.Source);
            }
        }

        LogSlow(sw, "history", jobId, TaskFileSources.Auto, requestPath);
        return TaskFileLookupResult<IReadOnlyList<TaskFileHistoryEntry>>.Ok([], TaskFileSources.Auto);
    }

    public TaskFileLookupResult<TaskFileContent> ReadFile(
        string jobId,
        string? watchPath,
        string requestPath,
        string? at,
        string? scope)
    {
        if (string.IsNullOrWhiteSpace(at))
            return ReadLiveFile(jobId, watchPath, requestPath, scope);

        if (!IsSha(at))
            return TaskFileLookupResult<TaskFileContent>.Fail(StatusCodes.Status400BadRequest, "Invalid commit SHA.");

        var resolved = ResolveCandidates(jobId, watchPath, requestPath, scope);
        if (!resolved.Success) return TaskFileLookupResult<TaskFileContent>.Fail(resolved.StatusCode, resolved.Error);

        var sw = Stopwatch.StartNew();
        foreach (var candidate in resolved.Candidates)
        {
            if (candidate.GitRoot == null)
            {
                if (resolved.IsExplicitScope)
                    return TaskFileLookupResult<TaskFileContent>.Fail(StatusCodes.Status400BadRequest, "The selected file source is not in a git repository.");
                continue;
            }

            var show = RunGit(candidate.GitRoot, "show", $"{at}:{candidate.GitPath}");
            if (show.Code == 0)
            {
                LogSlow(sw, "show", jobId, candidate.Source, candidate.RequestPath);
                return TaskFileLookupResult<TaskFileContent>.Ok(
                    new TaskFileContent(show.Out, ContentTypeFor(candidate.RequestPath), candidate.RequestPath),
                    candidate.Source);
            }

            if (resolved.IsExplicitScope)
            {
                return TaskFileLookupResult<TaskFileContent>.Fail(
                    StatusCodes.Status404NotFound,
                    "File was not found at the requested commit.");
            }
        }

        LogSlow(sw, "show", jobId, TaskFileSources.Auto, requestPath);
        return TaskFileLookupResult<TaskFileContent>.Fail(StatusCodes.Status404NotFound, "File was not found at the requested commit.");
    }

    /// <summary>
    /// Reads historical status.md versions across physical lane-folder moves.
    /// Generic file history follows one path; task Results must also search the
    /// same task folder under every lane because moving a card renames that
    /// portion of its workspace-repository path.
    /// </summary>
    public TaskFileLookupResult<IReadOnlyList<WorkspaceResultFileVersion>> GetWorkspaceResultHistory(
        string jobId,
        string? watchPath)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info is null)
            return TaskFileLookupResult<IReadOnlyList<WorkspaceResultFileVersion>>.Fail(404, "Job not found.");
        var candidate = BuildWorkspaceCandidate(info, "status.md");
        if (candidate?.GitRoot is null)
            return TaskFileLookupResult<IReadOnlyList<WorkspaceResultFileVersion>>.Ok([], TaskFileSources.Workspace);

        var possiblePaths = LanePaths(candidate.GitPath, info.State).Distinct(StringComparer.Ordinal).ToList();
        var args = new List<string> { "log", "--all", "--format=%H", "--" };
        args.AddRange(possiblePaths);
        var log = RunGit(candidate.GitRoot, args.ToArray());
        if (log.Code != 0)
            return TaskFileLookupResult<IReadOnlyList<WorkspaceResultFileVersion>>.Fail(400, log.Err);

        var versions = new List<WorkspaceResultFileVersion>();
        foreach (var sha in log.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var historicalPath = possiblePaths.FirstOrDefault(path =>
                RunGit(candidate.GitRoot, "cat-file", "-e", $"{sha}:{path}").Code == 0);
            if (historicalPath is null) continue;
            var content = RunGit(candidate.GitRoot, "show", $"{sha}:{historicalPath}");
            if (content.Code != 0) continue;
            var commit = RunGit(
                candidate.GitRoot,
                "show",
                "-s",
                "--format=%aI%x1f%an <%ae>%x1f%s%x1f%B",
                sha);
            if (commit.Code != 0) continue;
            var parts = commit.Out.Split(UnitSeparator, 4);
            DateTime? at = DateTimeOffset.TryParse(
                parts.ElementAtOrDefault(0)?.Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto)
                ? dto.UtcDateTime
                : null;
            var body = parts.ElementAtOrDefault(3) ?? string.Empty;
            var runRaw = ReadTrailer(body, "Run-Index");
            int? runIndex = int.TryParse(runRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var run)
                ? run
                : null;
            var historicalFolder = Path.GetDirectoryName(historicalPath)!.Replace('\\', '/');
            var historicalCandidate = candidate with
            {
                GitPath = historicalPath,
                JobFolderPath = Path.Combine(candidate.GitRoot, historicalFolder.Replace('/', Path.DirectorySeparatorChar)),
            };
            var taskJson = ReadFirstAt(
                candidate.GitRoot,
                sha,
                $"{historicalFolder}/task.json",
                $"{historicalFolder}/job.json");
            versions.Add(new WorkspaceResultFileVersion(
                new TaskFileHistoryEntry(
                    sha,
                    at,
                    runIndex,
                    NullIfEmpty(ReadTrailer(body, "Verdict")),
                    NullIfEmpty(parts.ElementAtOrDefault(2)?.Trim()) ?? FirstLine(body),
                    parts.ElementAtOrDefault(1)?.Trim() ?? string.Empty,
                    new TaskFileVersionProvenance(
                        TaskFileSources.Workspace,
                        historicalPath,
                        NullIfEmpty(ReadTrailer(body, "Steps")),
                        ReadGenerationAt(historicalCandidate, sha))),
                content.Out,
                taskJson));
        }

        return TaskFileLookupResult<IReadOnlyList<WorkspaceResultFileVersion>>.Ok(versions, TaskFileSources.Workspace);
    }

    private static IEnumerable<string> LanePaths(string currentGitPath, string currentState)
    {
        yield return currentGitPath;
        var segments = currentGitPath.Split('/');
        var index = Array.FindIndex(segments, segment => string.Equals(segment, currentState, StringComparison.Ordinal));
        if (index < 0) yield break;
        foreach (var lane in TaskStates.All)
        {
            var copy = segments.ToArray();
            copy[index] = lane;
            yield return string.Join('/', copy);
        }
    }

    private static string? ReadFirstAt(string gitRoot, string sha, params string[] paths)
    {
        foreach (var path in paths)
        {
            var value = RunGit(gitRoot, "show", $"{sha}:{path}");
            if (value.Code == 0) return value.Out;
        }
        return null;
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private TaskFileLookupResult<TaskFileContent> ReadLiveFile(
        string jobId,
        string? watchPath,
        string requestPath,
        string? scope)
    {
        var resolved = ResolveCandidates(jobId, watchPath, requestPath, scope);
        if (!resolved.Success) return TaskFileLookupResult<TaskFileContent>.Fail(resolved.StatusCode, resolved.Error);

        foreach (var candidate in resolved.Candidates)
        {
            if (!File.Exists(candidate.LivePath))
            {
                if (resolved.IsExplicitScope)
                    return TaskFileLookupResult<TaskFileContent>.Fail(StatusCodes.Status404NotFound, "File not found.");
                continue;
            }

            try
            {
                var content = File.ReadAllText(candidate.LivePath, Encoding.UTF8);
                return TaskFileLookupResult<TaskFileContent>.Ok(
                    new TaskFileContent(content, ContentTypeFor(candidate.RequestPath), candidate.RequestPath),
                    candidate.Source);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return TaskFileLookupResult<TaskFileContent>.Fail(StatusCodes.Status404NotFound, "File could not be read.");
            }
        }

        return TaskFileLookupResult<TaskFileContent>.Fail(StatusCodes.Status404NotFound, "File not found.");
    }

    private CandidateResolution ResolveCandidates(string jobId, string? watchPath, string requestPath, string? scope)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null)
            return CandidateResolution.Fail(StatusCodes.Status404NotFound, "Job not found.");

        if (!TryNormalizeRelativePath(requestPath, out var normalized, out var error))
            return CandidateResolution.Fail(StatusCodes.Status400BadRequest, error);

        var normalizedScope = NormalizeScope(scope);
        if (normalizedScope == null)
            return CandidateResolution.Fail(StatusCodes.Status400BadRequest, "scope must be auto, workspace, or code.");

        var candidates = new List<TaskFileCandidate>();
        var includeWorkspace = normalizedScope is TaskFileSources.Auto or TaskFileSources.Workspace;
        var includeCode = normalizedScope is TaskFileSources.Auto or TaskFileSources.Code;

        if (includeWorkspace)
        {
            var workspaceCandidate = BuildWorkspaceCandidate(info, normalized);
            if (workspaceCandidate != null)
                candidates.Add(workspaceCandidate);
            else if (normalizedScope == TaskFileSources.Workspace)
                return CandidateResolution.Fail(StatusCodes.Status400BadRequest, "The requested workspace path is outside the task folder.");
        }

        if (includeCode)
        {
            var codeCandidate = BuildCodeCandidate(info, watchPath, normalized);
            if (codeCandidate != null)
                candidates.Add(codeCandidate);
            else if (normalizedScope == TaskFileSources.Code)
                return CandidateResolution.Fail(StatusCodes.Status400BadRequest, "Could not resolve the code repository for this task.");
        }

        if (candidates.Count == 0)
            return CandidateResolution.Fail(StatusCodes.Status404NotFound, "No file source is available for this task.");

        return CandidateResolution.Ok(candidates, normalizedScope != TaskFileSources.Auto);
    }

    private TaskFileCandidate? BuildWorkspaceCandidate(TaskInfo info, string requestPath)
    {
        var jobRoot = Path.GetFullPath(info.FolderPath);
        var livePath = Path.GetFullPath(Path.Combine(jobRoot, requestPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithin(jobRoot, livePath)) return null;

        var gitRoot = ResolveGitRoot(jobRoot);
        var gitPath = gitRoot == null
            ? requestPath
            : Path.GetRelativePath(gitRoot, livePath).Replace('\\', '/');

        if (gitRoot != null && (Path.IsPathRooted(gitPath) || gitPath.StartsWith("..", StringComparison.Ordinal)))
            return null;

        return new TaskFileCandidate(
            TaskFileSources.Workspace,
            requestPath,
            livePath,
            gitRoot,
            gitPath,
            info.FolderPath);
    }

    private TaskFileCandidate? BuildCodeCandidate(TaskInfo info, string? watchPath, string requestPath)
    {
        var repoRoot = _git.ResolveRepoRoot(info.Id, watchPath);
        if (string.IsNullOrWhiteSpace(repoRoot)) return null;

        var root = Path.GetFullPath(repoRoot);
        var livePath = Path.GetFullPath(Path.Combine(root, requestPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithin(root, livePath)) return null;

        var gitRoot = ResolveGitRoot(root);
        var gitPath = gitRoot == null
            ? requestPath
            : Path.GetRelativePath(gitRoot, livePath).Replace('\\', '/');

        if (gitRoot != null && (Path.IsPathRooted(gitPath) || gitPath.StartsWith("..", StringComparison.Ordinal)))
            return null;

        return new TaskFileCandidate(
            TaskFileSources.Code,
            requestPath,
            livePath,
            gitRoot,
            gitPath,
            info.FolderPath);
    }

    private GitValue<IReadOnlyList<TaskFileHistoryEntry>> ReadHistory(TaskFileCandidate candidate)
    {
        var log = RunGit(
            candidate.GitRoot!,
            "log",
            "--follow",
            $"--format=%H%x1f%aI%x1f%an <%ae>%x1f%s%x1f%B%x1e",
            "--",
            candidate.GitPath);
        if (log.Code != 0)
            return GitValue<IReadOnlyList<TaskFileHistoryEntry>>.Fail(log.Err);

        if (string.IsNullOrWhiteSpace(log.Out))
            return GitValue<IReadOnlyList<TaskFileHistoryEntry>>.Ok([]);

        var entries = new List<TaskFileHistoryEntry>();
        foreach (var raw in log.Out.Split(RecordSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.Split(UnitSeparator, 5);
            if (parts.Length < 5) continue;

            var sha = parts[0].Trim();
            if (sha.Length == 0) continue;

            DateTime? at = null;
            if (DateTimeOffset.TryParse(parts[1].Trim(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
            {
                at = dto.UtcDateTime;
            }

            var body = parts[4];
            var runIndexRaw = ReadTrailer(body, "Run-Index");
            int? runIndex = int.TryParse(runIndexRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? n
                : null;
            var verdict = ReadTrailer(body, "Verdict");
            var steps = ReadTrailer(body, "Steps");
            var generation = candidate.Source == TaskFileSources.Workspace
                ? ReadGenerationAt(candidate, sha)
                : null;

            entries.Add(new TaskFileHistoryEntry(
                Sha: sha,
                At: at,
                RunIndex: runIndex,
                Verdict: string.IsNullOrWhiteSpace(verdict) ? null : verdict,
                Message: string.IsNullOrWhiteSpace(parts[3]) ? FirstLine(body) : parts[3].Trim(),
                Author: parts[2].Trim(),
                Provenance: new TaskFileVersionProvenance(
                    Source: candidate.Source,
                    Path: candidate.RequestPath,
                    Steps: string.IsNullOrWhiteSpace(steps) ? null : steps,
                    Generation: generation)));
        }

        return GitValue<IReadOnlyList<TaskFileHistoryEntry>>.Ok(entries);
    }

    private FileGenerationMeta? ReadGenerationAt(TaskFileCandidate candidate, string sha)
    {
        var metaPath = Path.Combine(candidate.JobFolderPath, ".metadata", "files.json");
        var rel = Path.GetRelativePath(candidate.GitRoot!, metaPath).Replace('\\', '/');
        if (Path.IsPathRooted(rel) || rel.StartsWith("..", StringComparison.Ordinal))
            return null;

        var show = RunGit(candidate.GitRoot!, "show", $"{sha}:{rel}");
        if (show.Code != 0 || string.IsNullOrWhiteSpace(show.Out))
            return null;

        try
        {
            var entries = JsonSerializer.Deserialize<List<FileGenerationMeta>>(show.Out, ReadOpts) ?? [];
            var match = entries.LastOrDefault(e =>
                string.Equals(NormalizeFileIndexPath(e.File), candidate.RequestPath, StringComparison.OrdinalIgnoreCase));
            return match == null
                ? null
                : match with
                {
                    File = NormalizeFileIndexPath(match.File),
                    TokensTotal = match.TokensTotal > 0 ? match.TokensTotal : match.TokensIn + match.TokensOut,
                    DurationMs = match.DurationMs > 0 || match.StartedAt == null || match.EndedAt == null
                        ? match.DurationMs
                        : (long)(match.EndedAt.Value - match.StartedAt.Value).TotalMilliseconds,
                };
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope)) return TaskFileSources.Auto;
        var value = scope.Trim();
        if (string.Equals(value, TaskFileSources.Auto, StringComparison.OrdinalIgnoreCase)) return TaskFileSources.Auto;
        if (string.Equals(value, TaskFileSources.Workspace, StringComparison.OrdinalIgnoreCase)) return TaskFileSources.Workspace;
        if (string.Equals(value, TaskFileSources.Code, StringComparison.OrdinalIgnoreCase)) return TaskFileSources.Code;
        return null;
    }

    private static bool TryNormalizeRelativePath(string raw, out string normalized, out string error)
    {
        normalized = "";
        error = "";
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "path is required.";
            return false;
        }

        var candidate = raw.Replace('\\', '/').Trim('/');
        var segments = candidate.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            error = "path is required.";
            return false;
        }

        foreach (var segment in segments)
        {
            if (segment is "." or ".." || string.Equals(segment, ".git", StringComparison.OrdinalIgnoreCase))
            {
                error = "path is not allowed.";
                return false;
            }

            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                error = "path contains invalid characters.";
                return false;
            }
        }

        normalized = string.Join('/', segments);
        return true;
    }

    private static string? ResolveGitRoot(string path)
    {
        var result = RunGit(path, "rev-parse", "--show-toplevel");
        if (result.Code != 0 || string.IsNullOrWhiteSpace(result.Out)) return null;
        return Path.GetFullPath(result.Out.Trim());
    }

    private static bool IsWithin(string root, string fullPath)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(fullPath);
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSha(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var s = value.Trim();
        if (s.Length is < 7 or > 40) return false;
        foreach (var ch in s)
        {
            var isHex = ch is >= '0' and <= '9'
                || ch is >= 'a' and <= 'f'
                || ch is >= 'A' and <= 'F';
            if (!isHex) return false;
        }
        return true;
    }

    private static string? ReadTrailer(string body, string name)
    {
        var prefix = name + ":";
        foreach (var line in body.Replace("\r\n", "\n").Split('\n', StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return line[prefix.Length..].Trim();
        }
        return null;
    }

    private static string FirstLine(string body) =>
        body.Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "";

    private static string NormalizeFileIndexPath(string file) =>
        file.Replace('\\', '/').TrimStart('/');

    private static string ContentTypeFor(string path)
    {
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return "application/json";
        if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return "text/markdown";
        if (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase)) return "text/html";
        return "text/plain";
    }

    private void LogSlow(Stopwatch sw, string operation, string jobId, string source, string path)
    {
        sw.Stop();
        if (sw.ElapsedMilliseconds < 500) return;
        _logger.LogInformation(
            "task-file-history operation={Operation} jobId={JobId} source={Source} path={Path} elapsedMs={ElapsedMs}",
            operation, jobId, source, path, sw.ElapsedMilliseconds);
    }

    private static GitProcessResult RunGit(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var p = Process.Start(psi);
        if (p == null) return new GitProcessResult("", "Failed to start git.", -1);

        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit((int)GitTimeout.TotalMilliseconds))
        {
            try { p.Kill(entireProcessTree: true); } catch (Exception __ex) { SilentCatch.Note(__ex, "TaskFileHistoryService:523"); }
            return new GitProcessResult("", "git timed out.", -1);
        }

        return new GitProcessResult(
            stdout.GetAwaiter().GetResult(),
            stderr.GetAwaiter().GetResult(),
            p.ExitCode);
    }

    private sealed record TaskFileCandidate(
        string Source,
        string RequestPath,
        string LivePath,
        string? GitRoot,
        string GitPath,
        string JobFolderPath);

    private sealed record GitProcessResult(string Out, string Err, int Code);

    private sealed record GitValue<T>(bool Success, T Value, string? Error)
    {
        public static GitValue<T> Ok(T value) => new(true, value, null);
        public static GitValue<T> Fail(string? error) => new(false, default!, error);
    }

    private sealed record CandidateResolution(
        bool Success,
        IReadOnlyList<TaskFileCandidate> Candidates,
        bool IsExplicitScope,
        int StatusCode,
        string? Error)
    {
        public static CandidateResolution Ok(IReadOnlyList<TaskFileCandidate> candidates, bool isExplicitScope) =>
            new(true, candidates, isExplicitScope, StatusCodes.Status200OK, null);

        public static CandidateResolution Fail(int statusCode, string error) =>
            new(false, [], false, statusCode, error);
    }
}

public static class TaskFileSources
{
    public const string Auto = "auto";
    public const string Workspace = "workspace";
    public const string Code = "code";
}

public sealed record TaskFileLookupResult<T>(
    bool Success,
    T? Value,
    int StatusCode,
    string? Error,
    string? Source)
{
    public static TaskFileLookupResult<T> Ok(T value, string source) =>
        new(true, value, StatusCodes.Status200OK, null, source);

    public static TaskFileLookupResult<T> Fail(int statusCode, string? error) =>
        new(false, default, statusCode, error, null);
}

public sealed record TaskFileHistoryEntry(
    string Sha,
    DateTime? At,
    int? RunIndex,
    string? Verdict,
    string Message,
    string Author,
    TaskFileVersionProvenance Provenance);

public sealed record TaskFileVersionProvenance(
    string Source,
    string Path,
    string? Steps,
    FileGenerationMeta? Generation);

public sealed record TaskFileContent(string Content, string ContentType, string Path);

public sealed record WorkspaceResultFileVersion(
    TaskFileHistoryEntry Entry,
    string Content,
    string? TaskJson);
