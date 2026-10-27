using AgentStudio.Shared;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using SourceFactDemandRequest = AgentStudio.TaskServer.Contracts.SourceFactDemandRequest;
using SourceFactKinds = AgentStudio.TaskServer.Contracts.SourceFactKinds;
using SourcePathHistoryDto = AgentStudio.TaskServer.Contracts.SourcePathHistoryDto;
using Microsoft.AspNetCore.Http;

namespace AgentStudio.Tasks;

public sealed class TaskFileHistoryService
{
    private const char UnitSeparator = '\x1f';
    private const char RecordSeparator = '\x1e';
    internal const string GitBudgetExhausted = "The git history budget for this request is exhausted.";
    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly TaskScannerService _scanner;
    private readonly GitService _git;
    private readonly ILogger<TaskFileHistoryService> _logger;
    private readonly ITaskHistoryGitRunner _runner;

    /// <summary>
    /// Budget every lookup of this service starts with. Overridable so tests can
    /// pin an exhausted budget instead of waiting one out; production leaves it
    /// at the policy default.
    /// </summary>
    private readonly TimeSpan _gitCallBudget;
    private readonly AgentStudio.Git.SourceFacts.ISourceFactsReader? _sourceFacts;

    public TaskFileHistoryService(
        TaskScannerService scanner,
        GitService git,
        ILogger<TaskFileHistoryService> logger,
        TimeSpan? gitCallBudget = null,
        ITaskHistoryGitRunner? runner = null,
        AgentStudio.Git.SourceFacts.ISourceFactsReader? sourceFacts = null)
    {
        _scanner = scanner;
        _git = git;
        _logger = logger;
        _gitCallBudget = gitCallBudget ?? GitCallBudgetPolicy.DefaultTotalBudget;
        _runner = runner ?? new ProcessTaskHistoryGitRunner();
        _sourceFacts = sourceFacts;
    }

    /// <summary>True when source file history is served from derived facts (AGT-3318).</summary>
    public bool ReadsDerivedCodeHistory
        => _sourceFacts?.Mode == AgentStudio.Git.SourceFacts.SourceFactsReadMode.Derived;

    /// <summary>
    /// Source file history of a card from the path history the runner-host
    /// producer published for the card's result commit (AGT-3318). No Git
    /// process starts. A missing row returns an empty list in state
    /// <c>warming</c> and asks the producer for it; a card without an
    /// attributed commit has no result to follow and answers
    /// <c>unavailable</c>.
    /// </summary>
    public async Task<(TaskFileLookupResult<IReadOnlyList<TaskFileHistoryEntry>> Result, string State)> GetDerivedCodeHistoryAsync(
        string jobId,
        string? watchPath,
        string requestPath,
        CancellationToken ct)
    {
        var facts = _sourceFacts ?? throw new InvalidOperationException("Derived source facts are not configured.");
        using var _t = GitProcessTelemetry.BeginRequest("tasks/files/history", _logger);
        var path = NormalizeFileIndexPath(requestPath.Trim());
        if (path.Length == 0 || path.Split('/').Any(segment => segment is ".." or "."))
            return (TaskFileLookupResult<IReadOnlyList<TaskFileHistoryEntry>>.Fail(
                StatusCodes.Status400BadRequest, "Path must be repository-relative."), AgentStudio.Git.SourceFacts.SourceFactStates.Ready);
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null)
            return (TaskFileLookupResult<IReadOnlyList<TaskFileHistoryEntry>>.Fail(
                StatusCodes.Status404NotFound, "Task not found."), AgentStudio.Git.SourceFacts.SourceFactStates.Ready);
        var head = ResultHead(info);
        if (head is null)
            return (TaskFileLookupResult<IReadOnlyList<TaskFileHistoryEntry>>.Ok([], TaskFileSources.Code),
                AgentStudio.Git.SourceFacts.SourceFactStates.Unavailable);

