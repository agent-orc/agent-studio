using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AgentRunner.SourceFacts;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>The stacked candidate: each member applied in stack order, or the member that could not be applied.</summary>
internal sealed record PackageStack(
    string IntegrationTipSha,
    string? CandidateSha,
    IReadOnlyList<ReviewPackageMemberStepDto> Steps,
    string? ConflictMember,
    IReadOnlyList<string> ConflictFiles);

/// <summary>Rendered package material, its union size, and the hunk ownership of the candidate.</summary>
internal sealed record PackageMaterial(
    string Text, int Files, int Lines, bool OverBudget, ReviewPackageOwnership Ownership);

/// <summary>
/// Disposable workspace of one shadow package attempt (AGT-3400 S4). It
/// materializes every member result and the integration tip, builds the
/// stacked candidate on the tip in stack order while keeping each member's
/// commits (cherry-picked, or merged when they cannot be replayed), and
/// renders each member's diff from the candidate before it to the candidate
/// after it, so every hunk has one owner. It never pushes and never writes a
/// ref outside its own clone.
/// </summary>
internal sealed class PackageReviewWorkspace
{
    private static readonly string[] Identity =
    [
        "-c", "user.name=Agent Studio package review",
        "-c", "user.email=package-review@agent-studio.invalid",
        "-c", "commit.gpgsign=false",
    ];

    private readonly ReviewPackageSubjectDto _subject;
    private readonly Action<string> _log;
    private readonly Func<DateTime> _utcNow;
    private readonly string? _sourceCacheRoot;

    public PackageReviewWorkspace(RunnerOptions options, ReviewPackageSubjectDto subject, ReviewPackageLeaseDto lease,
        Action<string> log, Func<DateTime>? utcNow = null, string? sourceCacheRoot = null)
    {
        _subject = subject;
        _log = log;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _sourceCacheRoot = sourceCacheRoot;
        AttemptRoot = Path.Combine(Path.GetFullPath(options.ReviewWorkDir),
            AttemptPrefix + RemoteReviewWorkspace.SafeSegment(lease.AttemptId));
        RepositoryPath = Path.Combine(AttemptRoot, "repository");
        ArtifactPath = Path.Combine(AttemptRoot, "artifacts");
        HomePath = Path.Combine(AttemptRoot, "home");
        TempPath = Path.Combine(AttemptRoot, "tmp");
    }

    /// <summary>Package attempt directories sit beside, not inside, the per-card <c>review-</c> attempts.</summary>
    internal const string AttemptPrefix = "package-";

    public string AttemptRoot { get; }
    public string RepositoryPath { get; }
    public string ArtifactPath { get; }
    public string HomePath { get; }
    public string TempPath { get; }

    private IReadOnlyList<ReviewPackageMemberDto> Members => _subject.Members.OrderBy(member => member.Order).ToArray();

    public async Task<string> MaterializeAsync(CancellationToken ct)
    {
        foreach (var path in new[] { ArtifactPath, HomePath, TempPath }) Directory.CreateDirectory(path);
        var url = _subject.Plan.RepositoryUrl
            ?? throw new ReviewInfrastructureException("SnapshotUnavailable", "The package plan names no repository URL.");
        var last = Members[^1];
        await HostSourceObjectCache.MaterializeAsync(url, last.ResultRef ?? last.ResultSha, last.ResultSha,
            RepositoryPath, Git, _utcNow, ct, cacheRoot: _sourceCacheRoot);
        foreach (var member in Members.Take(Members.Count - 1))
            await FetchAsync(member.ResultRef ?? member.ResultSha, member.ResultSha, ct);
        await Required(["fetch", "--no-tags", "origin", _subject.Plan.IntegrationRef!], ct);
        var tip = (await Required(["rev-parse", "FETCH_HEAD^{commit}"], ct)).StdOut.Trim();
        _log($"review_package_source_ready package={_subject.PackageId} members={Members.Count} tip={tip}");
        return tip;
    }

