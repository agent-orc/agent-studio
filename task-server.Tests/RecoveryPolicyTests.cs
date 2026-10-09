using AgentStudio.TaskServer.Contracts;
using AgentStudio.TaskServer.Recovery;
using Xunit;

namespace TaskServer.Tests;

public sealed class RecoveryPolicyTests
{
    private static RecoveryCheckFacts Healthy() => new(
        RecoveryManifest.CurrentSchema, true, true, true, "set", "set", "set", [], [], 24, 24, "1.0.0", "1.0.0",
        [new("repo", "https://origin", "refs/heads/main", "abc", RecoveryGitProbeOutcome.RefPresent, null)],
        true, true, true,
        [new("runner:a", "runner", "a", RecoveryCredentialCustody.SecretBundle)],
        [], true, true, true, true);

    public static TheoryData<string, Func<RecoveryCheckFacts, RecoveryCheckFacts>, RecoveryFindingSeverity> Faults => new()
    {
        { "incomplete-set", facts => facts with { CompleteMarkerPresent = false }, RecoveryFindingSeverity.BlocksRestore },
        { "corrupted-hash", facts => facts with { ActualSetSha256 = "other", ChangedFiles = ["snapshot.db"] }, RecoveryFindingSeverity.BlocksRestore },
        { "missing-cold-payload", facts => facts with { ActualSetSha256 = "other", ChangedFiles = ["cold/a.zip"], MissingColdPayloads = ["cold/a.zip"] }, RecoveryFindingSeverity.BlocksRestore },
        { "schema-mismatch", facts => facts with { ManifestSchemaVersion = 25 }, RecoveryFindingSeverity.BlocksRestore },
        { "manifest-missing", facts => facts with { ManifestPresent = false }, RecoveryFindingSeverity.BlocksRestore },
        { "manifest-unsupported", facts => facts with { ManifestSchema = "agent-studio.recovery-manifest/v9" }, RecoveryFindingSeverity.BlocksRestore },
        { "git-origin-unavailable", facts => facts with { GitProbes = [facts.GitProbes[0] with { Outcome = RecoveryGitProbeOutcome.OriginUnavailable }] }, RecoveryFindingSeverity.BlocksResume },
        { "git-ref-missing", facts => facts with { GitProbes = [facts.GitProbes[0] with { Outcome = RecoveryGitProbeOutcome.RefMissing }] }, RecoveryFindingSeverity.BlocksResume },
        { "git-origin-undeclared", facts => facts with { GitProbes = [facts.GitProbes[0] with { Outcome = RecoveryGitProbeOutcome.OriginNotDeclared }] }, RecoveryFindingSeverity.BlocksResume },
        { "client-credentials-lost", facts => facts with { Clients = [facts.Clients[0] with { Custody = RecoveryCredentialCustody.Undeclared }] }, RecoveryFindingSeverity.BlocksResume },
        { "client-credentials-lost", facts => facts with { Clients = [facts.Clients[0] with { Custody = RecoveryCredentialCustody.ReEnrol }] }, RecoveryFindingSeverity.BlocksResume },
        { "client-credentials-lost", facts => facts with { Clients = [facts.Clients[0] with { Custody = "unknown-custody" }] }, RecoveryFindingSeverity.BlocksResume },
        { "secret-bundle-missing", facts => facts with { SecretBundlePresent = false, SecretBundleDigestMatches = false }, RecoveryFindingSeverity.BlocksResume },
        { "secret-bundle-changed", facts => facts with { SecretBundleDigestMatches = false }, RecoveryFindingSeverity.BlocksResume },
        { "pending-host-obligation", facts => facts with { Obligations = [new(RecoveryObligationKinds.RunnerOutbox, "a", "run", "artifact-replay", 2, "")] }, RecoveryFindingSeverity.BlocksResume },
        { "release-differs", facts => facts with { TargetRelease = "1.0.1" }, RecoveryFindingSeverity.Advisory },
        { "git-ref-moved", facts => facts with { GitProbes = [facts.GitProbes[0] with { Outcome = RecoveryGitProbeOutcome.RefMoved, Detail = "def" }] }, RecoveryFindingSeverity.BlocksResume },
        { "git-ref-moved-proven", facts => facts with { GitProbes = [facts.GitProbes[0] with { Outcome = RecoveryGitProbeOutcome.RefMovedWithImmutableProof, Detail = "def" }] }, RecoveryFindingSeverity.Advisory },
        { "copy-receipt-missing", facts => facts with { OffHostCopyReceiptPresent = false }, RecoveryFindingSeverity.BlocksRestore },
        { "copy-receipt-invalid", facts => facts with { CopyReceiptValid = false }, RecoveryFindingSeverity.BlocksRestore },
        { "manifest-digest-mismatch", facts => facts with { ManifestDigestMatches = false }, RecoveryFindingSeverity.BlocksRestore },
        { "manifest-unreadable", facts => facts with { ManifestReadable = false, ManifestSchema = null }, RecoveryFindingSeverity.BlocksRestore },
    };

