namespace AgentStudio.TaskServer.Recovery;

public enum RecoveryFindingSeverity
{
    /// <summary>The set must not be restored.</summary>
    BlocksRestore,

    /// <summary>The set may be restored for inspection, but the target must stay in Maintenance.</summary>
    BlocksResume,

    /// <summary>Recorded in the receipt; recovery continues.</summary>
    Advisory,
}

public sealed record RecoveryFinding(string Code, RecoveryFindingSeverity Severity, string Subject, string Guidance);

public sealed record RecoveryCheckReport(IReadOnlyList<RecoveryFinding> Findings)
{
    public bool RestoreAllowed => Findings.All(item => item.Severity != RecoveryFindingSeverity.BlocksRestore);
    public bool ResumeAllowed => Findings.All(item => item.Severity == RecoveryFindingSeverity.Advisory);
}

public enum RecoveryGitProbeOutcome
{
    RefPresent,
    RefMissing,
    RefMoved,
    RefMovedWithImmutableProof,
    OriginUnavailable,
    OriginNotDeclared,
}

public sealed record RecoveryGitProbe(string RepositoryId, string? Origin, string RefName, string ExpectedSha, RecoveryGitProbeOutcome Outcome, string? Detail);

/// <summary>Observed facts about one recovery set copy and the target it would restore onto.</summary>
public sealed record RecoveryCheckFacts(
    string? ManifestSchema,
    bool ManifestPresent,
    bool CompleteMarkerPresent,
    bool InventoryPresent,
    string? CompleteSetSha256,
    string? InventorySetSha256,
    string? ActualSetSha256,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> MissingColdPayloads,
    int ManifestSchemaVersion,
    int TargetSchemaVersion,
    string ManifestRelease,
    string TargetRelease,
    IReadOnlyList<RecoveryGitProbe> GitProbes,
    bool SecretBundleDeclared,
    bool SecretBundlePresent,
    bool SecretBundleDigestMatches,
    IReadOnlyList<RecoveryClientCredential> Clients,
    IReadOnlyList<RecoveryHostObligation> Obligations,
    bool OffHostCopyReceiptPresent,
    bool CopyReceiptRequired,
    bool CopyReceiptValid,
    bool ManifestDigestMatches);

