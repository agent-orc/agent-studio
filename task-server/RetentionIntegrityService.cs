using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AgentStudio.Retention;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    internal async Task<ArchiveTargetStatusDto> GetArchiveTargetStatusAsync(CancellationToken ct)
    {
        var policy = await GetActiveRetentionPolicyAsync(ct);
        return new ArchiveTargetStatusDto(
            policy.ArchiveStorage.ArchiveTarget.ToString().ToLowerInvariant(),
            policy.ArchiveStorage.CopyToSecondary,
            policy.ArchiveStorage.DeleteLocalAfterVerification,
            true,
            S3ArchiveConfigured,
            string.IsNullOrWhiteSpace(_options.ArchiveS3Endpoint) ? null : _options.ArchiveS3Endpoint,
            string.IsNullOrWhiteSpace(_options.ArchiveS3Bucket) ? null : _options.ArchiveS3Bucket,
            _options.ArchiveS3Prefix,
            policy.ArchiveStorage.DeleteArchivedAfterYears);
    }

    internal async Task<RetentionIntegrityCheckResultDto> CheckArchiveIntegrityAsync(
        int? requestedSampleCount, string actorId, string trigger, CancellationToken ct)
    {
        var maximum = Math.Clamp(requestedSampleCount ?? _options.RetentionIntegritySampleCount, 1, 1000);
        var samples = new List<(string TaskId, string TaskKey, RetentionManifestEnvelope Envelope)>();
        await using (var connection = await OpenReadyAsync(ct))
        await using (var command = Command(connection, """
            SELECT am.task_id, t.task_key, am.manifest_json
             FROM archive_manifests am JOIN tasks t ON t.id = am.task_id
             WHERE am.state <> 'tombstone'
             ORDER BY random() LIMIT $limit;
            """, ("$limit", maximum)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                samples.Add((reader.GetString(0), reader.GetString(1),
                    JsonSerializer.Deserialize<RetentionManifestEnvelope>(reader.GetString(2), RetentionJson)
                    ?? throw new InvalidDataException($"Archive manifest for task '{reader.GetString(1)}' is invalid.")));

        var discrepancies = new List<string>();
        var verifiedObjects = 0;
        foreach (var sample in samples)
        foreach (var stage in sample.Envelope.Stages.Where(item => !string.IsNullOrWhiteSpace(item.PayloadSha256)))
        {
            var stageVerified = false;
            if (stage.Objects is { Count: > 0 })
            {
                foreach (var reference in stage.Objects)
                {
                    IArchiveTarget target;
                    try { target = TargetFor(reference); }
                    catch (Exception exception)
                    {
                        discrepancies.Add($"{sample.TaskKey} {reference.Target}:{reference.ObjectKey}: {exception.Message}");
                        continue;
                    }
                    try
                    {
                        var verification = await target.VerifyAsync(reference, ct);
                        if (verification.Verified) { verifiedObjects++; stageVerified = true; }
                        else discrepancies.Add($"{sample.TaskKey} {reference.Target}:{reference.ObjectKey}: {verification.Error}");
                    }
                    finally { if (target is IDisposable disposable) disposable.Dispose(); }
                }
            }
            else if (File.Exists(stage.PayloadPath))
            {
                var actual = await HashFileAsync(stage.PayloadPath, ct);
                stageVerified = string.Equals(actual, stage.PayloadSha256, StringComparison.OrdinalIgnoreCase);
                if (stageVerified) verifiedObjects++;
                else discrepancies.Add($"{sample.TaskKey} local:{stage.PayloadPath}: sha256-mismatch");
            }
            else
            {
                discrepancies.Add($"{sample.TaskKey} local:{stage.PayloadPath}: object-missing");
            }

            if (stageVerified)
            {
                string? materialized = null;
                try
                {
                    materialized = await MaterializeVerifiedPayloadAsync(stage, ct);
                    using var zip = ZipFile.OpenRead(materialized);
                    foreach (var file in stage.Files)
                    {
                        var entry = zip.GetEntry(file.RelativePath);
                        if (entry is null)
                        {
                            discrepancies.Add($"{sample.TaskKey} {file.RelativePath}: archive-entry-missing");
                            continue;
                        }
                        await using var stream = entry.Open();
                        var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
                        if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
                            discrepancies.Add($"{sample.TaskKey} {file.RelativePath}: file-sha256-mismatch");
                    }
                }
                finally
                {
                    if (materialized is not null && !string.Equals(materialized, stage.PayloadPath, StringComparison.OrdinalIgnoreCase)
                        && File.Exists(materialized)) File.Delete(materialized);
                }
            }
        }

        var policy = await GetActiveRetentionPolicyAsync(ct);
        var report = new RetentionPlanDto(
            UtcNow, policy.Version, samples.Count, 0, samples.Count, []);
        var runId = await RecordArchiveRunAsync(trigger, "integrity", policy.Version, report,
            verifiedObjects, 0, discrepancies, [], actorId, ct);
        if (discrepancies.Count > 0)
            await InWriteTransactionAsync(async (connection, transaction) => await AuditAsync(
                connection, transaction, actorId, "retention.integrity-discrepancy", "archive-run", runId,
                JsonSerializer.Serialize(new { sampledManifests = samples.Count, verifiedObjects, discrepancies }), ct), ct);
        return new RetentionIntegrityCheckResultDto(runId, samples.Count, verifiedObjects, discrepancies);
    }
}
