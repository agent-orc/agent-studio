using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer.Recovery;

/// <summary>Reads canonical refs from a repository origin without cloning it.</summary>
public interface IRecoveryGitProbe
{
    /// <summary>Returns ref name to commit, or null when the origin cannot be reached.</summary>
    Task<IReadOnlyDictionary<string, string>?> ListRemoteAsync(string origin, CancellationToken ct);
}

/// <summary>
/// Process-free ref reader: the Task Server never starts Git. Local bare repositories are read from their
/// ref files, HTTP(S) origins through the smart-HTTP ref advertisement, and any other origin (SSH) from an
/// operator-supplied <c>git ls-remote</c> listing keyed by origin.
/// </summary>
public sealed class OriginRefProbe(HttpClient http, IReadOnlyDictionary<string, string>? listings = null) : IRecoveryGitProbe
{
    public async Task<IReadOnlyDictionary<string, string>?> ListRemoteAsync(string origin, CancellationToken ct)
    {
        if (listings is not null && listings.TryGetValue(origin, out var listing))
            return File.Exists(listing) ? ParseListing(await File.ReadAllTextAsync(listing, ct)) : null;
        if (origin.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || origin.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return await ReadSmartHttpAsync(origin, ct);
        var local = origin.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ? new Uri(origin).LocalPath : origin;
        return Directory.Exists(local) ? await ReadLocalAsync(local, ct) : null;
    }

    /// <summary>Parses <c>git ls-remote</c> output: one "sha TAB ref" per line.</summary>
    public static IReadOnlyDictionary<string, string> ParseListing(string text)
    {
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t', 2);
            if (parts.Length == 2) refs[parts[1].Trim()] = parts[0].Trim();
        }
        return refs;
    }

    private static async Task<IReadOnlyDictionary<string, string>?> ReadLocalAsync(string repository, CancellationToken ct)
    {
        var gitDirectory = Directory.Exists(Path.Combine(repository, ".git")) ? Path.Combine(repository, ".git") : repository;
        if (!File.Exists(Path.Combine(gitDirectory, "HEAD"))) return null;
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        var packed = Path.Combine(gitDirectory, "packed-refs");
        if (File.Exists(packed))
            foreach (var line in await File.ReadAllLinesAsync(packed, ct))
            {
                if (line.StartsWith('#') || line.StartsWith('^')) continue;
                var parts = line.Split(' ', 2);
                if (parts.Length == 2) refs[parts[1].Trim()] = parts[0].Trim();
            }
        var refsRoot = Path.Combine(gitDirectory, "refs");
        if (Directory.Exists(refsRoot))
            foreach (var file in Directory.EnumerateFiles(refsRoot, "*", SearchOption.AllDirectories))
            {
                var value = (await File.ReadAllTextAsync(file, ct)).Trim();
                if (value.Length == 40 || value.Length == 64)
                    refs["refs/" + Path.GetRelativePath(refsRoot, file).Replace('\\', '/')] = value;
            }
        return refs;
    }