    /// <summary>
    /// Applies the members on <paramref name="integrationTip"/> in stack order.
    /// A member's own commits are those its result reaches and neither the tip
    /// nor an earlier member does. Without merge commits they are cherry-picked
    /// one by one; otherwise, or when a pick conflicts, the untouched result is
    /// merged with one merge commit. A merge conflict stops the stack.
    /// </summary>
    public async Task<PackageStack> StackAsync(string integrationTip, CancellationToken ct)
    {
        await Required(["checkout", "--detach", "--force", integrationTip], ct);
        var exclude = new List<string> { integrationTip };
        var steps = new List<ReviewPackageMemberStepDto>();
        foreach (var member in Members)
        {
            var before = await HeadAsync(ct);
            var own = await LinesAsync(["rev-list", "--reverse", "--topo-order", "--no-merges", member.ResultSha,
                "--not", .. exclude], ct);
            var merges = await LinesAsync(["rev-list", "--merges", member.ResultSha, "--not", .. exclude], ct);
            var strategy = ReviewPackageStackStrategies.Linear;
            if (own.Count > 0 && merges.Count == 0)
            {
                var pick = await Git(RepositoryPath, [.. Identity, "cherry-pick", "--keep-redundant-commits", .. own], false, ct);
                if (!pick.Success)
                {
                    await Git(RepositoryPath, ["cherry-pick", "--abort"], false, ct);
                    await Required(["reset", "--hard", before], ct);
                    strategy = ReviewPackageStackStrategies.Merge;
                }
            }
            else if (merges.Count > 0)
            {
                strategy = ReviewPackageStackStrategies.Merge;
            }

            if (strategy == ReviewPackageStackStrategies.Merge)
            {
                var merge = await Git(RepositoryPath, [.. Identity, "merge", "--no-ff", "--no-edit",
                    "-m", $"package review: stack {member.TaskKey}", member.ResultSha], false, ct);
                if (!merge.Success)
                {
                    var conflicted = await LinesAsync(["diff", "--name-only", "--diff-filter=U"], ct);
                    await Git(RepositoryPath, ["merge", "--abort"], false, ct);
                    _log($"review_package_stack_conflict package={_subject.PackageId} member={member.TaskKey} files={conflicted.Count}");
                    return new PackageStack(integrationTip, null, steps, member.TaskKey, conflicted);
                }
            }

            var after = await HeadAsync(ct);
            var commits = strategy == ReviewPackageStackStrategies.Linear
                ? await LinesAsync(["rev-list", "--reverse", $"{before}..{after}"], ct)
                : [.. await LinesAsync(["rev-list", "--reverse", member.ResultSha, "--not", .. exclude], ct), after];
            var (files, lines) = await NumstatAsync(before, after, ct);
            steps.Add(new ReviewPackageMemberStepDto(member.TaskKey, before, after, strategy, commits, files, lines));
            exclude.Add(member.ResultSha);
        }
        return new PackageStack(integrationTip, await HeadAsync(ct), steps, null, []);
    }

