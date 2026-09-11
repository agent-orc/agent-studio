using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// Maintains one shared origin checkout and creates a linked git worktree for
/// each claimed task. Shared git metadata is mutated under a short process-wide
/// gate; agent CLIs only ever run in their own checkout.
/// </summary>
public sealed class GitWorkspace
{
    internal static readonly SemaphoreSlim GitMetadataGate = new(1, 1);
    private readonly RunnerOptions _options;
    private readonly Action<string> _log;
    private readonly string _safeTaskKey;
    private readonly string? _gitRemote;
    private readonly string? _gitPushRemote;
    private readonly string _baseBranch;
    private readonly string? _projectId;
    private readonly bool _isProjectClone;
    private readonly string _workBranch;
    private readonly string? _sourceRunAttemptId;
    private readonly long? _fencingToken;
    private string? _preparedIntegrationBranch;
    private string? _startedHead;
    private readonly string? _restoredBaseSha;
    private bool _startedFromSalvage;
    private SalvageReconciliationResult? _pickupReconciliation;

    private const int MaxSalvagePublishAttempts = 3;

    public GitWorkspace(
        RunnerOptions options,
        string taskKey,
        Action<string> log,
        string? projectId = null,
        string? gitRemote = null,
        string? defaultBranch = null,
        bool isProjectClone = false,
        string? restoredBaseSha = null,
        string? sourceRunAttemptId = null,
        long? fencingToken = null)
    {
        _options = options;
        _log = log;
        _restoredBaseSha = string.IsNullOrWhiteSpace(restoredBaseSha) ? null : restoredBaseSha.Trim();
        _safeTaskKey = SafeSegment(taskKey);
        _projectId = string.IsNullOrWhiteSpace(projectId) ? null : projectId.Trim();
        _isProjectClone = isProjectClone || _projectId is not null;
        var claimedRemote = string.IsNullOrWhiteSpace(gitRemote) ? null : gitRemote.Trim();
        _gitRemote = _isProjectClone ? claimedRemote : claimedRemote ?? options.GitRemote;
        _gitPushRemote = _isProjectClone
            ? claimedRemote
            : string.IsNullOrWhiteSpace(options.GitPushRemote) ? _gitRemote : options.GitPushRemote.Trim();
        _baseBranch = string.IsNullOrWhiteSpace(defaultBranch) ? options.BaseBranch : defaultBranch.Trim();
        _workBranch = $"runner/{SafeSegment(_options.RunnerId)}/{_safeTaskKey}";
        if (string.IsNullOrWhiteSpace(sourceRunAttemptId) != !fencingToken.HasValue)
        {
            throw new ArgumentException(
                "Run attempt ID and fencing token must be supplied together.");
        }
        if (fencingToken is <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(fencingToken),
                "A positive fencing token is required for generation-scoped Git refs.");
        _sourceRunAttemptId = string.IsNullOrWhiteSpace(sourceRunAttemptId)
            ? null
            : sourceRunAttemptId.Trim();
        _fencingToken = fencingToken;
    }

    public string ProjectCachePath => CachePathForProject(_options.WorkDir, _projectId);
    public string SharedRepoPath => Path.Combine(ProjectCachePath, "repo");
    public string RepoPath => Path.Combine(ProjectCachePath, "worktrees", _safeTaskKey);
    public string? RepositoryUrl => _gitRemote;
    /// <summary>Canonical branch that retains this task's salvage generation.</summary>
    public string WorkBranch => _workBranch;
    /// <summary>
    /// The commit this workspace started from - the Result-Envelope's BaseSha.
    /// On a reattach nothing in this process ran <see cref="PrepareAsync"/>, so the
    /// value is restored from durable slot state instead. It deliberately does not
    /// feed <c>_startedHead</c>: that field is also the teardown's "provably
    /// untouched checkout" marker, and a reattached run must keep inspecting the
    /// remote for retained work rather than trusting a start marker it did not
    /// observe itself.
    /// </summary>
    public string? BaseSha => _startedHead ?? _restoredBaseSha;
    public string IntegrationBranchRef =>
        $"refs/heads/{ToBranchName(_preparedIntegrationBranch ?? _baseBranch)}";
    public SalvageReconciliationResult? PickupReconciliation => _pickupReconciliation;