        var read = await facts.GetPathHistoryAsync(info.ProjectName, head, path, ct);
        if (read.Value is null)
        {
            if (read.State == AgentStudio.Git.SourceFacts.SourceFactStates.Warming)
                await facts.RequestAsync(info.ProjectName, new SourceFactDemandRequest(
                    SourceFactKinds.PathHistory, "card-open-file-history", HeadSha: head, Path: path), ct);
            return (TaskFileLookupResult<IReadOnlyList<TaskFileHistoryEntry>>.Ok([], TaskFileSources.Code), read.State);
        }
        return (TaskFileLookupResult<IReadOnlyList<TaskFileHistoryEntry>>.Ok(
            ProjectDerivedHistory(read.Value, path), TaskFileSources.Code), AgentStudio.Git.SourceFacts.SourceFactStates.Ready);
    }

    /// <summary>The card's result head: its last attributed commit.</summary>
    internal static string? ResultHead(TaskInfo info)
    {
        var attributed = new HashSet<string>(
            TaskIntegrationStatusService.AttributedCommits(info), StringComparer.OrdinalIgnoreCase);
        var chain = info.Commits.Count > 0
            ? info.Commits
            : info.Commit is null ? [] : new List<TaskCommitInfo> { info.Commit };
        return chain.LastOrDefault(commit => commit.Sha is { Length: 40 or 64 } && attributed.Contains(commit.Sha))?.Sha;
    }

    /// <summary>Maps stored path history to the history wire shape, with the same trailer parsing as the Git read.</summary>
    internal static IReadOnlyList<TaskFileHistoryEntry> ProjectDerivedHistory(SourcePathHistoryDto history, string requestPath)
        => history.Entries.Select(entry =>
        {
            var runIndexRaw = ReadTrailer(entry.Body, "Run-Index");
            var verdict = ReadTrailer(entry.Body, "Verdict");
            var steps = ReadTrailer(entry.Body, "Steps");
            return new TaskFileHistoryEntry(
                Sha: entry.Sha,
                At: entry.AuthoredAt,
                RunIndex: int.TryParse(runIndexRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null,
                Verdict: string.IsNullOrWhiteSpace(verdict) ? null : verdict,
                Message: string.IsNullOrWhiteSpace(entry.Subject) ? FirstLine(entry.Body) : entry.Subject.Trim(),
                Author: entry.Author,
                Provenance: new TaskFileVersionProvenance(
                    Source: TaskFileSources.Code,
                    Path: requestPath,
                    Steps: string.IsNullOrWhiteSpace(steps) ? null : steps,
                    Generation: null));
        }).ToArray();

    public TaskFileLookupResult<IReadOnlyList<TaskFileHistoryEntry>> GetHistory(
        string jobId,
        string? watchPath,
        string requestPath,
        string? scope)
    {
        // One budget for the whole lookup, so the deadline the caller sees is
        // this method's and not the sum of however many commits the file has.
        var budget = new GitCallBudget(_gitCallBudget);
        var resolved = ResolveCandidates(budget, jobId, watchPath, requestPath, scope);
        if (!resolved.Success) return TaskFileLookupResult<IReadOnlyList<TaskFileHistoryEntry>>.Fail(resolved.StatusCode, resolved.Error);

        var sw = Stopwatch.StartNew();
        foreach (var candidate in resolved.Candidates)
        {
            if (candidate.GitRoot == null)
            {
                if (resolved.IsExplicitScope)
                    return TaskFileLookupResult<IReadOnlyList<TaskFileHistoryEntry>>.Fail(
                        StatusCodes.Status400BadRequest,
                        budget.IsExhausted ? GitBudgetExhausted : "The selected file source is not in a git repository.");
                continue;
            }

            var result = ReadHistory(budget, candidate);
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
            return ReadLiveFile(new GitCallBudget(_gitCallBudget), jobId, watchPath, requestPath, scope);

        if (!IsSha(at))
            return TaskFileLookupResult<TaskFileContent>.Fail(StatusCodes.Status400BadRequest, "Invalid commit SHA.");

        var budget = new GitCallBudget(_gitCallBudget);
        var resolved = ResolveCandidates(budget, jobId, watchPath, requestPath, scope);
        if (!resolved.Success) return TaskFileLookupResult<TaskFileContent>.Fail(resolved.StatusCode, resolved.Error);

        var sw = Stopwatch.StartNew();
        foreach (var candidate in resolved.Candidates)
        {
            if (candidate.GitRoot == null)
            {
                if (resolved.IsExplicitScope)
                    return TaskFileLookupResult<TaskFileContent>.Fail(
                        StatusCodes.Status400BadRequest,
                        budget.IsExhausted ? GitBudgetExhausted : "The selected file source is not in a git repository.");
                continue;
            }

            var show = RunGit(budget, candidate.GitRoot, "show", $"{at}:{candidate.GitPath}");
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
    ///
    /// <para>This walks the whole workspace repository, which takes seconds to
    /// tens of seconds on a large one, so it never runs on a request path: only
    /// <see cref="LegacyResultHistoryBackfill"/> calls it, with its own budget
    /// (AGT-3086). The answer is all or nothing. A missing repository, a spent
    /// budget, a timeout or a failed read fails the walk with a reason instead
    /// of returning a shorter list that looks complete. A git exit that only
    /// says "this path is not in that commit" is an answer, not a failure.</para>
    /// </summary>
    internal TaskFileLookupResult<IReadOnlyList<WorkspaceResultFileVersion>> WalkLegacyResultHistory(
        TaskInfo info,
        GitCallBudget budget)
    {
        var jobRoot = Path.GetFullPath(info.FolderPath);
        var revParse = RunGit(budget, jobRoot, "rev-parse", "--show-toplevel");
        if (revParse.Code != 0 || string.IsNullOrWhiteSpace(revParse.Out))
            return LegacyWalkFailed(revParse.RunnerFailed
                ? revParse.Err
                : "The workspace task folder is not in a git repository. " + revParse.Err.Trim());
        var gitRoot = Path.GetFullPath(revParse.Out.Trim());
        var gitPath = Path.GetRelativePath(gitRoot, Path.Combine(jobRoot, "status.md")).Replace('\\', '/');
        if (Path.IsPathRooted(gitPath) || gitPath.StartsWith("..", StringComparison.Ordinal))
            return LegacyWalkFailed("The task folder is outside the workspace repository.");

        var possiblePaths = LanePaths(gitPath, info.State).Distinct(StringComparer.Ordinal).ToList();
        // The commit metadata rides along with the walk, so no commit needs a
        // separate `show -s` call.
        var args = new List<string> { "log", "--all", "--format=%H%x1f%aI%x1f%an <%ae>%x1f%s%x1f%B%x1e", "--" };
        args.AddRange(possiblePaths);
        var log = RunGit(budget, gitRoot, args.ToArray());
        if (log.Code != 0)
            return LegacyWalkFailed(log.RunnerFailed ? log.Err : "git log failed. " + log.Err.Trim());

        var versions = new List<WorkspaceResultFileVersion>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in log.Out.Split(RecordSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.Split(UnitSeparator, 5);
            var sha = parts[0].Trim();
            if (parts.Length < 5 || sha.Length == 0 || !seen.Add(sha)) continue;

            string? historicalPath = null;
            foreach (var path in possiblePaths)
            {
                var exists = RunGit(budget, gitRoot, "cat-file", "-e", $"{sha}:{path}");
                if (exists.RunnerFailed) return LegacyWalkFailed(exists.Err);
                if (exists.Code != 0) continue;
                historicalPath = path;
                break;
            }
            // The commit touched a lane path without leaving status.md there,
            // for example the delete half of a lane move.
            if (historicalPath is null) continue;

            var content = RunGit(budget, gitRoot, "show", $"{sha}:{historicalPath}");
            if (content.Code != 0)
                return LegacyWalkFailed(content.RunnerFailed
                    ? content.Err
                    : $"git show {sha}:{historicalPath} failed. {content.Err.Trim()}");

            var historicalFolder = Path.GetDirectoryName(historicalPath)!.Replace('\\', '/');
            var taskJson = ReadFirstAt(
                budget,
                gitRoot,
                sha,
                $"{historicalFolder}/task.json",
                $"{historicalFolder}/job.json");
            if (!taskJson.Success) return LegacyWalkFailed(taskJson.Error);

            DateTime? at = DateTimeOffset.TryParse(
                parts[1].Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto)
                ? dto.UtcDateTime
                : null;
            var body = parts[4];
            int? runIndex = int.TryParse(ReadTrailer(body, "Run-Index"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var run)
                ? run
                : null;
            versions.Add(new WorkspaceResultFileVersion(
                new TaskFileHistoryEntry(
                    sha,
                    at,
                    runIndex,
                    NullIfEmpty(ReadTrailer(body, "Verdict")),
                    NullIfEmpty(parts[3]) ?? FirstLine(body),
                    parts[2].Trim(),
                    new TaskFileVersionProvenance(
                        TaskFileSources.Workspace,
                        historicalPath,
                        NullIfEmpty(ReadTrailer(body, "Steps")),
                        Generation: null)),
                content.Out,
                taskJson.Value));
        }

        return TaskFileLookupResult<IReadOnlyList<WorkspaceResultFileVersion>>.Ok(versions, TaskFileSources.Workspace);
    }

    private static TaskFileLookupResult<IReadOnlyList<WorkspaceResultFileVersion>> LegacyWalkFailed(string? reason)
        => TaskFileLookupResult<IReadOnlyList<WorkspaceResultFileVersion>>.Fail(
            StatusCodes.Status503ServiceUnavailable,
            string.IsNullOrWhiteSpace(reason) ? "git failed without a message." : reason.Trim());

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

    /// <summary>
    /// First of <paramref name="paths"/> present at <paramref name="sha"/>, or
    /// null when none is. Fails only when git itself gave no answer.
    /// </summary>
    private GitValue<string?> ReadFirstAt(GitCallBudget budget, string gitRoot, string sha, params string[] paths)
    {
        foreach (var path in paths)
        {
            var value = RunGit(budget, gitRoot, "show", $"{sha}:{path}");
            if (value.RunnerFailed) return GitValue<string?>.Fail(value.Err);
            if (value.Code == 0) return GitValue<string?>.Ok(value.Out);
        }
        return GitValue<string?>.Ok(null);
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private TaskFileLookupResult<TaskFileContent> ReadLiveFile(
        GitCallBudget budget,
        string jobId,
        string? watchPath,
        string requestPath,
        string? scope)
    {
        var resolved = ResolveCandidates(budget, jobId, watchPath, requestPath, scope);
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
                if (!FilesystemPathBoundary.IsRegularFileUnder(candidate.RootPath, candidate.LivePath))
                    return TaskFileLookupResult<TaskFileContent>.Fail(StatusCodes.Status404NotFound, "File could not be read.");
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

    private CandidateResolution ResolveCandidates(GitCallBudget budget, string jobId, string? watchPath, string requestPath, string? scope)
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
            var workspaceCandidate = BuildWorkspaceCandidate(budget, info, normalized);
            if (workspaceCandidate != null)
                candidates.Add(workspaceCandidate);
            else if (normalizedScope == TaskFileSources.Workspace)
                return CandidateResolution.Fail(StatusCodes.Status400BadRequest, "The requested workspace path is outside the task folder.");
        }

        if (includeCode)
        {
            var codeCandidate = BuildCodeCandidate(budget, info, watchPath, normalized);
            if (codeCandidate != null)
                candidates.Add(codeCandidate);
            else if (normalizedScope == TaskFileSources.Code)
                return CandidateResolution.Fail(StatusCodes.Status400BadRequest, "Could not resolve the code repository for this task.");
        }

        if (candidates.Count == 0)
            return CandidateResolution.Fail(StatusCodes.Status404NotFound, "No file source is available for this task.");

        return CandidateResolution.Ok(candidates, normalizedScope != TaskFileSources.Auto);
    }

    private TaskFileCandidate? BuildWorkspaceCandidate(GitCallBudget budget, TaskInfo info, string requestPath)
    {
        var jobRoot = Path.GetFullPath(info.FolderPath);
        var livePath = Path.GetFullPath(Path.Combine(jobRoot, requestPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithin(jobRoot, livePath)) return null;

        var gitRoot = ResolveGitRoot(budget, jobRoot);
        var gitPath = gitRoot == null
            ? requestPath
            : Path.GetRelativePath(gitRoot, livePath).Replace('\\', '/');

        if (gitRoot != null && (Path.IsPathRooted(gitPath) || gitPath.StartsWith("..", StringComparison.Ordinal)))
            return null;

        return new TaskFileCandidate(
            TaskFileSources.Workspace,
            requestPath,
            livePath,
            jobRoot,
            gitRoot,
            gitPath,
            info.FolderPath);
    }

    private TaskFileCandidate? BuildCodeCandidate(GitCallBudget budget, TaskInfo info, string? watchPath, string requestPath)
    {
        var repoRoot = _git.ResolveRepoRoot(info.Id, watchPath);
        if (string.IsNullOrWhiteSpace(repoRoot)) return null;

        var root = Path.GetFullPath(repoRoot);
        var livePath = Path.GetFullPath(Path.Combine(root, requestPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithin(root, livePath)) return null;

        var gitRoot = ResolveGitRoot(budget, root);
        var gitPath = gitRoot == null
            ? requestPath
            : Path.GetRelativePath(gitRoot, livePath).Replace('\\', '/');

        if (gitRoot != null && (Path.IsPathRooted(gitPath) || gitPath.StartsWith("..", StringComparison.Ordinal)))
            return null;

        return new TaskFileCandidate(
            TaskFileSources.Code,
            requestPath,
            livePath,
            root,
            gitRoot,
            gitPath,
            info.FolderPath);
    }

    private GitValue<IReadOnlyList<TaskFileHistoryEntry>> ReadHistory(GitCallBudget budget, TaskFileCandidate candidate)
    {
        var log = RunGit(
            budget,
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
                ? ReadGenerationAt(budget, candidate, sha)
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

    private FileGenerationMeta? ReadGenerationAt(GitCallBudget budget, TaskFileCandidate candidate, string sha)
    {
        var metaPath = Path.Combine(candidate.JobFolderPath, ".metadata", "files.json");
        var rel = Path.GetRelativePath(candidate.GitRoot!, metaPath).Replace('\\', '/');
        if (Path.IsPathRooted(rel) || rel.StartsWith("..", StringComparison.Ordinal))
            return null;

        var show = RunGit(budget, candidate.GitRoot!, "show", $"{sha}:{rel}");
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

    private string? ResolveGitRoot(GitCallBudget budget, string path)
    {
        var result = RunGit(budget, path, "rev-parse", "--show-toplevel");
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

    /// <summary>
    /// Runs one git call against the lookup's shared
    /// <see cref="GitCallBudget"/>. The budget, not a fixed per-call constant,
    /// supplies the timeout, so a lookup that walks many commits stays bounded as
    /// a whole (AGT-2867).
    /// </summary>
    private TaskHistoryGitResult RunGit(GitCallBudget budget, string cwd, params string[] args)
    {
        var timeout = budget.NextCallTimeout();
        if (timeout <= TimeSpan.Zero)
            return TaskHistoryGitResult.Failed(GitBudgetExhausted);
        return _runner.Run(cwd, args, timeout);
    }

    private sealed record TaskFileCandidate(
        string Source,
        string RequestPath,
        string LivePath,
        string RootPath,
        string? GitRoot,
        string GitPath,
        string JobFolderPath);

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