    /// <summary>
    /// Each member's diff with its changed files, the union size against the
    /// plan budget, and the hunk ownership. Over budget, no text is rendered:
    /// a package splits, it is never truncated.
    /// </summary>
    public async Task<PackageMaterial> MaterialAsync(PackageStack stack, CancellationToken ct)
    {
        var plan = _subject.Plan;
        var diffs = new List<(ReviewPackageMemberStepDto Step, MergeBaseDiffMaterial Material)>();
        foreach (var step in stack.Steps)
            diffs.Add((step, await MergeBaseDiffMaterial.ComputeAsync(RepositoryPath, step.TipBeforeSha, step.TipAfterSha,
                plan.MaximumFiles, plan.MaximumLines, Environment(), true, ct)));
        var files = diffs.SelectMany(diff => diff.Material.ChangedFiles).Distinct(StringComparer.Ordinal).Count();
        var lines = diffs.Sum(diff => diff.Material.TotalDiffLines);
        var over = diffs.Any(diff => diff.Material.TotalFiles > plan.MaximumFiles)
            || files > plan.MaximumFiles || lines > plan.MaximumLines;
        var ownership = new ReviewPackageOwnership([], new Dictionary<string, IReadOnlyList<string>>());
        if (over) return new PackageMaterial("", files, lines, true, ownership);

        var zeroContext = new List<(string, string)>();
        foreach (var step in stack.Steps)
            zeroContext.Add((step.TaskKey, (await Required([.. MergeBaseDiffMaterial.PinnedConfiguration, "diff",
                "--no-ext-diff", "--no-textconv", "--no-color", "--find-renames", "--unified=0",
                step.TipBeforeSha, step.TipAfterSha], ct, pinned: true)).StdOut));
        ownership = ReviewPackageOwnershipIndex.Build(zeroContext);
        return new PackageMaterial(Render(stack, diffs, files, lines), files, lines, false, ownership);
    }

    /// <summary>The Dossier at the stacked candidate, as text, bounded with a visible marker.</summary>
    public async Task<string> DossierAsync(string candidateSha, CancellationToken ct)
    {
        var shown = await Git(RepositoryPath, ["show", $"{candidateSha}:{_subject.DossierPath}"], false, ct);
        return DossierExcerpt(_subject.DossierPath,
            shown.Success ? shown.StdOut : null, _subject.Plan.DossierExcerptCharacters);
    }

    public void Delete()
    {
        if (Directory.Exists(AttemptRoot) && !ResilientDirectory.TryDelete(AttemptRoot))
            _log($"review_package_cleanup_failed path={AttemptRoot}");
    }