    [Fact]
    public void Healthy_set_has_no_findings()
    {
        var report = RecoveryCheckPolicy.Evaluate(Healthy());
        Assert.Empty(report.Findings);
        Assert.True(report.RestoreAllowed);
        Assert.True(report.ResumeAllowed);
    }

    [Theory]
    [MemberData(nameof(Faults))]
    public void Each_fault_maps_to_one_specific_finding(string code, Func<RecoveryCheckFacts, RecoveryCheckFacts> fault, RecoveryFindingSeverity severity)
    {
        var report = RecoveryCheckPolicy.Evaluate(fault(Healthy()));
        var finding = Assert.Single(report.Findings, item => item.Code == code);
        Assert.Equal(severity, finding.Severity);
        Assert.False(string.IsNullOrWhiteSpace(finding.Guidance));
        Assert.Equal(severity != RecoveryFindingSeverity.BlocksRestore, report.RestoreAllowed);
    }

    [Fact]
    public void Missing_cold_payload_is_not_also_reported_as_corruption()
    {
        var report = RecoveryCheckPolicy.Evaluate(Healthy() with
        {
            ActualSetSha256 = "other", ChangedFiles = ["cold/a.zip"], MissingColdPayloads = ["cold/a.zip"],
        });
        Assert.DoesNotContain(report.Findings, item => item.Code == "corrupted-hash");
    }

    [Fact]
    public void Incomplete_set_stops_before_hash_checks()
    {
        var report = RecoveryCheckPolicy.Evaluate(Healthy() with { InventoryPresent = false, ActualSetSha256 = null });
        Assert.Equal("incomplete-set", Assert.Single(report.Findings).Code);
    }

    [Fact]
    public void Older_target_schema_names_the_release_to_install()
    {
        var finding = Assert.Single(RecoveryCheckPolicy.Evaluate(Healthy() with { ManifestSchemaVersion = 25 }).Findings);
        Assert.Contains("Install release 1.0.0 (schema 25)", finding.Guidance, StringComparison.Ordinal);
    }

    private static RecoveryResumeFacts Ready() => new(TaskServerMode.Maintenance, true, 0, true, [], [], false, [], true);

    public static TheoryData<string, Func<RecoveryResumeFacts, RecoveryResumeFacts>> ResumeBlocks => new()
    {
        { "no-recovery-restore", facts => facts with { RestoredFromRecoverySet = false } },
        { "identity-comparison-failed", facts => facts with { IdentityComparisonsPassed = false } },
        { "maintenance-required", facts => facts with { Mode = TaskServerMode.Normal } },
        { "attempts-unresolved", facts => facts with { UnresolvedAttempts = 1 } },
        { "old-writer-open", facts => facts with { OldWriterClosedAttested = false } },
        { "stale-hosts-unfenced", facts => facts with { StaleRunnerPrincipals = ["runner:a"] } },
        { "host-obligation-unreconciled", facts => facts with { LiveObligations = [new(RecoveryObligationKinds.RunnerOutbox, "a", "run", "artifact-replay", 1, "")] } },
        { "git-origin-unavailable", facts => facts with { OpenSetFindings = [new("git-origin-unavailable", RecoveryFindingSeverity.BlocksResume, "repo", "g")] } },
        { "git-ref-moved", facts => facts with { OpenSetFindings = [new("git-ref-moved", RecoveryFindingSeverity.BlocksResume, "repo", "g")] } },
        { "client-credentials-lost", facts => facts with { OpenSetFindings = [new("client-credentials-lost", RecoveryFindingSeverity.BlocksResume, "runner:a", "g")] } },
        { "copy-receipt-missing", facts => facts with { OpenSetFindings = [new("copy-receipt-missing", RecoveryFindingSeverity.BlocksRestore, "copy-receipt.json", "g")] } },
    };

    [Theory]
    [MemberData(nameof(ResumeBlocks))]
    public void Resume_gate_blocks_each_unproven_condition(string code, Func<RecoveryResumeFacts, RecoveryResumeFacts> change)
    {
        var decision = RecoveryResumePolicy.Decide(change(Ready()));
        Assert.False(decision.Allowed);
        Assert.Equal(code, Assert.Single(decision.Blockers).Code);
    }

    [Fact]
    public void Resume_gate_passes_when_everything_is_proven()
        => Assert.True(RecoveryResumePolicy.Decide(Ready()).Allowed);