/// <summary>
/// Pure verification policy for a recovery set. Every failure maps to one stable code and specific
/// guidance, so the drill and the operator see the same recovery instruction for the same fault.
/// </summary>
public static class RecoveryCheckPolicy
{
    public static RecoveryCheckReport Evaluate(RecoveryCheckFacts facts)
    {
        var findings = new List<RecoveryFinding>();

        if (!facts.ManifestPresent)
            findings.Add(new("manifest-missing", RecoveryFindingSeverity.BlocksRestore, "recovery-manifest.json",
                "The copy has no recovery manifest, so identities, origins and credential custody cannot be checked. Copy the set again with `task-server recovery copy`, which carries the manifest."));
        else if (!string.Equals(facts.ManifestSchema, RecoveryManifest.CurrentSchema, StringComparison.Ordinal))
            findings.Add(new("manifest-unsupported", RecoveryFindingSeverity.BlocksRestore, facts.ManifestSchema ?? "(none)",
                $"This release reads {RecoveryManifest.CurrentSchema}. Use the release that captured the set to verify and restore it."));

        if (facts.CopyReceiptRequired && !facts.OffHostCopyReceiptPresent)
            findings.Add(new("copy-receipt-missing", RecoveryFindingSeverity.BlocksRestore, "copy-receipt.json",
                "The copied set has no receipt binding its manifest to the verified set. Copy it again from the live authority or use another verified off-host copy."));
        else if (facts.OffHostCopyReceiptPresent && !facts.CopyReceiptValid)
            findings.Add(new("copy-receipt-invalid", RecoveryFindingSeverity.BlocksRestore, "copy-receipt.json",
                "The copy receipt is unreadable or does not identify this manifest and set. Discard this copy and verify another off-host copy; do not edit the receipt."));
        else if (facts.OffHostCopyReceiptPresent && !facts.ManifestDigestMatches)
            findings.Add(new("manifest-digest-mismatch", RecoveryFindingSeverity.BlocksRestore, "recovery-manifest.json",
                "The manifest changed after the copy receipt was written. Discard this copy and verify another off-host copy; do not edit the manifest or receipt."));

        if (!facts.CompleteMarkerPresent || !facts.InventoryPresent)
        {
            findings.Add(new("incomplete-set", RecoveryFindingSeverity.BlocksRestore,
                !facts.CompleteMarkerPresent ? "complete.json" : "inventory.json",
                "The set stopped before its completion marker. Do not write a marker by hand. Restore the newest earlier verified copy, or capture a new set from the live authority."));
            return Finish(findings, facts);
        }

        if (facts.MissingColdPayloads.Count > 0)
            findings.Add(new("missing-cold-payload", RecoveryFindingSeverity.BlocksRestore,
                string.Join(", ", facts.MissingColdPayloads),
                "Archived task evidence referenced by the snapshot is absent. Copy the payload from another verified copy of the same set (matching set digest). If no copy has it, capture a new set while the source archive still holds it. A restore without it loses that evidence."));

        var changed = facts.ChangedFiles.Except(facts.MissingColdPayloads, StringComparer.Ordinal).ToList();
        var digestsAgree = string.Equals(facts.ActualSetSha256, facts.InventorySetSha256, StringComparison.OrdinalIgnoreCase)
                           && string.Equals(facts.ActualSetSha256, facts.CompleteSetSha256, StringComparison.OrdinalIgnoreCase);
        if (changed.Count > 0 || (!digestsAgree && facts.MissingColdPayloads.Count == 0))
            findings.Add(new("corrupted-hash", RecoveryFindingSeverity.BlocksRestore,
                changed.Count > 0 ? string.Join(", ", changed) : "set digest",
                "Set members no longer match the recorded inventory hashes. Discard this copy, fetch another off-host copy whose copy receipt shows the same set digest, and verify again. Do not edit inventory.json to match."));

        if (facts.ManifestPresent && facts.ManifestSchemaVersion != facts.TargetSchemaVersion)
            findings.Add(new("schema-mismatch", RecoveryFindingSeverity.BlocksRestore,
                $"set schema {facts.ManifestSchemaVersion}, target schema {facts.TargetSchemaVersion}",
                facts.ManifestSchemaVersion > facts.TargetSchemaVersion
                    ? $"The target release is older than the set. Install release {facts.ManifestRelease} (schema {facts.ManifestSchemaVersion}) on the empty target, restore, then verify before any upgrade."
                    : $"Restore with the release that wrote the set ({facts.ManifestRelease}, schema {facts.ManifestSchemaVersion}), verify identities, then upgrade through the installation upgrade path. Do not let a newer release migrate an unverified restore."));
        else if (facts.ManifestPresent && !string.Equals(facts.ManifestRelease, facts.TargetRelease, StringComparison.Ordinal))
            findings.Add(new("release-differs", RecoveryFindingSeverity.Advisory,
                $"set {facts.ManifestRelease}, target {facts.TargetRelease}",
                "Schemas match, so the restore is compatible. Record both releases in the restore receipt."));

        return Finish(findings, facts);
    }