    internal static string DossierExcerpt(string path, string? content, int maximumCharacters)
    {
        var text = new StringBuilder();
        text.AppendLf($"## Dossier excerpt (`{path}` at the stacked candidate)");
        text.AppendLf();
        if (content is null)
        {
            text.AppendLf("_The Dossier file is not present at the stacked candidate._");
            return text.ToString();
        }
        var plain = path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase)
            ? HtmlText(content) : content.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        var limit = Math.Max(1_000, maximumCharacters);
        text.AppendLf(plain.Length <= limit ? plain : plain[..limit]);
        if (plain.Length > limit)
            text.AppendLf().AppendLf($"[[DOSSIER_EXCERPT_TRUNCATED: shown {limit} of {plain.Length} characters]]");
        return text.ToString();
    }

    private static string HtmlText(string html)
    {
        var text = Regex.Replace(html, @"<(script|style)\b[^>]*>[\s\S]*?</\1>", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<(br|/p|/li|/h[1-6]|/tr|/div|/section|/table)\b[^>]*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<t[dh]\b[^>]*>", " | ", RegexOptions.IgnoreCase);
        text = WebUtility.HtmlDecode(Regex.Replace(text, @"<[^>]+>", ""));
        text = Regex.Replace(text.Replace("\r\n", "\n", StringComparison.Ordinal), @"[ \t]+", " ");
        return Regex.Replace(text, @"\n\s*\n+", "\n\n").Trim();
    }

    private string Render(PackageStack stack,
        IReadOnlyList<(ReviewPackageMemberStepDto Step, MergeBaseDiffMaterial Material)> diffs, int files, int lines)
    {
        var plan = _subject.Plan;
        var text = new StringBuilder();
        text.AppendLf("## Package review material");
        text.AppendLf();
        text.AppendLf($"- Integration ref: `{plan.IntegrationRef}` at `{stack.IntegrationTipSha}` (freshly fetched)");
        text.AppendLf($"- Stacked candidate: `{stack.CandidateSha}`");
        text.AppendLf($"- Members in stack order: {stack.Steps.Count}");
        text.AppendLf($"- Union: {files} changed files, {lines} diff lines (budget {plan.MaximumFiles} files, {plan.MaximumLines} lines)");
        var index = 0;
        foreach (var (step, material) in diffs)
        {
            index++;
            text.AppendLf();
            text.AppendLf($"### Member {index}: `{step.TaskKey}` ({step.Strategy}, {step.CommitShas.Count} commit(s))");
            text.AppendLf();
            text.AppendLf($"- Candidate before: `{step.TipBeforeSha}`");
            text.AppendLf($"- Candidate after: `{step.TipAfterSha}`");
            text.AppendLf();
            if (material.ChangedFiles.Count == 0) text.AppendLf("_No changed files: the tip already holds this member._");
            else foreach (var file in material.ChangedFiles) text.Append("- `").Append(file).AppendLf("`");
            text.AppendLf();
            text.AppendLf("```diff");
            foreach (var line in material.DiffLines) text.AppendLf(line);
            text.AppendLf("```");
        }
        return text.ToString();
    }

    private async Task FetchAsync(string reference, string expectedSha, CancellationToken ct)
    {
        await Required(["fetch", "--no-tags", "origin", reference], ct);
        var present = await Git(RepositoryPath, ["cat-file", "-e", expectedSha + "^{commit}"], false, ct);
        if (!present.Success)
            throw new ReviewInfrastructureException("ShaMismatch",
                $"Fetched '{reference}' does not contain the member result '{expectedSha}'.");
    }

    private async Task<(int Files, int Lines)> NumstatAsync(string before, string after, CancellationToken ct)
    {
        var rows = await LinesAsync(["diff", "--numstat", "--find-renames", before, after], ct);
        var lines = 0;
        foreach (var row in rows)
        {
            var parts = row.Split('\t');
            if (parts.Length >= 2 && int.TryParse(parts[0], out var added) && int.TryParse(parts[1], out var removed))
                lines += added + removed;
        }
        return (rows.Count, lines);
    }

    private async Task<string> HeadAsync(CancellationToken ct)
        => (await Required(["rev-parse", "HEAD"], ct)).StdOut.Trim();

    private async Task<IReadOnlyList<string>> LinesAsync(IReadOnlyList<string> args, CancellationToken ct)
        => (await Required(args, ct)).StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private async Task<ProcessResult> Required(IReadOnlyList<string> args, CancellationToken ct, bool pinned = false)
    {
        var result = pinned
            ? await ProcessRunner.RunAsync("git", args, RepositoryPath, environment: Environment(), clearEnvironment: true, ct: ct)
            : await Git(RepositoryPath, args, false, ct);
        if (!result.Success)
            throw new ReviewInfrastructureException("WorkspaceProofFailed",
                $"git {string.Join(' ', args.Take(3))} failed: {result.StdErr.Trim()}");
        return result;
    }

    private async Task<ProcessResult> Git(string cwd, IReadOnlyList<string> args, bool credentialed, CancellationToken ct)
        => await ProcessRunner.RunAsync("git", ["-c", "credential.helper=", .. args], cwd,
            environment: Environment(), clearEnvironment: true, ct: ct);

    private IReadOnlyDictionary<string, string?> Environment() => new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        ["PATH"] = System.Environment.GetEnvironmentVariable("PATH"),
        ["LANG"] = System.Environment.GetEnvironmentVariable("LANG") ?? "C.UTF-8",
        ["LC_ALL"] = System.Environment.GetEnvironmentVariable("LC_ALL") ?? "C.UTF-8",
        ["SSL_CERT_FILE"] = System.Environment.GetEnvironmentVariable("SSL_CERT_FILE"),
        ["SSL_CERT_DIR"] = System.Environment.GetEnvironmentVariable("SSL_CERT_DIR"),
        ["SystemRoot"] = System.Environment.GetEnvironmentVariable("SystemRoot"),
        ["HOME"] = HomePath,
        ["TMPDIR"] = TempPath,
        ["GIT_CONFIG_NOSYSTEM"] = "1",
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GIT_OPTIONAL_LOCKS"] = "0",
    };
}