    private async Task<IReadOnlyDictionary<string, string>?> ReadSmartHttpAsync(string origin, CancellationToken ct)
    {
        try
        {
            var body = await http.GetStringAsync(origin.TrimEnd('/') + "/info/refs?service=git-upload-pack", ct);
            var refs = new Dictionary<string, string>(StringComparer.Ordinal);
            var index = 0;
            while (index + 4 <= body.Length)
            {
                var length = Convert.ToInt32(body.Substring(index, 4), 16);
                if (length == 0) { index += 4; continue; }
                var line = body.Substring(index + 4, length - 4).TrimEnd('\n');
                index += length;
                if (line.StartsWith('#')) continue;
                var payload = line.Split('\0', 2)[0];
                var parts = payload.Split(' ', 2);
                if (parts.Length == 2 && parts[0].Length is 40 or 64) refs[parts[1]] = parts[0];
            }
            return refs;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or FormatException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}

public sealed record RecoveryCopyReceipt(
    string Schema,
    string BackupId,
    string ManifestId,
    string SetSha256,
    string ManifestSha256,
    DateTime CopiedAt,
    string Destination,
    int FileCount,
    long TotalBytes,
    IReadOnlyList<string> Warnings);

public sealed record RecoveryIdentityComparison(string Subject, string Expected, string Actual, bool Matches);

public sealed record RecoveryRestoreReceipt(
    string Schema,
    string ManifestId,
    string BackupId,
    string SetSha256,
    string SourceRelease,
    string TargetRelease,
    DateTime CapturedAt,
    DateTime? LossAt,
    DateTime RestoreStartedAt,
    DateTime RestoreCompletedAt,
    double RestoreSeconds,
    double? MeasuredRecoveryPointSeconds,
    IReadOnlyList<RecoveryIdentityComparison> Comparisons,
    IReadOnlyList<RecoveryFinding> Findings,
    string CopyDirectory,
    DateTime? HostsFencedAt = null,
    int? FencedCredentials = null,
    DateTime? ResumedAt = null,
    double? MeasuredRecoveryTimeSeconds = null,
    string? ManifestSha256 = null)
{
    public const string FileName = "recovery-restore-receipt.json";
    public const string CurrentSchema = "agent-studio.recovery-restore-receipt/v1";
}

/// <param name="ManifestSha256">Digest of the exact manifest bytes that were parsed and checked.</param>
public sealed record RecoveryVerifyResult(RecoveryManifest? Manifest, RecoveryCheckReport Report, string? ManifestSha256 = null);

public sealed record RecoveryRestoreResult(bool Restored, RecoveryRestoreReceipt? Receipt, RecoveryCheckReport Report, string Message);

/// <summary>
/// Application coordination for the installation recovery set: capture, off-host copy, verify,
/// restore to an empty target and the gated resume. Store mutations go through Task Server services.
/// </summary>
public sealed class RecoveryWorkflow(TaskServerStore store, TaskServerOptions options, IRecoveryGitProbe git, TimeProvider clock)
{
    public const string ManifestFile = "recovery-manifest.json";
    public const string CopyReceiptFile = "copy-receipt.json";
    public const string SetDirectory = "set";
    private const string CustodyFile = "custody.json";

    private static readonly RecoveryRebuildableCache[] RebuildableCaches =
    [
        new("runner worktrees and caches", "runner host work directories", "next claim recreates the worktree from the origin"),
        new("host preflight cache", "studio_host_preflight_cache", "next host preflight"),
        new("token summary cache", "studio_token_summary_cache", "next Studio summary read"),
        new("workspace metrics cache", "studio_workspace_metrics_cache", "next Studio metrics read"),
        new("container images", "Docker image store", "pull or build the release recorded in store.release"),
    ];

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public string ManifestPath(string backupId) => Path.Combine(store.BackupDirectory, "recovery", backupId + "." + ManifestFile);

    /// <summary>Creates a verified full set through the existing backup service, then writes its manifest beside it.</summary>
    public async Task<RecoveryManifest> CaptureAsync(RecoveryCustodyDeclaration custody, string actorId, CancellationToken ct)
    {
        // Declared canonical refs are resolved before the set exists, so an unreachable origin leaves no orphan set.
        var declaredRepositories = (custody.Repositories ?? []).ToDictionary(item => item.RepositoryId, StringComparer.Ordinal);
        var declaredRefs = new List<(string RepositoryId, List<RecoveryRef> Refs)>();
        foreach (var declared in declaredRepositories.Values.Where(item => item.Refs is { Count: > 0 }))
        {
            var remote = await git.ListRemoteAsync(declared.Origin, ct)
                         ?? throw new InvalidOperationException(
                             $"Origin of repository '{declared.RepositoryId}' is unreachable at capture; declared refs cannot be recorded.");
            declaredRefs.Add((declared.RepositoryId, declared.Refs!.Select(name => new RecoveryRef(
                name,
                remote.TryGetValue(name, out var sha) ? sha : throw new InvalidOperationException($"Origin has no ref '{name}'."),
                "declared")).ToList()));
        }

        var backup = await new FullBackupManagementService(store).CreateAsync(actorId, ct);
        var setRoot = store.FullBackupSetPath(backup.Id);
        var facts = await TaskServerStore.ReadRecoverySnapshotFactsAsync(setRoot, ct);
        var inventory = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(setRoot, "inventory.json"), ct)).RootElement;

        var repositories = facts.RecordedRepositories
            .Select(item => item with
            {
                Origin = declaredRepositories.TryGetValue(item.RepositoryId, out var declared) ? declared.Origin : item.Origin,
            })
            .ToList();
        foreach (var (repositoryId, refs) in declaredRefs)
        {
            var index = repositories.FindIndex(item => item.RepositoryId == repositoryId);
            if (index < 0) repositories.Add(new RecoveryRepository(repositoryId, declaredRepositories[repositoryId].Origin, refs));
            else repositories[index] = repositories[index] with { SampledRefs = [.. refs, .. repositories[index].SampledRefs] };
        }

        var clientCustody = (custody.Clients ?? []).ToDictionary(item => item.PrincipalId, item => item.Custody, StringComparer.Ordinal);
        var clients = facts.ActivePrincipals
            .Select(item => item with
            {
                Custody = clientCustody.TryGetValue(item.PrincipalId, out var declared) ? declared : RecoveryCredentialCustody.Undeclared,
            })
            .ToList();

        var bundle = custody.SecretBundle;
        var manifest = new RecoveryManifest(
            RecoveryManifest.CurrentSchema,
            $"rcv_{backup.Id}",
            UtcNow,
            custody.InstallationId,
            new RecoveryStore(RecoveryStoreTypes.TaskServerSqlite, TaskServerBuildIdentity.Current.Release,
                TaskServerBuildIdentity.Current.GitSha, facts.SchemaVersion),
            new RecoveryIdentities(facts.ServerId, facts.Workspaces, facts.Projects, facts.TaskCount, facts.TaskIdentitySha256),
            new RecoveryDataSet("database-snapshot", backup.Id, backup.SetSha256, "complete.json", "inventory.json",
                inventory.GetProperty("files").GetArrayLength(), backup.TotalBytes),
            new RecoveryColdEvidence(facts.ColdPayloads.Count, facts.ColdPayloads),
            facts.ExternalArtifacts,
            repositories,
            (custody.Configuration ?? []).Select(item => item with { Sha256 = item.Sha256 ?? HashIfPresent(item.Location) }).ToList(),
            new RecoverySecretCustody(bundle?.Location, bundle is null ? null : HashIfPresent(bundle.Location),
                bundle?.Encryption, bundle?.RecoveryCredentialHolder, clients),
            facts.Obligations,
            RebuildableCaches);

        var path = ManifestPath(backup.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest, RecoveryJson.Options) + Environment.NewLine, ct);
        await File.WriteAllTextAsync(Path.ChangeExtension(path, null) + "." + CustodyFile,
            JsonSerializer.Serialize(custody, RecoveryJson.Options) + Environment.NewLine, ct);
        await store.AuditRecoveryAsync(actorId, "recovery.captured", manifest.ManifestId,
            new { backup.Id, backup.SetSha256, obligations = facts.Obligations.Count }, ct);
        return manifest;
    }

    /// <summary>Copies set, manifest and custody declaration to a destination and verifies the copy before writing its receipt.</summary>
    public async Task<RecoveryCopyReceipt> CopyAsync(string backupId, string destinationRoot, CancellationToken ct)
    {
        var setRoot = store.FullBackupSetPath(backupId);
        var manifestPath = ManifestPath(backupId);
        if (!File.Exists(manifestPath))
            throw new InvalidDataException($"Full backup '{backupId}' has no recovery manifest; capture it with `task-server recovery capture`.");
        var destination = Path.GetFullPath(Path.Combine(destinationRoot, backupId));
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new IOException($"Copy destination '{destination}' is not empty.");

        var warnings = new List<string>();
        var dataRoot = Path.GetFullPath(store.DataDirectory) + Path.DirectorySeparatorChar;
        var backupRoot = Path.GetFullPath(store.BackupDirectory) + Path.DirectorySeparatorChar;
        if (destination.StartsWith(dataRoot, StringComparison.Ordinal) || destination.StartsWith(backupRoot, StringComparison.Ordinal))
            warnings.Add("Destination is inside the authority's own data or backup directory; this is not an off-host copy.");

        CopyTree(setRoot, Path.Combine(destination, SetDirectory));
        File.Copy(manifestPath, Path.Combine(destination, ManifestFile));
        var custodyPath = Path.ChangeExtension(manifestPath, null) + "." + CustodyFile;
        if (File.Exists(custodyPath)) File.Copy(custodyPath, Path.Combine(destination, CustodyFile));

        var verified = await VerifyCopyAsync(destination, secretBundleOverride: null, probeGit: false, ct,
            requireCopyReceipt: false);
        var blocking = verified.Report.Findings.Where(item => item.Severity == RecoveryFindingSeverity.BlocksRestore).ToList();
        if (blocking.Count > 0)
            throw new InvalidDataException("Copied set failed verification: " + string.Join("; ", blocking.Select(item => item.Code)));

        var manifest = verified.Manifest!;
        var receipt = new RecoveryCopyReceipt(
            "agent-studio.recovery-copy-receipt/v1", backupId, manifest.ManifestId, manifest.DataSet.SetSha256,
            verified.ManifestSha256!, UtcNow, destination,
            manifest.DataSet.FileCount, manifest.DataSet.TotalBytes, warnings);
        await File.WriteAllTextAsync(Path.Combine(destination, CopyReceiptFile),
            JsonSerializer.Serialize(receipt, RecoveryJson.Options) + Environment.NewLine, ct);
        return receipt;
    }

    /// <summary>Verifies a copied recovery set without opening any authority store.</summary>
    public async Task<RecoveryVerifyResult> VerifyCopyAsync(
        string copyRoot, string? secretBundleOverride, bool probeGit, CancellationToken ct,
        bool requireCopyReceipt = true)
    {
        var setRoot = Path.Combine(copyRoot, SetDirectory);
        var manifestPath = Path.Combine(copyRoot, ManifestFile);
        RecoveryManifest? manifest = null;
        string? manifestSchema = null;
        string? manifestSha = null;
        var manifestReadable = true;
        if (File.Exists(manifestPath))
        {
            // Hash and parse the same bytes, so the digest bound to a receipt is the manifest that was checked.
            var bytes = await File.ReadAllBytesAsync(manifestPath, ct);
            manifestSha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            try
            {
                var root = JsonDocument.Parse(bytes).RootElement;
                manifestSchema = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("schema", out var schema)
                                 && schema.ValueKind == JsonValueKind.String
                    ? schema.GetString()
                    : null;
                if (manifestSchema == RecoveryManifest.CurrentSchema)
                {
                    manifest = JsonSerializer.Deserialize<RecoveryManifest>(bytes, RecoveryJson.Options);
                    if (!IsStructurallyComplete(manifest)) (manifest, manifestReadable) = (null, false);
                }
            }
            catch (JsonException)
            {
                manifestReadable = false;
            }
        }

        var receiptPath = Path.Combine(copyRoot, CopyReceiptFile);
        var receiptPresent = File.Exists(receiptPath);
        var receiptValid = !receiptPresent;
        var manifestDigestMatches = !receiptPresent;
        if (receiptPresent)
        {
            try
            {
                var receipt = JsonSerializer.Deserialize<RecoveryCopyReceipt>(
                    await File.ReadAllTextAsync(receiptPath, ct), RecoveryJson.Options);
                receiptValid = receipt is not null && manifest is not null
                    && receipt.Schema == "agent-studio.recovery-copy-receipt/v1"
                    && receipt.BackupId == manifest.DataSet.BackupId
                    && receipt.ManifestId == manifest.ManifestId
                    && string.Equals(receipt.SetSha256, manifest.DataSet.SetSha256, StringComparison.OrdinalIgnoreCase)
                    && receipt.FileCount == manifest.DataSet.FileCount
                    && receipt.TotalBytes == manifest.DataSet.TotalBytes;
                manifestDigestMatches = receipt is not null && manifestSha is not null
                    && string.Equals(receipt.ManifestSha256, manifestSha, StringComparison.OrdinalIgnoreCase);
            }
            catch (JsonException)
            {
                receiptValid = false;
                manifestDigestMatches = false;
            }
        }

        var completePath = Path.Combine(setRoot, "complete.json");
        var inventoryPath = Path.Combine(setRoot, "inventory.json");
        string? completeSha = null, inventorySha = null, actualSha = null;
        var changed = new List<string>();
        var missingCold = new List<string>();
        if (File.Exists(completePath) && File.Exists(inventoryPath))
        {
            List<(string Path, long Size, string Sha)> recorded;
            try
            {
                completeSha = JsonDocument.Parse(await File.ReadAllTextAsync(completePath, ct)).RootElement.GetProperty("setSha256").GetString();
            }
            catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                completeSha = null;
                changed.Add("complete.json");
            }
            try
            {
                var inventory = JsonDocument.Parse(await File.ReadAllTextAsync(inventoryPath, ct)).RootElement;
                inventorySha = inventory.GetProperty("setSha256").GetString();
                recorded = inventory.GetProperty("files").EnumerateArray()
                    .Select(item => (Path: item.GetProperty("relativePath").GetString()!, Size: item.GetProperty("size").GetInt64(),
                        Sha: item.GetProperty("sha256").GetString()!))
                    .ToList();
            }
            catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                // An unreadable inventory is a corrupted set member, not a crash of the verifier.
                inventorySha = null;
                recorded = [];
                changed.Add("inventory.json");
            }
            var actual = await InventoryAsync(setRoot, ct);
            actualSha = SetHash(actual);
            var actualByPath = actual.ToDictionary(item => item.Path, StringComparer.Ordinal);
            foreach (var entry in recorded)
                if (!actualByPath.TryGetValue(entry.Path, out var found) || found.Sha != entry.Sha || found.Size != entry.Size)
                    changed.Add(entry.Path);
            if (inventorySha is not null)
                changed.AddRange(actual.Select(item => item.Path).Except(recorded.Select(item => item.Path), StringComparer.Ordinal));
            foreach (var cold in manifest?.ColdEvidence.Payloads ?? [])
                if (!actualByPath.TryGetValue(cold.RelativePath, out var found) || found.Sha != cold.Sha256)
                    missingCold.Add(cold.RelativePath);
            if (manifest is not null && inventorySha is not null
                && !string.Equals(manifest.DataSet.SetSha256, inventorySha, StringComparison.OrdinalIgnoreCase))
                changed.Add(ManifestFile);
        }

        var probes = new List<RecoveryGitProbe>();
        if (probeGit && manifest is not null)
            foreach (var repository in manifest.Repositories)
            {
                var sampled = repository.SampledRefs.Where(item => item.Sha.Length > 0).ToList();
                if (sampled.Count == 0) continue;
                if (string.IsNullOrWhiteSpace(repository.Origin))
                {
                    probes.AddRange(sampled.Select(item => new RecoveryGitProbe(repository.RepositoryId, null, item.Name, item.Sha,
                        RecoveryGitProbeOutcome.OriginNotDeclared, null)));
                    continue;
                }
                var refs = await git.ListRemoteAsync(repository.Origin, ct);
                foreach (var item in sampled)
                {
                    if (refs is null)
                    {
                        probes.Add(new(repository.RepositoryId, repository.Origin, item.Name, item.Sha,
                            RecoveryGitProbeOutcome.OriginUnavailable, null));
                        continue;
                    }
                    if (!refs.TryGetValue(item.Name, out var sha))
                    {
                        probes.Add(new(repository.RepositoryId, repository.Origin, item.Name, item.Sha,
                            RecoveryGitProbeOutcome.RefMissing, null));
                        continue;
                    }
                    if (string.Equals(sha, item.Sha, StringComparison.OrdinalIgnoreCase))
                    {
                        probes.Add(new(repository.RepositoryId, repository.Origin, item.Name, item.Sha,
                            RecoveryGitProbeOutcome.RefPresent, null));
                        continue;
                    }

                    var proofRef = refs.Where(candidate =>
                            string.Equals(candidate.Value, item.Sha, StringComparison.OrdinalIgnoreCase) &&
                            candidate.Key.StartsWith("refs/heads/agent-studio/results/", StringComparison.Ordinal) &&
                            candidate.Key.EndsWith("/" + item.Sha, StringComparison.OrdinalIgnoreCase))
                        .Select(candidate => candidate.Key).FirstOrDefault();
                    probes.Add(new(repository.RepositoryId, repository.Origin, item.Name, item.Sha,
                        proofRef is null ? RecoveryGitProbeOutcome.RefMoved : RecoveryGitProbeOutcome.RefMovedWithImmutableProof,
                        proofRef is null ? sha : $"{sha}; proof {proofRef}"));
                }
            }

        var bundleLocation = secretBundleOverride ?? manifest?.SecretCustody.BundleLocation;
        var bundlePresent = bundleLocation is not null && File.Exists(bundleLocation);
        var bundleMatches = bundlePresent && manifest?.SecretCustody.BundleSha256 is { } expected
                            && string.Equals(await HashFileAsync(bundleLocation!, ct), expected, StringComparison.OrdinalIgnoreCase);

        var facts = new RecoveryCheckFacts(
            manifestSchema,
            File.Exists(manifestPath),
            File.Exists(completePath),
            File.Exists(inventoryPath),
            completeSha,
            inventorySha,
            actualSha,
            changed,
            missingCold,
            manifest?.Store.SchemaVersion ?? 0,
            TaskServerStore.CurrentSchemaVersion,
            manifest?.Store.Release ?? "unknown",
            TaskServerBuildIdentity.Current.Release,
            probes,
            manifest?.SecretCustody.BundleLocation is not null,
            bundlePresent,
            bundleMatches,
            manifest?.SecretCustody.Clients ?? [],
            manifest?.PendingHostObligations ?? [],
            receiptPresent,
            requireCopyReceipt,
            receiptValid,
            manifestDigestMatches,
            manifestReadable);
        return new RecoveryVerifyResult(manifest, RecoveryCheckPolicy.Evaluate(facts), manifestSha);
    }

    /// <summary>
    /// Restores a verified copy onto an empty target through the full backup restore service, then compares
    /// identities with the manifest. The target remains in Maintenance; <see cref="ResumeAsync"/> releases it.
    /// </summary>
    public async Task<RecoveryRestoreResult> RestoreToEmptyAsync(
        string copyRoot, string? secretBundleOverride, DateTime? lossAt, string actorId, CancellationToken ct)
    {
        var started = UtcNow;
        var stopwatch = Stopwatch.StartNew();
        RequireEmptyTarget(store.DataDirectory, "data directory");
        RequireEmptyTarget(options.ResolveBackupDirectory(), "backup directory");
        RequireEmptyTarget(options.ResolveRetentionArchivePath(), "archive directory");

        var verified = await VerifyCopyAsync(copyRoot, secretBundleOverride, probeGit: true, ct);
        if (!verified.Report.RestoreAllowed || verified.Manifest is null)
            return new RecoveryRestoreResult(false, null, verified.Report, "Recovery set failed verification; the target was not changed.");
        var manifest = verified.Manifest;

        // The copy is read twice: once to verify, once to place it. Bind the placed set to the manifest before
        // the store opens, so a copy that changed in between never reaches the target.
        var placedSet = Path.Combine(options.ResolveFullBackupDirectory(), manifest.DataSet.BackupId);
        CopyTree(Path.Combine(copyRoot, SetDirectory), placedSet);
        var placedSha = SetHash(await InventoryAsync(placedSet, ct));
        if (!string.Equals(placedSha, manifest.DataSet.SetSha256, StringComparison.OrdinalIgnoreCase))
        {
            Directory.Delete(placedSet, recursive: true);
            var report = new RecoveryCheckReport([.. verified.Report.Findings, new RecoveryFinding(
                "set-changed-during-restore", RecoveryFindingSeverity.BlocksRestore, $"placed set digest {placedSha}",
                "The copied set changed after it was verified, so the placed set no longer matches the manifest. The target was left empty. Protect the off-host copy from writers, verify it again and restore.")]);
            return new RecoveryRestoreResult(false, null, report, "Recovery set changed after verification; the target was left empty.");
        }

        await store.InitializeForBackupAsync(ct);
        await store.ChangeModeAsync(new ChangeModeRequest(TaskServerMode.Maintenance, "recovery restore to empty target"), actorId, ct);
        var restored = await new FullBackupManagementService(store).RestoreAsync(manifest.DataSet.BackupId, actorId, ct);
        if (!restored.Restored)
            return new RecoveryRestoreResult(false, null, verified.Report, restored.Message);

        var comparisons = await CompareWithLiveAsync(manifest, ct);

        stopwatch.Stop();
        var completed = UtcNow;
        var receipt = new RecoveryRestoreReceipt(
            RecoveryRestoreReceipt.CurrentSchema,
            manifest.ManifestId,
            manifest.DataSet.BackupId,
            manifest.DataSet.SetSha256,
            manifest.Store.Release,
            TaskServerBuildIdentity.Current.Release,
            manifest.CapturedAt,
            lossAt,
            started,
            completed,
            Math.Round(stopwatch.Elapsed.TotalSeconds, 3),
            lossAt is null ? null : Math.Round((lossAt.Value - manifest.CapturedAt).TotalSeconds, 3),
            comparisons,
            verified.Report.Findings,
            Path.GetFullPath(copyRoot),
            ManifestSha256: verified.ManifestSha256);
        await WriteReceiptAsync(receipt, ct);
        await store.AuditRecoveryAsync(actorId, "recovery.restored", manifest.ManifestId,
            new { manifest.DataSet.BackupId, mismatches = comparisons.Count(item => !item.Matches) }, ct);
        var identical = comparisons.All(item => item.Matches);
        return new RecoveryRestoreResult(identical, receipt, verified.Report,
            identical
                ? "Restored in Maintenance; identities, task set and cold evidence match the manifest."
                : "Restored in Maintenance, but identities differ from the manifest; do not resume.");
    }

    public async Task<RecoveryRestoreReceipt?> ReadReceiptAsync(CancellationToken ct)
    {
        var path = Path.Combine(store.DataDirectory, RecoveryRestoreReceipt.FileName);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<RecoveryRestoreReceipt>(await File.ReadAllTextAsync(path, ct), RecoveryJson.Options)
            : null;
    }

    /// <summary>Revokes every runner credential the old authority issued; required before resume.</summary>
    public async Task<int> FenceHostsAsync(string actorId, CancellationToken ct)
    {
        var receipt = await ReadReceiptAsync(ct)
                      ?? throw new InvalidOperationException("No recovery restore receipt; fence hosts only on a restored target.");
        var fencedAt = UtcNow;
        var revoked = await store.FenceRecoveredRunnerCredentialsAsync(fencedAt, actorId, ct);
        await WriteReceiptAsync(receipt with { HostsFencedAt = fencedAt, FencedCredentials = revoked }, ct);
        return revoked;
    }

    public async Task<IssuedPrincipalCredential> ReenrolClientAsync(string principalId, string actorId, CancellationToken ct)
    {
        if (await ReadReceiptAsync(ct) is null)
            throw new InvalidOperationException("No recovery restore receipt; re-enrol clients only on a restored target.");
        return await store.ReissueRecoveredClientCredentialAsync(principalId, actorId, null, ct);
    }

    /// <summary>
    /// Re-enrols a client and delivers the new credential to <paramref name="credentialPath"/>. The credential
    /// is written and flushed to an owner-only staging file created with that mode before the rotation commits;
    /// a failed write rolls the rotation back, and the committed credential is then moved into place.
    /// </summary>
    public async Task<IssuedPrincipalCredential> ReenrolClientToFileAsync(
        string principalId, string credentialPath, string actorId, CancellationToken ct)
    {
        if (await ReadReceiptAsync(ct) is null)
            throw new InvalidOperationException("No recovery restore receipt; re-enrol clients only on a restored target.");
        var target = Path.GetFullPath(credentialPath);
        var staging = Path.Combine(Path.GetDirectoryName(target)!, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        IssuedPrincipalCredential issued;
        try
        {
            issued = await store.ReissueRecoveredClientCredentialAsync(principalId, actorId, async (credential, token) =>
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using var stream = new FileStream(staging, options);
                await stream.WriteAsync(Encoding.UTF8.GetBytes(credential), token);
                stream.Flush(flushToDisk: true);
            }, ct);
        }
        catch
        {
            File.Delete(staging);
            throw;
        }
        try
        {
            File.Move(staging, target, overwrite: true);
        }
        catch (Exception exception)
        {
            throw new IOException(
                $"The new credential is active and was written to '{staging}' but could not be moved to '{target}': {exception.Message}",
                exception);
        }
        return issued;
    }

    /// <summary>Evaluates the resume gate and, when it passes and not only checking, releases Maintenance.</summary>
    public async Task<(RecoveryResumeDecision Decision, RecoveryRestoreReceipt? Receipt)> ResumeAsync(
        bool oldWriterClosed, bool obligationsRetained, bool checkOnly, string? secretBundleOverride, string actorId, CancellationToken ct)
    {
        var receipt = await ReadReceiptAsync(ct);
        IReadOnlyList<RecoveryFinding> findings = [];
        IReadOnlyList<RecoveryIdentityComparison> fresh = [];
        if (receipt is not null)
        {
            // Every piece of evidence the gate relies on is re-read now: the copy, its binding to this
            // restore, its Git refs and custody, and the target's identities against that manifest.
            if (!Directory.Exists(receipt.CopyDirectory))
                findings = [new RecoveryFinding("recovery-copy-unavailable", RecoveryFindingSeverity.BlocksResume,
                    receipt.CopyDirectory,
                    "The copied recovery set is unavailable. Restore access to the verified off-host copy and run the resume check again so its inventory and Git refs can be verified afresh.")];
            else
            {
                var verified = await VerifyCopyAsync(receipt.CopyDirectory, secretBundleOverride, probeGit: true, ct);
                var binding = RecoveryCheckPolicy.BindToRestore(receipt.ManifestId, receipt.BackupId, receipt.SetSha256,
                    receipt.ManifestSha256, verified.Manifest, verified.ManifestSha256, receipt.CopyDirectory);
                findings = binding is null ? verified.Report.Findings : [.. verified.Report.Findings, binding];
                if (binding is null) fresh = await CompareWithLiveAsync(verified.Manifest!, ct);
            }
        }
        var facts = await store.ReadRecoveryResumeFactsAsync(
            receipt?.HostsFencedAt ?? receipt?.RestoreCompletedAt ?? DateTime.MaxValue,
            receipt?.RestoreCompletedAt ?? DateTime.MaxValue,
            receipt is not null, oldWriterClosed, obligationsRetained, findings, ct);
        var failed = (receipt?.Comparisons ?? []).Concat(fresh)
            .Where(item => !item.Matches || !string.Equals(item.Expected, item.Actual, StringComparison.Ordinal))
            .Select(item => item.Subject)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        facts = facts with
        {
            // Both the restore-time comparisons and a fresh recheck must exist and match.
            IdentityComparisonsPassed = receipt?.Comparisons is { Count: > 0 } && fresh.Count > 0 && failed.Count == 0,
            FailedIdentitySubjects = failed,
        };
        var decision = RecoveryResumePolicy.Decide(facts);
        if (!decision.Allowed || checkOnly || receipt is null) return (decision, receipt);

        await store.ChangeModeAsync(new ChangeModeRequest(TaskServerMode.Normal, "recovery resume gate passed"), actorId, ct);
        var resumed = UtcNow;
        receipt = receipt with
        {
            ResumedAt = resumed,
            MeasuredRecoveryTimeSeconds = receipt.LossAt is null ? null : Math.Round((resumed - receipt.LossAt.Value).TotalSeconds, 3),
        };
        await WriteReceiptAsync(receipt, ct);
        await store.AuditRecoveryAsync(actorId, "recovery.resumed", receipt.ManifestId, new { receipt.MeasuredRecoveryTimeSeconds }, ct);
        return (decision, receipt);
    }

    /// <summary>Compares the live target with a manifest: identities, task set and restored cold evidence.</summary>
    private async Task<List<RecoveryIdentityComparison>> CompareWithLiveAsync(RecoveryManifest manifest, CancellationToken ct)
    {
        var live = await store.ReadLiveRecoveryFactsAsync(ct);
        var comparisons = new List<RecoveryIdentityComparison>
        {
            Compare("server id", manifest.Identities.ServerId, live.ServerId),
            Compare("schema version", manifest.Store.SchemaVersion.ToString(), live.SchemaVersion.ToString()),
            Compare("task count", manifest.Identities.TaskCount.ToString(), live.TaskCount.ToString()),
            Compare("task identity digest", manifest.Identities.TaskIdentitySha256, live.TaskIdentitySha256),
            Compare("workspaces", Join(manifest.Identities.Workspaces.Select(item => item.WorkspaceId)), Join(live.Workspaces.Select(item => item.WorkspaceId))),
            Compare("projects", Join(manifest.Identities.Projects.Select(item => $"{item.ProjectId}:{item.TaskKeyPrefix}:{item.TaskCount}")),
                Join(live.Projects.Select(item => $"{item.ProjectId}:{item.TaskKeyPrefix}:{item.TaskCount}"))),
        };
        var archiveRoot = options.ResolveRetentionArchivePath();
        foreach (var cold in manifest.ColdEvidence.Payloads)
        {
            var path = Path.Combine(archiveRoot, cold.RelativePath["cold/".Length..].Replace('/', Path.DirectorySeparatorChar));
            comparisons.Add(Compare($"cold {cold.RelativePath}", cold.Sha256, File.Exists(path) ? await HashFileAsync(path, ct) : "missing"));
        }
        return comparisons;
    }

    private static bool IsStructurallyComplete(RecoveryManifest? manifest)
        => manifest is { ManifestId: not null, Store: not null, Identities.Workspaces: not null, Identities.Projects: not null,
               DataSet.BackupId: not null, DataSet.SetSha256: not null, ColdEvidence.Payloads: not null,
               Repositories: not null, SecretCustody.Clients: not null, PendingHostObligations: not null }
           && manifest.Repositories.All(item => item is { RepositoryId: not null, SampledRefs: not null })
           && manifest.ColdEvidence.Payloads.All(item => item is { RelativePath: not null, Sha256: not null }
                                                          && item.RelativePath.StartsWith("cold/", StringComparison.Ordinal));

    private async Task WriteReceiptAsync(RecoveryRestoreReceipt receipt, CancellationToken ct)
        => await File.WriteAllTextAsync(Path.Combine(store.DataDirectory, RecoveryRestoreReceipt.FileName),
            JsonSerializer.Serialize(receipt, RecoveryJson.Options) + Environment.NewLine, ct);

    private static RecoveryIdentityComparison Compare(string subject, string expected, string actual)
        => new(subject, expected, actual, string.Equals(expected, actual, StringComparison.Ordinal));

    private static string Join(IEnumerable<string> values) => string.Join(",", values.Order(StringComparer.Ordinal));

    private static void RequireEmptyTarget(string path, string label)
    {
        // A skeleton of empty directories (for example one left by a refused restore) still counts as empty.
        if (Directory.Exists(path) && Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Any())
            throw new IOException($"Restore target {label} '{path}' is not empty. Recovery restores only onto an empty target.");
    }

    private static string? HashIfPresent(string path)
        => File.Exists(path) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) : null;

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }

    private static async Task<List<(string Path, long Size, string Sha)>> InventoryAsync(string root, CancellationToken ct)
    {
        var result = new List<(string, long, string)>();
        if (!Directory.Exists(root)) return result;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(path => Path.GetFileName(path) is not "inventory.json" and not "complete.json")
                     .Order(StringComparer.Ordinal))
            result.Add((Path.GetRelativePath(root, path).Replace('\\', '/'), new FileInfo(path).Length, await HashFileAsync(path, ct)));
        return result;
    }

    /// <summary>Same digest rule as the full backup inventory: ordered "path:size:sha" lines.</summary>
    private static string SetHash(IEnumerable<(string Path, long Size, string Sha)> files)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Concat(files.Select(file => $"{file.Path}:{file.Size}:{file.Sha}\n")))));

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: false);
        }
    }
}