    private static RecoveryCheckReport Finish(List<RecoveryFinding> findings, RecoveryCheckFacts facts)
    {
        foreach (var probe in facts.GitProbes)
        {
            var subject = $"{probe.RepositoryId} {probe.RefName}@{Short(probe.ExpectedSha)}";
            switch (probe.Outcome)
            {
                case RecoveryGitProbeOutcome.OriginUnavailable:
                    findings.Add(new("git-origin-unavailable", RecoveryFindingSeverity.BlocksResume, $"{subject} via {probe.Origin}",
                        "The canonical origin could not be reached, so restored task results cannot be checked against published refs. Restore network access or the origin credential, or point the custody declaration at a verified mirror. Keep the target in Maintenance until the refs verify."));
                    break;
                case RecoveryGitProbeOutcome.OriginNotDeclared:
                    findings.Add(new("git-origin-undeclared", RecoveryFindingSeverity.BlocksResume, subject,
                        "The set records a result for this repository but no origin. Add the repository origin to the custody declaration and verify again."));
                    break;
                case RecoveryGitProbeOutcome.RefMissing:
                    findings.Add(new("git-ref-missing", RecoveryFindingSeverity.BlocksResume, subject,
                        "The origin does not hold the canonical commit the authority recorded. Publish it from the host salvage or source bundle before resuming. Without it, the restored task history points at a commit nobody can fetch."));
                    break;
                case RecoveryGitProbeOutcome.RefMoved:
                    findings.Add(new("git-ref-moved", RecoveryFindingSeverity.BlocksResume, subject,
                        $"The ref now points elsewhere ({probe.Detail}); the recorded commit has not been proven reachable from the origin. Restore the recorded ref or publish an immutable ref at the recorded commit, then verify the recovery set again before resuming."));
                    break;
                case RecoveryGitProbeOutcome.RefMovedWithImmutableProof:
                    findings.Add(new("git-ref-moved-proven", RecoveryFindingSeverity.Advisory, subject,
                        $"The ref moved, but an immutable result ref on the origin still points at the recorded commit ({probe.Detail})."));
                    break;
            }
        }

        if (facts.SecretBundleDeclared && !facts.SecretBundlePresent)
            findings.Add(new("secret-bundle-missing", RecoveryFindingSeverity.BlocksResume, "secret bundle",
                "The encrypted secret bundle is not at its declared location. Fetch the copy captured with this set from off-host custody and verify its digest. Client re-enrolment does not recover configuration secrets in the bundle."));
        else if (facts.SecretBundleDeclared && !facts.SecretBundleDigestMatches)
            findings.Add(new("secret-bundle-changed", RecoveryFindingSeverity.BlocksResume, "secret bundle",
                "The secret bundle digest differs from the one captured with this set. Use the encrypted bundle copy made with this set and verify its digest before resuming."));

        var lost = facts.Clients
            .Where(client => client.Custody != RecoveryCredentialCustody.SecretBundle
                             || !facts.SecretBundlePresent || !facts.SecretBundleDigestMatches)
            .Select(client => client.PrincipalId)
            .ToList();
        foreach (var principalId in lost)
            findings.Add(new("client-credentials-lost", RecoveryFindingSeverity.BlocksResume, principalId,
                "Stored principal hashes cannot recreate this client's cleartext credential. After restore, run `task-server recovery reenrol --principal <id> --credential-out <file>` for this principal. It revokes old credentials and issues one fresh credential in Maintenance. Deliver the new credential through the client's protected token file before reconnecting."));

        foreach (var obligation in facts.Obligations)
            findings.Add(new("pending-host-obligation", RecoveryFindingSeverity.BlocksResume,
                $"{obligation.Kind} {obligation.RunnerId}/{obligation.RunId}",
                obligation.Kind == RecoveryObligationKinds.SalvageBundle
                    ? "The accepted result exists only as a source bundle, not on the origin. Keep the bundle and publish or re-run before resuming. It is not a cache."
                    : $"Host {obligation.RunnerId} still held {obligation.Backlog} unacknowledged outbox record(s) in state '{obligation.State}'. Keep that host's outbox and worktree. After restore, let it drain against the restored authority, which accepts or rejects each record under fencing. Never delete it as cache."));

        return new RecoveryCheckReport(findings);
    }

    private static string Short(string sha) => sha.Length > 12 ? sha[..12] : sha;
}