    public async Task<string> PrepareAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_gitRemote))
            throw MissingRepositoryUrl();

        Directory.CreateDirectory(ProjectCachePath);
        Directory.CreateDirectory(Path.Combine(ProjectCachePath, "worktrees"));

        await GitMetadataGate.WaitAsync(ct);
        try
        {
            var existingClone = Directory.Exists(Path.Combine(SharedRepoPath, ".git"));
            if (!existingClone)
            {
                _log($"git clone origin -> {SharedRepoPath}");
                await Git(["clone", _gitRemote, SharedRepoPath], ProjectCachePath, ct);
            }
            await ConfigureOriginAsync(ct);
            if (existingClone)
            {
                _log("git fetch origin --prune");
                await Git(["fetch", "origin", "--prune"], SharedRepoPath, ct);
            }
            // Reclaim debris from a process crash before creating the new
            // linked checkout. A crashed process may have left its only copy of
            // the work here, so the same salvage invariant as normal teardown
            // applies before anything is removed.
            if (Directory.Exists(RepoPath))
            {
                var retained = HasFencedGeneration
                    ? await SecureAndRemoveAsync(
                        "Unknown",
                        sourceRunAttemptId: null,
                        quarantine: true,
                        ct)
                    : await SecureAndRemoveAsync(
                        "Unknown",
                        sourceRunAttemptId: null,
                        quarantine: false,
                        ct);
                _pickupReconciliation = retained.Reconciliation;
            }
            await TryGit(["worktree", "prune"], SharedRepoPath, ct);

            // Resolve the pickup branch after retained work is secured. That
            // salvage may have created the canonical runner ref, which must be
            // the authoritative continuation base instead of the requested
            // project branch.
            var requested = string.IsNullOrWhiteSpace(_options.Branch) ? _baseBranch : _options.Branch!;
            var requestedBase = await BranchExistsOnOrigin(requested, ct)
                ? requested
                : await OriginDefaultBranch(ct) ?? _baseBranch;
            string branch;
            if (!HasFencedGeneration && await BranchExistsOnOrigin(_workBranch, ct))
            {
                branch = _workBranch;
                _log($"resuming task from existing salvage branch 'origin/{_workBranch}'");
            }
            else
            {
                branch = requestedBase;
            }
            _preparedIntegrationBranch = requestedBase;
            if (branch != requested && branch != _workBranch)
                _log($"branch '{requested}' not found on origin; falling back to base branch '{branch}'");

            await TryGit(["branch", "-D", _workBranch], SharedRepoPath, ct);
            var authoritativeBase = await FetchRemoteBranchHeadAsync(branch, ct)
                ?? throw new InvalidOperationException($"Authoritative pickup branch 'origin/{branch}' disappeared during preparation.");
            _log($"worktree-authoritative-base branch=refs/heads/{branch} sha={authoritativeBase} path={RepoPath}");
            _log($"git worktree add {RepoPath} on {_workBranch} from refs/heads/{branch} at {ShortSha(authoritativeBase)}");
            await Git(["worktree", "add", "-B", _workBranch, RepoPath, authoritativeBase], SharedRepoPath, ct);

            _startedFromSalvage = string.Equals(branch, _workBranch, StringComparison.Ordinal);
            _startedHead = (await Git(["rev-parse", "HEAD"], RepoPath, ct)).StdOut.Trim();
            _log($"task worktree ready on '{_workBranch}' at {ShortSha(_startedHead)}");
            return branch;
        }
        finally
        {
            GitMetadataGate.Release();
        }
    }

    private static string ToBranchName(string value)
    {
        var branch = value.Trim();
        if (branch.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase))
            return branch["refs/heads/".Length..];
        if (branch.StartsWith("origin/", StringComparison.OrdinalIgnoreCase))
            return branch["origin/".Length..];
        return branch;
    }

    /// <summary>
    /// Proves the registered fetch and push path before the server leases the
    /// first card for this host/project pair. It prepares the same shared clone
    /// later task worktrees use, so a green result is not a disposable probe of
    /// a different path.
    /// </summary>
    public static async Task<ProjectDeliveryPreflightResult> PreflightProjectAsync(
        RunnerOptions options,
        string projectId,
        string repositoryUrl,
        string defaultBranch,
        Action<string> log,
        CancellationToken ct)
    {
        var expected = repositoryUrl.Trim();
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(expected)
            || string.IsNullOrWhiteSpace(defaultBranch))
            return new(false, expected, expected, "project id and repository URL are required");

        var projectPath = CachePathForProject(options.WorkDir, projectId);
        var sharedRepoPath = Path.Combine(projectPath, "repo");
        string? probeRef = null;
        var probeMayExist = false;
        Directory.CreateDirectory(projectPath);

        await GitMetadataGate.WaitAsync(ct);
        try
        {
            if (!Directory.Exists(Path.Combine(sharedRepoPath, ".git")))
            {
                log($"project-delivery-preflight clone project={projectId} repository={expected}");
                var clone = await ProcessRunner.RunAsync(
                    "git", ["clone", "--no-checkout", expected, sharedRepoPath], projectPath, ct: ct);
                if (!clone.Success) return Failed("clone", clone, expected, expected);
            }

            var fetchSet = await ProcessRunner.RunAsync(
                "git", ["remote", "set-url", "origin", expected], sharedRepoPath, ct: ct);
            if (!fetchSet.Success) return Failed("set fetch URL", fetchSet, expected, expected);
            var pushSet = await ProcessRunner.RunAsync(
                "git", ["remote", "set-url", "--push", "origin", expected], sharedRepoPath, ct: ct);
            if (!pushSet.Success) return Failed("set push URL", pushSet, expected, expected);

            var fetch = await ProcessRunner.RunAsync(
                "git", ["remote", "get-url", "origin"], sharedRepoPath, ct: ct);
            if (!fetch.Success) return Failed("read fetch URL", fetch, expected, expected);
            var push = await ProcessRunner.RunAsync(
                "git", ["remote", "get-url", "--push", "origin"], sharedRepoPath, ct: ct);
            if (!push.Success) return Failed("read push URL", push, fetch.StdOut.Trim(), expected);
            var fetchUrl = fetch.StdOut.Trim();
            var pushUrl = push.StdOut.Trim();
            if (!SameRemote(fetchUrl, expected) || !SameRemote(pushUrl, expected))
                return new(false, fetchUrl, pushUrl,
                    $"fetch/push URL mismatch: registered={expected}, fetch={fetchUrl}, push={pushUrl}");

            var fetchResult = await ProcessRunner.RunAsync(
                "git", ["fetch", "origin", "--prune"], sharedRepoPath, ct: ct);
            if (!fetchResult.Success) return Failed("fetch", fetchResult, fetchUrl, pushUrl);

            probeRef = $"refs/heads/runner/{SafeSegment(options.RunnerId)}/delivery-preflight-{Guid.NewGuid():N}";
            var sourceRef = $"refs/remotes/origin/{defaultBranch.Trim()}";
            var source = await ProcessRunner.RunAsync(
                "git", ["show-ref", "--verify", "--quiet", sourceRef], sharedRepoPath, ct: ct);
            if (!source.Success)
                return new(
                    false,
                    fetchUrl,
                    pushUrl,
                    $"target branch '{defaultBranch.Trim()}' does not exist on the registered repository");
            probeMayExist = true;
            var writable = await ProcessRunner.RunAsync(
                "git", ["push", "origin", $"{sourceRef}:{probeRef}"], sharedRepoPath, ct: ct);
            if (!writable.Success)
                return Failed("write probe", writable, fetchUrl, pushUrl);

            var cleanup = await ProcessRunner.RunAsync(
                "git", ["push", "origin", $":{probeRef}"], sharedRepoPath, ct: ct);
            probeMayExist = !cleanup.Success;
            return cleanup.Success
                ? new(
                    true,
                    fetchUrl,
                    pushUrl,
                    $"clone/fetch URLs match registration; target branch '{defaultBranch.Trim()}' exists; write probe created and removed {probeRef}")
                : Failed("write probe cleanup", cleanup, fetchUrl, pushUrl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(false, expected, expected, $"preflight exception: {OneLine(ex.Message)}");
        }
        finally
        {
            if (probeMayExist && probeRef is not null
                && Directory.Exists(Path.Combine(sharedRepoPath, ".git")))
            {
                var cleanup = await ProcessRunner.RunAsync(
                    "git", ["push", "origin", $":{probeRef}"], sharedRepoPath, ct: CancellationToken.None);
                if (!cleanup.Success)
                    log($"project-delivery-preflight-cleanup-failed project={projectId} ref={probeRef} error={OneLine(cleanup.StdErr)}");
            }
            GitMetadataGate.Release();
        }
    }

    private static ProjectDeliveryPreflightResult Failed(
        string stage, ProcessResult result, string fetchUrl, string pushUrl) =>
        new(false, fetchUrl, pushUrl,
            $"{stage} failed ({result.ExitCode}): {OneLine(string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr)}");

    private static bool SameRemote(string actual, string expected) =>
        string.Equals(actual.Trim().TrimEnd('/'), expected.Trim().TrimEnd('/'), StringComparison.Ordinal);

    /// <summary>
    /// Prepare a detached, disposable checkout for an Epic planning run. No
    /// task branch is created or resumed and teardown never commits or pushes.
    /// </summary>
    public async Task<string> PrepareReadOnlyAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_gitRemote))
            throw MissingRepositoryUrl();

        Directory.CreateDirectory(ProjectCachePath);
        Directory.CreateDirectory(Path.Combine(ProjectCachePath, "worktrees"));
        await GitMetadataGate.WaitAsync(ct);
        try
        {
            var existingClone = Directory.Exists(Path.Combine(SharedRepoPath, ".git"));
            if (!existingClone)
                await Git(["clone", _gitRemote, SharedRepoPath], ProjectCachePath, ct);
            await ConfigureOriginAsync(ct);
            if (existingClone)
            {
                await Git(["fetch", "origin", "--prune"], SharedRepoPath, ct);
            }

            if (Directory.Exists(RepoPath))
            {
                await WorktreeProcessReaper.ReapAsync(RepoPath, _log, ct);
                await Git(["worktree", "remove", "--force", RepoPath], SharedRepoPath, ct);
            }
            await TryGit(["worktree", "prune"], SharedRepoPath, ct);

            var requested = string.IsNullOrWhiteSpace(_options.Branch) ? _baseBranch : _options.Branch!;
            var branch = await BranchExistsOnOrigin(requested, ct)
                ? requested
                : await OriginDefaultBranch(ct) ?? _baseBranch;
            _log($"git worktree add --detach {RepoPath} from origin/{branch} for read-only Epic planning");
            await Git(["worktree", "add", "--detach", RepoPath, $"origin/{branch}"], SharedRepoPath, ct);
            return branch;
        }
        finally
        {
            GitMetadataGate.Release();
        }
    }

    private async Task ConfigureOriginAsync(CancellationToken ct)
    {
        var fetchUrl = _gitRemote
            ?? throw new InvalidOperationException("Cannot configure origin without a repository URL.");
        // Enforce the project-clone invariant at the mutation point as well as
        // in constructor resolution. A later caller or fallback change must
        // never make a project push anywhere except its registered fetch URL.
        var pushUrl = _isProjectClone ? fetchUrl : _gitPushRemote ?? fetchUrl;
        var source = _isProjectClone ? "project-registry" : "runner-fallback";
        // replace-all repairs clones that accumulated more than one stale fetch
        // or push URL. `remote set-url` refuses either multi-value configuration.
        await Git(["config", "--replace-all", "remote.origin.url", fetchUrl], SharedRepoPath, ct);
        await Git(["config", "--replace-all", "remote.origin.pushurl", pushUrl], SharedRepoPath, ct);
        _log(
            $"git-remote-configured projectId={_projectId ?? "legacy"} source={source} " +
            $"fetchUrl={fetchUrl} pushUrl={pushUrl}");
    }

    private InvalidOperationException MissingRepositoryUrl()
        => _isProjectClone
            ? new InvalidOperationException(
                $"Project '{_projectId ?? "unknown"}' is not remote-capable because its registry has no repository URL.")
            : new InvalidOperationException(
                "The one-shot run has no repositoryUrl and RUNNER_GIT_REMOTE is not configured as a fallback.");

    /// <summary>
    /// Remove a planning checkout without salvage. Returns true when the agent
    /// changed tracked or untracked product-source files, which invalidates the
    /// planning result but is never committed or pushed.
    /// </summary>
    public async Task<bool> TeardownReadOnlyAsync(CancellationToken ct)
    {
        await GitMetadataGate.WaitAsync(ct);
        try
        {
            if (!Directory.Exists(RepoPath)) return false;
            var status = (await Git(["status", "--porcelain=v1", "--untracked-files=all"], RepoPath, ct)).StdOut;
            var mutated = !string.IsNullOrWhiteSpace(status);
            _log($"epic-planning-checkout-teardown path={RepoPath} sourceMutated={mutated}");
            await WorktreeProcessReaper.ReapAsync(RepoPath, _log, ct);
            await Git(["worktree", "remove", "--force", RepoPath], SharedRepoPath, ct);
            await TryGit(["worktree", "prune"], SharedRepoPath, ct);
            return mutated;
        }
        finally
        {
            GitMetadataGate.Release();
        }
    }

    public Task<WorktreeTeardownResult> TeardownAsync(string outcome, CancellationToken ct)
        => TeardownAsync(outcome, sourceRunAttemptId: null, ct);

    public async Task<WorktreeTeardownResult> TeardownAsync(
        string outcome,
        string? sourceRunAttemptId,
        CancellationToken ct)
    {
        await GitMetadataGate.WaitAsync(ct);
        try
        {
            try
            {
                var secured = await SecureAndRemoveAsync(outcome, sourceRunAttemptId, ct);
                return secured with { Reconciliation = _pickupReconciliation ?? secured.Reconciliation };
            }
            catch (WorktreeSalvageException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log($"worktree-salvage-failed branch={_workBranch} path={RepoPath} error={OneLine(ex.Message)}");
                throw new WorktreeSalvageException(RepoPath, _workBranch, ex);
            }
        }
        finally
        {
            GitMetadataGate.Release();
        }
    }

    /// <summary>
    /// Preserves a fenced-out generation without publishing a delivery ref.
    /// Quarantine refs are generation- and SHA-specific, so a late runner can
    /// never replace the current attempt's salvage or result identity.
    /// </summary>
    public async Task<WorktreeTeardownResult> TeardownToQuarantineAsync(
        string outcome,
        string? sourceRunAttemptId,
        CancellationToken ct)
    {
        await GitMetadataGate.WaitAsync(ct);
        try
        {
            try
            {
                return await SecureAndRemoveAsync(
                    outcome,
                    sourceRunAttemptId,
                    quarantine: true,
                    ct);
            }
            catch (WorktreeSalvageException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log($"worktree-quarantine-failed branch={_workBranch} path={RepoPath} error={OneLine(ex.Message)}");
                throw new WorktreeSalvageException(RepoPath, _workBranch, ex);
            }
        }
        finally
        {
            GitMetadataGate.Release();
        }
    }

    /// <summary>
    /// Secure the result on both the salvage ref and a run-scoped immutable ref,
    /// but keep the worktree present until the Task Server acknowledges the
    /// matching result envelope.
    /// </summary>
    public async Task<WorktreeTeardownResult> SecureForHandoffAsync(
        string outcome,
        string sourceRunAttemptId,
        CancellationToken ct)
    {
        await GitMetadataGate.WaitAsync(ct);
        try
        {
            try
            {
                return await SecureAsync(outcome, sourceRunAttemptId, removeAfterSecure: false,
                    immutableRefRequired: true, quarantine: false, ct);
            }
            catch (WorktreeSalvageException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log($"worktree-transfer-failed branch={_workBranch} path={RepoPath} error={OneLine(ex.Message)}");
                throw new WorktreeSalvageException(RepoPath, _workBranch, ex);
            }
        }
        finally
        {
            GitMetadataGate.Release();
        }
    }

    public async Task TeardownAfterHandoffAsync(
        WorktreeTeardownResult secured,
        ResultHandoffAck acknowledgement,
        string expectedRunId,
        string expectedEnvelopeDigest,
        CancellationToken ct)
    {
        new DurableHandoffGate(
            expectedRunId,
            expectedEnvelopeDigest).RequireAcknowledged(acknowledgement);
        await GitMetadataGate.WaitAsync(ct);
        try
        {
            if (!Directory.Exists(RepoPath)) return;
            var head = (await Git(["rev-parse", "HEAD"], RepoPath, ct)).StdOut.Trim();
            if (!string.Equals(head, secured.ResultSha, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Worktree HEAD changed after handoff: expected '{secured.ResultSha}', found '{head}'.");
            }
            var status = (await Git(
                ["status", "--porcelain=v1", "--untracked-files=all"],
                RepoPath,
                ct)).StdOut;
            if (!string.IsNullOrWhiteSpace(status))
            {
                throw new InvalidOperationException(
                    "Worktree changed after handoff acknowledgement; cleanup is blocked so the unjournaled work remains recoverable.");
            }
            await RemoveSecuredWorktreeAsync(secured.SecuredWork, ct);
        }
        finally
        {
            GitMetadataGate.Release();
        }
    }

    public async Task<WorkspaceDependencyIdentities> ReadDependencyIdentitiesAsync(
        CancellationToken ct)
    {
        var submodules = new List<ResultDependencyIdentity>();
        if (File.Exists(Path.Combine(RepoPath, ".gitmodules")))
        {
            var result = await ProcessRunner.RunAsync(
                "git",
                ["submodule", "status", "--recursive"],
                workingDirectory: RepoPath,
                ct: ct);
            if (!result.Success)
                throw new InvalidOperationException(
                    $"git submodule status failed ({result.ExitCode}): {result.StdErr.Trim()}");
            foreach (var line in result.StdOut.Split(
                         ['\r', '\n'],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = line.TrimStart(' ', '+', '-', 'U')
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length >= 2)
                    submodules.Add(new ResultDependencyIdentity(fields[1], fields[0]));
            }
        }

        var lfsObjects = new List<ResultDependencyIdentity>();
        var attributes = Directory.EnumerateFiles(
                RepoPath,
                ".gitattributes",
                SearchOption.AllDirectories)
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .ToArray();
        var usesLfs = attributes.Any(path =>
            File.ReadLines(path).Any(line => line.Contains("filter=lfs", StringComparison.Ordinal)));
        if (usesLfs)
        {
            var result = await ProcessRunner.RunAsync(
                "git",
                ["lfs", "ls-files", "--long"],
                workingDirectory: RepoPath,
                ct: ct);
            if (!result.Success)
                throw new InvalidOperationException(
                    $"git lfs ls-files failed ({result.ExitCode}): {result.StdErr.Trim()}");
            foreach (var line in result.StdOut.Split(
                         ['\r', '\n'],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = line.IndexOf(" - ", StringComparison.Ordinal);
                if (separator < 0) separator = line.IndexOf(" * ", StringComparison.Ordinal);
                if (separator <= 0) continue;
                var objectId = line[..separator].Trim();
                var path = line[(separator + 3)..].Trim();
                lfsObjects.Add(new ResultDependencyIdentity(path, objectId));
            }
        }

        return new WorkspaceDependencyIdentities(submodules, lfsObjects);
    }

    private async Task<WorktreeTeardownResult> SecureAndRemoveAsync(
        string outcome,
        string? sourceRunAttemptId,
        CancellationToken ct)
        => await SecureAndRemoveAsync(
            outcome,
            sourceRunAttemptId,
            quarantine: false,
            ct);

    private async Task<WorktreeTeardownResult> SecureAndRemoveAsync(
        string outcome,
        string? sourceRunAttemptId,
        bool quarantine,
        CancellationToken ct)
        => await SecureAsync(
            outcome,
            sourceRunAttemptId,
            removeAfterSecure: true,
            immutableRefRequired: false,
            quarantine,
            ct);

    private async Task<WorktreeTeardownResult> SecureAsync(
        string outcome,
        string? sourceRunAttemptId,
        bool removeAfterSecure,
        bool immutableRefRequired,
        bool quarantine,
        CancellationToken ct)
    {
        if (HasFencedGeneration
            && !string.IsNullOrWhiteSpace(sourceRunAttemptId)
            && !string.Equals(
                sourceRunAttemptId,
                _sourceRunAttemptId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Workspace generation '{_sourceRunAttemptId}' cannot publish for " +
                $"run attempt '{sourceRunAttemptId}'.");
        }
        if (!Directory.Exists(RepoPath))
        {
            await TryGit(["worktree", "prune"], SharedRepoPath, ct);
            return WorktreeTeardownResult.NoResult;
        }

        var status = (await Git(["status", "--porcelain=v1", "--untracked-files=all"], RepoPath, ct)).StdOut;
        var wasDirty = !string.IsNullOrWhiteSpace(status);
        _log($"worktree-salvage-status path={RepoPath} dirty={wasDirty} outcome={outcome}");

        if (wasDirty)
        {
            await Git(["add", "--all"], RepoPath, ct);
            await Git([
                "-c", "user.name=Agent Studio Runner",
                "-c", "user.email=runner@agent-studio.invalid",
                "commit", "-m", $"wip(runner): salvage before teardown - outcome {outcome}"
            ], RepoPath, ct);
            _log($"worktree-salvage-commit-created path={RepoPath} outcome={outcome}");
        }

        var head = (await Git(["rev-parse", "HEAD"], RepoPath, ct)).StdOut.Trim();
        var salvageBranch = quarantine
            ? QuarantineBranch(head, sourceRunAttemptId)
            : FencedSalvageBranch(head) ?? _workBranch;
        var changedDuringRun = _startedHead is not null
            && !string.Equals(_startedHead, head, StringComparison.OrdinalIgnoreCase);
        var hasWork = wasDirty || changedDuringRun || _startedFromSalvage;
        string? remoteHead = null;
        SalvageReconciliationResult? reconciliation = null;
        RemoteDeliveryProof? deliveryProof = null;

        // A checkout which is still exactly at its recorded start commit is
        // provably clean and needs no remote query. Crash debris has no start
        // marker, so inspect both reachability and the durable runner ref.
        if (hasWork || _startedHead is null)
        {
            try
            {
                var hasLocalOnlyCommits = await HasLocalOnlyCommitsAsync(ct);
                remoteHead = await RemoteBranchHeadAsync(salvageBranch, ct);
                hasWork = hasWork || hasLocalOnlyCommits || remoteHead is not null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log($"worktree-salvage-remote-check-failed branch={salvageBranch} path={RepoPath} error={OneLine(ex.Message)}");
                throw new WorktreeSalvageException(RepoPath, salvageBranch, ex);
            }
        }

        var mustPublishFence = hasWork || HasFencedGeneration || quarantine;
        if (mustPublishFence)
        {
            try
            {
                // The salvage fence is the recovery contract. Publish and
                // verify it before the immutable result ref is attempted and
                // before any worktree removal can run. A later envelope failure
                // may therefore requeue with a durable branch reference.
                reconciliation = HasFencedGeneration || quarantine
                    ? await PublishImmutableSalvageAsync(
                        salvageBranch,
                        head,
                        remoteHead,
                        quarantine,
                        ct)
                    : await ReconcileSalvageAsync(head, remoteHead, ct);
                var verifiedBranch = reconciliation.RecoveryBranch ?? reconciliation.CanonicalBranch;
                var verifiedCommit = reconciliation.RecoveryCommitSha ?? reconciliation.CanonicalCommitSha;
                deliveryProof = new RemoteDeliveryProof(
                    RegisteredRepositoryUrl(),
                    $"refs/heads/{verifiedBranch}",
                    verifiedCommit);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log($"worktree-salvage-push-failed branch={salvageBranch} path={RepoPath} error={OneLine(ex.Message)}");
                throw ex as WorktreeSalvageException
                    ?? new WorktreeSalvageException(RepoPath, salvageBranch, ex, head, remoteHead);
            }
        }

        string? immutableResultRef = null;
        if (!quarantine && !string.IsNullOrWhiteSpace(sourceRunAttemptId))
        {
            var candidateRef = _fencingToken.HasValue
                ? FencedGitRefs.ImmutableResult(
                    sourceRunAttemptId,
                    _fencingToken.Value,
                    head)
                : $"refs/heads/agent-studio/results/{SafeSegment(sourceRunAttemptId)}/{head.ToLowerInvariant()}";
            try
            {
                await PushImmutableResultAndVerifyAsync(candidateRef, head, ct);
                immutableResultRef = candidateRef;
                deliveryProof = new RemoteDeliveryProof(
                    RegisteredRepositoryUrl(),
                    candidateRef,
                    head);
            }
            catch (Exception ex) when (!immutableRefRequired && ex is not OperationCanceledException)
            {
                // On the legacy completion path the immutable ref is best-effort
                // evidence. Without it the request carries no result envelope,
                // so the server records delivery-failed and requeues once before
                // escalating. The already verified salvage fence remains the
                // delivery proof and recovery source.
                _log($"immutable-result-ref-push-failed ref={candidateRef} path={RepoPath} error={OneLine(ex.Message)}; completing without result envelope");
            }
        }
        if (removeAfterSecure)
            await RemoveSecuredWorktreeAsync(hasWork, ct);
        else
            _log($"worktree-handoff-secured path={RepoPath} resultSha={head} immutableRef={immutableResultRef}");

        return reconciliation is not null
            ? new WorktreeTeardownResult(hasWork, salvageBranch,
                reconciliation?.AuthoritativeBaseSha ?? head,
                BuildBranchUrl(_gitRemote!, salvageBranch),
                ResultSha: head,
                Reconciliation: reconciliation,
                ImmutableResultRef: immutableResultRef,
                DeliveryProof: deliveryProof)
            : new WorktreeTeardownResult(
                false,
                null,
                null,
                null,
                ResultSha: head,
                ImmutableResultRef: immutableResultRef,
                DeliveryProof: deliveryProof);
    }

    private async Task<SalvageReconciliationResult> PublishImmutableSalvageAsync(
        string targetBranch,
        string localHead,
        string? remoteHead,
        bool quarantine,
        CancellationToken ct)
    {
        if (remoteHead is null)
        {
            await PushAndVerifyAsync(targetBranch, localHead, ct);
        }
        else if (!string.Equals(remoteHead, localHead, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Immutable salvage ref 'refs/heads/{targetBranch}' already resolves to " +
                $"'{remoteHead}', expected '{localHead}'.");
        }

        var kind = quarantine ? "quarantined" : "generation-scoped";
        var result = new SalvageReconciliationResult(
            kind,
            targetBranch,
            localHead,
            localHead,
            null,
            null,
            targetBranch,
            localHead);
        _log(
            $"worktree-salvage-reconciled kind={kind} " +
            $"canonicalRef=refs/heads/{targetBranch} canonicalSha={localHead} " +
            $"localSha={localHead} recoveryRef=none recoverySha=none " +
            $"authoritativeBaseRef=refs/heads/{targetBranch} authoritativeBaseSha={localHead}");
        return result;
    }

    private async Task RemoveSecuredWorktreeAsync(bool securedWork, CancellationToken ct)
    {
        await WorktreeProcessReaper.ReapAsync(RepoPath, _log, ct);
        await Git(["worktree", "remove", "--force", RepoPath], SharedRepoPath, ct);
        await TryGit(["worktree", "prune"], SharedRepoPath, ct);
        await TryGit(["branch", "-D", _workBranch], SharedRepoPath, ct);
        _log($"worktree-teardown-completed path={RepoPath} secured={securedWork} branch={(securedWork ? _workBranch : "none")}");
    }

    private async Task<SalvageReconciliationResult> ReconcileSalvageAsync(
        string localHead, string? observedRemoteHead, CancellationToken ct)
    {
        Exception? lastError = null;
        var remoteHead = observedRemoteHead;
        for (var attempt = 1; attempt <= MaxSalvagePublishAttempts; attempt++)
        {
            try
            {
                remoteHead = await FetchRemoteBranchHeadAsync(_workBranch, ct);
                if (remoteHead is null)
                {
                    await PushAndVerifyAsync(_workBranch, localHead, ct);
                    return Reconciliation("local-ahead", localHead, localHead);
                }

                if (string.Equals(remoteHead, localHead, StringComparison.OrdinalIgnoreCase))
                    return Reconciliation("equal", remoteHead, localHead);

                if (await IsAncestorAsync(remoteHead, localHead, ct))
                {
                    await PushAndVerifyAsync(_workBranch, localHead, ct);
                    return Reconciliation("local-ahead", localHead, localHead);
                }

                if (await IsAncestorAsync(localHead, remoteHead, ct))
                    return Reconciliation("remote-ahead", remoteHead, localHead);

                var recoveryBranch = RecoveryBranch(localHead, remoteHead);
                await PushAndVerifyAsync(recoveryBranch, localHead, ct);
                var result = new SalvageReconciliationResult(
                    "divergent", _workBranch, remoteHead, localHead,
                    recoveryBranch, localHead, _workBranch, remoteHead);
                _log($"worktree-salvage-reconciled kind=divergent canonicalRef=refs/heads/{_workBranch} canonicalSha={remoteHead} localSha={localHead} recoveryRef=refs/heads/{recoveryBranch} recoverySha={localHead} authoritativeBaseRef=refs/heads/{_workBranch} authoritativeBaseSha={remoteHead}");
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                _log($"worktree-salvage-publish-retry branch={_workBranch} localSha={localHead} remoteSha={remoteHead ?? "missing"} attempt={attempt}/{MaxSalvagePublishAttempts} error={OneLine(ex.Message)}");
            }
        }

        throw new WorktreeSalvageException(
            RepoPath, _workBranch,
            lastError ?? new InvalidOperationException("Salvage reconciliation exhausted its retry budget."),
            localHead, remoteHead);

        SalvageReconciliationResult Reconciliation(string kind, string canonicalHead, string retainedLocalHead)
        {
            var result = new SalvageReconciliationResult(
                kind, _workBranch, canonicalHead, retainedLocalHead,
                null, null, _workBranch, canonicalHead);
            _log($"worktree-salvage-reconciled kind={kind} canonicalRef=refs/heads/{_workBranch} canonicalSha={canonicalHead} localSha={retainedLocalHead} recoveryRef=none recoverySha=none authoritativeBaseRef=refs/heads/{_workBranch} authoritativeBaseSha={canonicalHead}");
            return result;
        }
    }

    private async Task PushAndVerifyAsync(string branch, string expectedHead, CancellationToken ct)
    {
        if (!IsAllowedSalvageTarget(branch, expectedHead))
        {
            throw new InvalidOperationException(
                $"Refusing salvage push to non-card branch '{branch}'. " +
                "Allowed targets are the exact fenced salvage or quarantine ref, " +
                $"or legacy '{_workBranch}' collision refs.");
        }
        _log($"worktree-salvage-push-started branch={branch} sha={ShortSha(expectedHead)} path={RepoPath}");
        await Git(["push", "origin", $"HEAD:refs/heads/{branch}"], RepoPath, ct);
        var published = await RemoteBranchHeadAsync(branch, ct);
        if (!string.Equals(published, expectedHead, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Registered project repository ref 'refs/heads/{branch}' resolved to " +
                $"'{published ?? "missing"}' after push, expected '{expectedHead}'.");
        _log($"worktree-salvage-push-completed branch={branch} sha={ShortSha(expectedHead)} path={RepoPath}");
    }

    private bool IsAllowedSalvageTarget(string targetBranch, string expectedHead)
    {
        if (!HasFencedGeneration
            && IsCardScopedSalvageTarget(_workBranch, targetBranch))
            return true;
        var expectedFenced = FencedSalvageBranch(expectedHead);
        if (expectedFenced is not null
            && string.Equals(targetBranch, expectedFenced, StringComparison.Ordinal))
            return true;
        return string.Equals(
            targetBranch,
            QuarantineBranch(expectedHead, _sourceRunAttemptId),
            StringComparison.Ordinal)
            || string.Equals(
                targetBranch,
                QuarantineBranch(expectedHead, sourceRunAttemptId: null),
                StringComparison.Ordinal);
    }

    internal static bool IsCardScopedSalvageTarget(string cardBranch, string targetBranch)
    {
        var segments = cardBranch.Split('/');
        if (segments.Length != 3
            || !string.Equals(segments[0], "runner", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(segments[1])
            || string.IsNullOrWhiteSpace(segments[2]))
            return false;

        return string.Equals(targetBranch, cardBranch, StringComparison.Ordinal)
            || targetBranch.StartsWith(cardBranch + "-collision-", StringComparison.Ordinal);
    }

    private async Task PushImmutableResultAndVerifyAsync(
        string immutableRef,
        string expectedHead,
        CancellationToken ct)
    {
        _log($"result-transfer-started ref={immutableRef} sha={expectedHead} path={RepoPath}");
        await Git(["push", "origin", $"HEAD:{immutableRef}"], RepoPath, ct);
        var result = await ProcessRunner.RunAsync(
            "git",
            ["ls-remote", RegisteredRepositoryUrl(), immutableRef],
            workingDirectory: SharedRepoPath,
            ct: ct);
        if (!result.Success)
            throw new InvalidOperationException(
                $"git ls-remote against the registered project repository for {immutableRef} " +
                $"failed ({result.ExitCode}): {result.StdErr.Trim()}");
        var published = result.StdOut.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (!string.Equals(published, expectedHead, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Immutable result ref '{immutableRef}' resolved to '{published ?? "missing"}', expected '{expectedHead}'.");
        }
        _log($"result-transfer-completed ref={immutableRef} sha={expectedHead} path={RepoPath}");
    }

    private async Task<bool> IsAncestorAsync(string ancestor, string descendant, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(
            "git", ["merge-base", "--is-ancestor", ancestor, descendant], workingDirectory: RepoPath, ct: ct);
        if (result.ExitCode == 0) return true;
        if (result.ExitCode == 1) return false;
        throw new InvalidOperationException(
            $"git merge-base --is-ancestor {ancestor} {descendant} failed ({result.ExitCode}): {result.StdErr.Trim()}");
    }

    private string RecoveryBranch(string localHead, string remoteHead)
        => $"{_workBranch}-collision-{localHead}-{remoteHead}";

    private bool HasFencedGeneration
        => _sourceRunAttemptId is not null && _fencingToken.HasValue;

    private string? FencedSalvageBranch(string head)
        => !HasFencedGeneration
            ? null
            : $"agent-studio/salvage/{SafeSegment(_options.RunnerId)}/{_safeTaskKey}/" +
              $"{SafeSegment(_sourceRunAttemptId!)}/fence-{_fencingToken!.Value}/" +
              head.ToLowerInvariant();

    private string QuarantineBranch(string head, string? sourceRunAttemptId)
    {
        var attempt = string.IsNullOrWhiteSpace(sourceRunAttemptId)
            ? "unknown-generation"
            : SafeSegment(sourceRunAttemptId);
        var fence = _fencingToken.HasValue
            && string.Equals(sourceRunAttemptId, _sourceRunAttemptId, StringComparison.Ordinal)
                ? $"fence-{_fencingToken.Value}"
                : "fence-unknown";
        return $"agent-studio/quarantine/{SafeSegment(_options.RunnerId)}/{_safeTaskKey}/" +
               $"{attempt}/{fence}/{head.ToLowerInvariant()}";
    }

    private async Task<bool> HasLocalOnlyCommitsAsync(CancellationToken ct)
    {
        var result = await Git(["rev-list", "--count", "HEAD", "--not", "--remotes=origin"], RepoPath, ct);
        return int.TryParse(result.StdOut.Trim(), out var count) && count > 0;
    }

    private async Task<string?> RemoteBranchHeadAsync(string branch, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(
            "git",
            ["ls-remote", "--heads", RegisteredRepositoryUrl(), $"refs/heads/{branch}"],
            workingDirectory: SharedRepoPath, ct: ct);
        if (!result.Success)
            throw new InvalidOperationException(
                $"git ls-remote against the registered project repository for {branch} " +
                $"failed ({result.ExitCode}): {result.StdErr.Trim()}");
        var first = result.StdOut.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(first) ? null : first;
    }

    private async Task<string?> FetchRemoteBranchHeadAsync(string branch, CancellationToken ct)
    {
        var remoteHead = await RemoteBranchHeadAsync(branch, ct);
        if (remoteHead is null) return null;
        await Git(["fetch", "origin", $"refs/heads/{branch}"], SharedRepoPath, ct);
        var fetched = (await Git(["rev-parse", "FETCH_HEAD"], SharedRepoPath, ct)).StdOut.Trim();
        if (!string.Equals(fetched, remoteHead, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"origin/{branch} changed while fetching: observed '{remoteHead}', fetched '{fetched}'.");
        return fetched;
    }

    private async Task<bool> BranchExistsOnOrigin(string branch, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(
            "git",
            ["ls-remote", "--heads", RegisteredRepositoryUrl(), branch],
            workingDirectory: SharedRepoPath,
            ct: ct);
        return result.Success && result.StdOut.Contains($"refs/heads/{branch}", StringComparison.Ordinal);
    }

    private string RegisteredRepositoryUrl()
        => _gitRemote
           ?? throw new InvalidOperationException(
               "Cannot verify delivery because the project registration has no repository URL.");

    private async Task<string?> OriginDefaultBranch(CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(
            "git", ["symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD"],
            workingDirectory: SharedRepoPath, ct: ct);
        if (!result.Success) return null;
        const string prefix = "origin/";
        var branch = result.StdOut.Trim();
        return branch.StartsWith(prefix, StringComparison.Ordinal) ? branch[prefix.Length..] : null;
    }

    private static async Task<ProcessResult> Git(IReadOnlyList<string> args, string workingDirectory, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync("git", args, workingDirectory: workingDirectory, ct: ct);
        if (!result.Success)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({result.ExitCode}): {result.StdErr.Trim()}");
        return result;
    }

    private static async Task TryGit(IReadOnlyList<string> args, string workingDirectory, CancellationToken ct)
    {
        if (!Directory.Exists(workingDirectory)) return;
        await ProcessRunner.RunAsync("git", args, workingDirectory: workingDirectory, ct: ct);
    }

    private static string? BuildBranchUrl(string remote, string branch)
    {
        var value = remote.Trim();
        if (value.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
            value = "https://github.com/" + value["git@github.com:".Length..];
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            return null;
        value = value.TrimEnd('/');
        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            value = value[..^4];
        return value + "/tree/" + branch;
    }

    private static string ShortSha(string sha) => sha.Length > 8 ? sha[..8] : sha;
    private static string OneLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    internal static string SafeSegment(string value)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray();
        var safe = new string(chars).Trim('-', '.');
        return safe.Length == 0 ? "task" : safe;
    }

    internal static string CachePathForProject(string workDir, string? projectId)
        => string.IsNullOrWhiteSpace(projectId)
            ? workDir
            : Path.Combine(workDir, SafeSegment(projectId));
}

public sealed record ProjectDeliveryPreflightResult(
    bool Succeeded,
    string FetchUrl,
    string PushUrl,
    string Detail);

public sealed record WorktreeTeardownResult(
    bool SecuredWork,
    string? Branch,
    string? CommitSha,
    string? BranchUrl,
    string? ResultSha = null,
    SalvageReconciliationResult? Reconciliation = null,
    string? ImmutableResultRef = null,
    RemoteDeliveryProof? DeliveryProof = null)
{
    public static WorktreeTeardownResult NoWork { get; } = new(false, null, null, null, null);
    public static WorktreeTeardownResult NoResult { get; } = new(false, null, null, null, null);
    public static WorktreeTeardownResult Clean(string resultSha) => new(false, null, null, null, resultSha);
}

/// <summary>
/// Exact remote evidence captured only after <c>git ls-remote</c> against the
/// project registration resolved the delivered ref to the expected commit.
/// </summary>
public sealed record RemoteDeliveryProof(
    string RepositoryUrl,
    string Ref,
    string CommitSha);

public sealed record SalvageReconciliationResult(
    string Kind,
    string CanonicalBranch,
    string CanonicalCommitSha,
    string LocalCommitSha,
    string? RecoveryBranch,
    string? RecoveryCommitSha,
    string AuthoritativeBaseBranch,
    string AuthoritativeBaseSha);

public sealed record WorkspaceDependencyIdentities(
    IReadOnlyList<ResultDependencyIdentity> Submodules,
    IReadOnlyList<ResultDependencyIdentity> LfsObjects);

public sealed class WorktreeSalvageException : Exception
{
    public WorktreeSalvageException(
        string worktreePath,
        string branch,
        Exception innerException,
        string? localCommitSha = null,
        string? remoteCommitSha = null)
        : base($"Could not secure worktree '{worktreePath}' on origin branch '{branch}'.", innerException)
    {
        WorktreePath = worktreePath;
        Branch = branch;
        LocalCommitSha = localCommitSha;
        RemoteCommitSha = remoteCommitSha;
    }

    public string WorktreePath { get; }
    public string Branch { get; }
    public string? LocalCommitSha { get; }
    public string? RemoteCommitSha { get; }
}
