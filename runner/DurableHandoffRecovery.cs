using System.Security.Cryptography;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// Replays host-local result transfer state before the daemon claims new work.
/// It never starts an agent CLI. Transfer, acknowledgement, cleanup, and
/// completion resume from the journaled facts of the original RunAttempt.
/// </summary>
public sealed class DurableHandoffRecovery
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly RunnerOptions _options;
    private readonly TaskServerClient _client;
    private readonly Action<string> _log;

    public DurableHandoffRecovery(
        RunnerOptions options,
        TaskServerClient client,
        Action<string> log)
    {
        _options = options;
        _client = client;
        _log = log;
    }

    public async Task RecoverAllAsync(CancellationToken ct)
    {
        if (!_client.UsesDurableTaskServer) return;
        var root = Path.Combine(_options.WorkDir, "outbox");
        foreach (var outbox in DurableRunOutbox.OpenAll(root))
        {
            if (!string.Equals(
                    outbox.Authority.RunnerId,
                    _options.RunnerId,
                    StringComparison.Ordinal))
                continue;
            if (DurableRunOutbox.IsActive(outbox.Authority.RunId))
                continue;
            if (string.Equals(
                    outbox.Snapshot.FinalHandoffState,
                    "completed",
                    StringComparison.Ordinal)
                && outbox.Pending.Count == 0)
                continue;

            var reconciled = false;
            try
            {
                var artifactReplay = string.Equals(
                    outbox.Snapshot.FinalHandoffState, "artifact-replay", StringComparison.Ordinal);
                if (artifactReplay)
                {
                    _client.RestoreCompletedOutboxAuthority(outbox.Authority);
                    _log($"completed artifact authority restored run={outbox.Authority.RunId} fence={outbox.Authority.Fence}");
                }
                else
                {
                    var lease = await _client.ReconcileOutboxAuthorityAsync(
                        outbox.Authority,
                        Math.Max(30, _options.TtlSeconds),
                        ct);
                    _log(
                        $"outbox authority reconciled run={outbox.Authority.RunId} " +
                        $"fence={outbox.Authority.Fence} expires={lease.ExpiresAt:o}");
                }
                reconciled = true;
                await RecoverAsync(outbox, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (reconciled)
                {
                    outbox.RecordHandoffState("transfer-recovery");
                    await ReportSafeAsync(outbox, CancellationToken.None);
                }
                _log($"outbox recovery deferred run={outbox.Authority.RunId} task={outbox.Authority.TaskKey} error={ex.Message}");
            }
        }
    }

    private async Task RecoverAsync(DurableRunOutbox outbox, CancellationToken ct)
    {
        var context = Latest<DurableRunContextPayload>(outbox, "run-context")
                      ?? throw new InvalidDataException(
                          $"Run '{outbox.Authority.RunId}' has no durable repository context.");
        var terminal = Latest<DurableTerminalPayload>(outbox, "terminal")
                       ?? throw new InvalidDataException(
                           $"Run '{outbox.Authority.RunId}' has no durable terminal fact.");
        var workspace = new GitWorkspace(
            _options,
            outbox.Authority.TaskKey,
            _log,
            context.RepositoryId,
            context.RepositoryUrl,
            context.DefaultBranch,
            sourceRunAttemptId: outbox.Authority.RunId,
            fencingToken: outbox.Authority.Fence);

        var finalItem = outbox.Items.LastOrDefault(item => item.Kind == "final-result");
        var manifest = LatestManifest(outbox);
        ImmutableResultEnvelope envelope;
        WorktreeTeardownResult secured;
        if (finalItem is null)
        {
            manifest ??= await JournalArtifactsAsync(outbox, ct);
            outbox.RecordHandoffState("transferring");
            await ReportSafeAsync(outbox, ct);
            secured = await workspace.SecureForHandoffAsync(
                terminal.Outcome,
                outbox.Authority.RunId,
                ct);
            var dependencies = await workspace.ReadDependencyIdentitiesAsync(ct);
            envelope = new ImmutableResultEnvelope(
                context.RepositoryId,
                outbox.Authority.RunId,
                context.BaseSha,
                secured.ResultSha
                ?? throw new InvalidOperationException("Recovered transfer has no result SHA."),
                secured.ImmutableResultRef,
                null,
                manifest.Digest,
                dependencies.Submodules,
                dependencies.LfsObjects);
            outbox.Enqueue(
                "git-facts",
                JsonSerializer.Serialize(
                    new DurableGitFactsPayload(
                        context.RepositoryId,
                        context.BaseSha,
                        secured.ResultSha
                        ?? throw new InvalidOperationException(
                            "Recovered transfer has no result SHA."),
                        secured.ImmutableResultRef,
                        secured.Reconciliation,
                        secured.Reconciliation?.Kind == "divergent"
                            ? "inspect-preserved-divergent-tips"
                            : "retry-transfer-without-coding"),
                    Json));
            finalItem = outbox.Enqueue(
                "final-result",
                JsonSerializer.Serialize(envelope, Json));
        }
        else
        {
            if (manifest is null)
            {
                throw new InvalidDataException(
                    $"Run '{outbox.Authority.RunId}' has no durable artifact manifest.");
            }
            envelope = JsonSerializer.Deserialize<ImmutableResultEnvelope>(
                           finalItem.PayloadJson,
                           Json)
                       ?? throw new InvalidDataException("Durable result envelope is empty.");
            secured = new WorktreeTeardownResult(
                true,
                ResultSha: envelope.ResultSha,
                Branch: null,
                CommitSha: null,
                BranchUrl: null,
                ImmutableResultRef: envelope.ImmutableRemoteRef);
        }

        foreach (var item in outbox.Pending.Where(item => item.Sequence < finalItem.Sequence))
        {
            await _client.SendOutboxItemAsync(outbox.Authority, item, ct);
            outbox.Acknowledge(item.Sequence);
        }

        ResultHandoffAck acknowledgement;
        if (outbox.HandoffAcknowledgement is null)
        {
            acknowledgement = await _client.AcknowledgeResultHandoffAsync(
                outbox.Authority,
                finalItem,
                envelope,
                ct);
            outbox.RecordHandoffAcknowledgement(acknowledgement);
        }
        else
        {
            acknowledgement = outbox.HandoffAcknowledgement;
        }
        var envelopeDigest = acknowledgement.EnvelopeDigest;
        await ReportSafeAsync(outbox, ct);

        if (Directory.Exists(workspace.RepoPath))
        {
            await workspace.TeardownAfterHandoffAsync(
                secured,
                acknowledgement,
                outbox.Authority.RunId,
                envelopeDigest,
                ct);
        }

        var completion = outbox.Items.LastOrDefault(item => item.Kind == "completion")
                         ?? outbox.Enqueue(
                             "completion",
                             JsonSerializer.Serialize(
                                 new DurableCompletionPayload(
                                     terminal.Outcome,
                                     terminal.Reason,
                                     envelopeDigest),
                                 Json));
        if (completion.Sequence > outbox.LastAcknowledgedSequence)
        {
            await _client.SendOutboxItemAsync(outbox.Authority, completion, ct);
            outbox.Acknowledge(completion.Sequence);
        }
        outbox.RecordHandoffState("completed", envelopeDigest);
        await ReportSafeAsync(outbox, ct);
        try
        {
            var retry = await TransferArtifactsAfterDeliveryAsync(outbox, manifest, ct);
            outbox.RecordHandoffState(retry ? "artifact-replay" : "completed", envelopeDigest);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            outbox.RecordHandoffState("artifact-replay", envelopeDigest);
            _log(
                $"artifact-transfer recovery remained non-fatal run={outbox.Authority.RunId} "
                + $"artifacts=partial error={ex.Message}");
        }
        _log($"outbox recovery completed run={outbox.Authority.RunId} task={outbox.Authority.TaskKey} resultSha={envelope.ResultSha}");
    }

    private async Task<DurableArtifactManifest> JournalArtifactsAsync(
        DurableRunOutbox outbox,
        CancellationToken ct)
    {
        var results = Path.Combine(
            _options.WorkDir,
            "tasks",
            GitWorkspace.SafeSegment(outbox.Authority.TaskKey),
            "results");
        var evidence = RemoteTaskRunner.AttemptEvidenceDir(
            _options.WorkDir, outbox.Authority.TaskKey, outbox.Authority.RunId);
        if (!Directory.Exists(evidence))
            RemoteTaskRunner.CopyResultEvidence(results, evidence);
        var limits = await _client.GetArtifactTransferLimitsAsync(outbox.Authority.TaskKey, ct);
        var observed = RemoteTaskRunner.ObserveResultFiles(evidence);
        var (selected, skipped) = ArtifactTransferPolicy.Select(evidence, observed, limits);
        if (skipped.Count > 0)
        {
            RemoteTaskRunner.UpdateDeliverablesArtifactPolicy(evidence, skipped, limits);
            observed = RemoteTaskRunner.ObserveResultFiles(evidence);
            (selected, skipped) = ArtifactTransferPolicy.Select(evidence, observed, limits);
        }
        var entries = new List<ArtifactManifestEntry>();
        foreach (var file in selected)
        {
            var bytes = await File.ReadAllBytesAsync(file.FullPath, ct);
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            entries.Add(new ArtifactManifestEntry(file.RelativePath, sha, bytes.LongLength));
        }
        entries.AddRange(await RemoteTaskRunner.BuildWithheldManifestEntriesAsync(evidence, skipped, ct));
        var manifest = RemoteTaskRunner.BuildArtifactManifest(entries);
        outbox.Enqueue("artifact-manifest", manifest.Json);
        return manifest;
    }

    private async Task<bool> TransferArtifactsAfterDeliveryAsync(
        DurableRunOutbox outbox,
        DurableArtifactManifest manifest,
        CancellationToken ct)
    {
        var results = Path.Combine(
            _options.WorkDir,
            "tasks",
            GitWorkspace.SafeSegment(outbox.Authority.TaskKey),
            "results");
        var evidence = RemoteTaskRunner.AttemptEvidenceDir(
            _options.WorkDir, outbox.Authority.TaskKey, outbox.Authority.RunId);
        if (Directory.Exists(evidence)) results = evidence;
        var limits = await _client.GetArtifactTransferLimitsAsync(outbox.Authority.TaskKey, ct);
        var (files, initialSkipped) = ArtifactTransferPolicy.Select(
            results,
            RemoteTaskRunner.ObserveResultFiles(results),
            limits);
        var expected = JsonSerializer.Deserialize<ArtifactManifestEntry[]>(manifest.Json, Json)
                       ?? throw new InvalidDataException(
                           $"Run '{outbox.Authority.RunId}' has an empty artifact manifest.");
        var withheldByPath = expected
            .Where(entry => entry.TransferStatus == "withheld")
            .ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        var issues = initialSkipped
            .Where(issue => !withheldByPath.ContainsKey(issue.Path))
            .ToList();
        issues.AddRange(withheldByPath.Values.Select(entry => new ArtifactTransferIssue(
            entry.Path,
            entry.SizeBytes,
            entry.Reason ?? "was withheld by the original artifact policy")));
        var expectedByPath = expected.Where(entry => entry.TransferStatus != "withheld").ToDictionary(
            entry => entry.Path,
            StringComparer.Ordinal);
        var acknowledgedPaths = outbox.Items
            .Where(item => item.Kind == "artifact"
                           && item.Sequence <= outbox.LastAcknowledgedSequence)
            .Select(item => JsonSerializer.Deserialize<DurableArtifactPayload>(item.PayloadJson, Json)?.Name)
            .OfType<string>()
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.Ordinal);
        var observedPaths = files.Select(file => file.RelativePath)
            .Concat(initialSkipped.Select(issue => issue.Path))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var missing in expected.Where(entry =>
                     entry.TransferStatus != "withheld"
                     &&
                     !observedPaths.Contains(entry.Path)
                     && !acknowledgedPaths.Contains(entry.Path)))
        {
            issues.Add(new ArtifactTransferIssue(
                missing.Path,
                missing.SizeBytes,
                "was unavailable after artifact manifest preparation",
                ArtifactTransferOutcomes.TransferFailed));
        }
        foreach (var file in files)
        {
            if (withheldByPath.ContainsKey(file.RelativePath)) continue;
            if (acknowledgedPaths.Contains(file.RelativePath)) continue;
            try
            {
                var bytes = await File.ReadAllBytesAsync(file.FullPath, ct);
                var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (!expectedByPath.TryGetValue(file.RelativePath, out var entry)
                    || entry.SizeBytes != bytes.LongLength
                    || !string.Equals(entry.Sha256, sha, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new ArtifactTransferIssue(
                        file.RelativePath,
                        bytes.LongLength,
                        expectedByPath.ContainsKey(file.RelativePath)
                            ? "changed after artifact manifest preparation; skipped to preserve manifest integrity"
                            : "was created after artifact manifest preparation; skipped to preserve manifest integrity",
                        ArtifactTransferOutcomes.TransferFailed));
                    continue;
                }
                var upload = new RunnerArtifactUpload(file.RelativePath, Convert.ToBase64String(bytes));
                RemoteTaskRunner.ValidateArtifactAcknowledgement(
                    outbox.Authority.TaskKey,
                    [upload],
                    await _client.UploadArtifactsAsync(new ArtifactIngestRequest(
                        outbox.Authority.TaskKey,
                        [upload],
                        outbox.Authority.RunnerId,
                        outbox.Authority.LeaseId,
                        outbox.Authority.Fence,
                        outbox.Authority.RunId,
                        outbox.Authority.Fence,
                        IdempotencyKey: $"artifact:{outbox.Authority.RunId}:{file.RelativePath}:{WireDigest.Hash(upload.ContentBase64)}"),
                        ct));
            }
            catch (TaskServerException ex) when (ArtifactTransferPolicy.IsCapacityRejection(ex))
            {
                issues.Add(new ArtifactTransferIssue(
                    file.RelativePath,
                    file.SizeBytes,
                    ex.StatusCode == 413
                        ? ex.Message
                        : "was refused because artifact storage is full (HTTP 507)"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                issues.Add(new ArtifactTransferIssue(
                    file.RelativePath,
                    file.SizeBytes,
                    $"upload failed ({ex.Message})",
                    ArtifactTransferOutcomes.TransferFailed));
            }
        }
        try
        {
            await _client.UploadArtifactsAsync(new ArtifactIngestRequest(
                outbox.Authority.TaskKey,
                [],
                outbox.Authority.RunnerId,
                outbox.Authority.LeaseId,
                outbox.Authority.Fence,
                outbox.Authority.RunId,
                outbox.Authority.Fence,
                IdempotencyKey: $"artifact-finalize:{outbox.Authority.RunId}",
                FinalizeResult: true), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log($"artifact finalization recovery was non-fatal run={outbox.Authority.RunId}: {ex.Message}");
        }
        if (issues.Count == 0) return false;
        await _client.ReportArtifactTransferAsync(new ArtifactTransferReportRequest(
            outbox.Authority.TaskKey,
            "partial",
            issues,
            outbox.Authority.RunnerId,
            outbox.Authority.LeaseId,
            outbox.Authority.Fence,
            outbox.Authority.RunId), ct);
        _log(
            $"artifact-transfer recovery run={outbox.Authority.RunId} artifacts=partial "
            + $"notTransferred={issues.Count}");
        return issues.Any(issue => issue.Outcome == ArtifactTransferOutcomes.TransferFailed);
    }

    private static T? Latest<T>(DurableRunOutbox outbox, string kind)
        where T : class
    {
        var item = outbox.Items.LastOrDefault(item => item.Kind == kind);
        return item is null ? null : JsonSerializer.Deserialize<T>(item.PayloadJson, Json);
    }

    private static DurableArtifactManifest? LatestManifest(DurableRunOutbox outbox)
    {
        var item = outbox.Items.LastOrDefault(item => item.Kind == "artifact-manifest");
        if (item is null) return null;
        var digest = Convert.ToHexString(
                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(item.PayloadJson)))
            .ToLowerInvariant();
        return new DurableArtifactManifest(digest, item.PayloadJson);
    }

    private async Task ReportSafeAsync(DurableRunOutbox outbox, CancellationToken ct)
    {
        try
        {
            await _client.ReportOutboxAsync(
                _options.RunnerId,
                _client.RunnerInstanceId,
                outbox,
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log($"outbox recovery observability deferred run={outbox.Authority.RunId} error={ex.Message}");
        }
    }
}