    [Fact]
    public void Retained_obligations_and_reconciled_client_do_not_block()
    {
        var decision = RecoveryResumePolicy.Decide(Ready() with
        {
            LiveObligations = [new(RecoveryObligationKinds.SalvageBundle, "a", "run", "bundle-only", 0, "")],
            ObligationsRetainedAttested = true,
            ReconciledClientPrincipals = ["runner:a"],
            OpenSetFindings =
            [
                new("client-credentials-lost", RecoveryFindingSeverity.BlocksResume, "runner:a", "g"),
                new("pending-host-obligation", RecoveryFindingSeverity.BlocksResume, "a", "g"),
            ],
        });
        Assert.True(decision.Allowed);
    }

    [Fact]
    public void One_reconciled_client_does_not_clear_another_lost_client()
    {
        var decision = RecoveryResumePolicy.Decide(Ready() with
        {
            ReconciledClientPrincipals = ["runner:a"],
            OpenSetFindings =
            [
                new("client-credentials-lost", RecoveryFindingSeverity.BlocksResume, "runner:a", "g"),
                new("client-credentials-lost", RecoveryFindingSeverity.BlocksResume, "studio:b", "g"),
            ],
        });
        Assert.Equal("studio:b", Assert.Single(decision.Blockers).Subject);
    }
}

public sealed class RecoveryRestoreBindingTests
{
    private static readonly RecoveryManifest Manifest = new(
        RecoveryManifest.CurrentSchema, "rcv_a", DateTime.UnixEpoch, null,
        new(RecoveryStoreTypes.TaskServerSqlite, "1.0.0", "sha", 24),
        new("srv", [], [], 0, "digest"),
        new("database-snapshot", "a", "set-a", "complete.json", "inventory.json", 1, 1),
        new(0, []), [], [], [], new(null, null, null, null, []), [], []);

    public static TheoryData<string, string, string, string?, bool, string?, string> Bindings => new()
    {
        { "same set", "rcv_a", "a", "manifest-a", true, "manifest-a", "" },
        { "other manifest id", "rcv_b", "a", "manifest-a", true, "manifest-a", "manifest id" },
        { "other backup", "rcv_a", "b", "manifest-a", true, "manifest-a", "backup id" },
        { "other set digest", "rcv_a", "a", "manifest-a", true, "manifest-a", "set digest" },
        { "rewritten manifest", "rcv_a", "a", "manifest-a", true, "manifest-forged", "manifest digest" },
        { "receipt without digest", "rcv_a", "a", null, true, "manifest-a", "manifest digest" },
        { "unreadable manifest", "rcv_a", "a", "manifest-a", false, null, "manifest unreadable" },
    };

    [Theory]
    [MemberData(nameof(Bindings))]
    public void Resume_binds_the_verified_copy_to_the_restored_set(
        string _, string receiptManifestId, string receiptBackupId, string? receiptManifestSha, bool manifestReadable,
        string? copyManifestSha, string expectedDifference)
    {
        var receiptSet = expectedDifference == "set digest" ? "set-b" : "set-a";
        var finding = RecoveryCheckPolicy.BindToRestore(receiptManifestId, receiptBackupId, receiptSet, receiptManifestSha,
            manifestReadable ? Manifest : null, copyManifestSha, "/copy");
        if (expectedDifference.Length == 0)
        {
            Assert.Null(finding);
            return;
        }
        Assert.NotNull(finding);
        Assert.Equal("recovery-copy-mismatch", finding.Code);
        Assert.Equal(RecoveryFindingSeverity.BlocksResume, finding.Severity);
        Assert.Contains(expectedDifference, finding.Subject, StringComparison.Ordinal);
        Assert.Contains(RecoveryResumePolicy.Decide(new RecoveryResumeFacts(
            TaskServerMode.Maintenance, true, 0, true, [], [], false, [finding], true)).Blockers,
            item => item.Code == "recovery-copy-mismatch");
    }

    [Fact]
    public void Failed_identity_recheck_names_the_failed_subjects()
    {
        var decision = RecoveryResumePolicy.Decide(new RecoveryResumeFacts(
            TaskServerMode.Maintenance, true, 0, true, [], [], false, [], false,
            FailedIdentitySubjects: ["cold cold/a.zip"]));
        var blocker = Assert.Single(decision.Blockers);
        Assert.Equal("identity-comparison-failed", blocker.Code);
        Assert.Equal("cold cold/a.zip", blocker.Subject);
    }
}

public sealed class OriginRefProbeTests
{
    [Fact]
    public void Ls_remote_listing_maps_refs_to_commits()
    {
        var refs = OriginRefProbe.ParseListing("aaaa\tHEAD\nbbbb\trefs/heads/main\n");
        Assert.Equal("bbbb", refs["refs/heads/main"]);
        Assert.Equal(2, refs.Count);
    }
}
