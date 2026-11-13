using AgentStudio.Management;
using AgentStudio.Security;

namespace AgentStudio.Personas;

/// <summary>Metadata-only fact shared with the Operations view. Value is a bounded state or number, never source text.</summary>
public sealed record OpsPostureFactV1(string Id, string Scope, string? Value, DateTime? ObservedAt,
    string Source, TimeSpan Freshness);
public sealed record OpsPostureFactsV1(DateTime TakenAt, IReadOnlyList<OpsPostureFactV1> Facts);
public sealed record OpsWardenFinding(OpsPostureFactV1 Fact, string Rule, int Floor, string Title,
    int Impact = 1, int Urgency = 1, double Confidence = 0);

/// <summary>Projects existing backup and security view answers into a small, secret-free vocabulary.</summary>
public static class OpsPostureReader
{
    private static readonly TimeSpan Daily = TimeSpan.FromHours(26);
    private static readonly TimeSpan Weekly = TimeSpan.FromDays(8);
    private static readonly TimeSpan RestoreFreshness = TimeSpan.FromDays(36);
    public static OpsPostureFactsV1 Read(DateTime now, BackupOverview? backup,
        SecurityPostureResponse? security, IReadOnlyList<OpsPostureFactV1>? published = null)
    {
        var facts = new Dictionary<string, OpsPostureFactV1>(StringComparer.Ordinal);
        void Add(string id, string? value, DateTime? at, string source, TimeSpan freshness = default,
            string scope = "installation") => facts[id] = new(id, scope, value, at, source,
                freshness == default ? Daily : freshness);
        if (backup is not null)
        {
            var at = Parse(backup.GeneratedAt);
            Add("backup.schedule", backup.Configured && backup.State != "disabled" ? "enabled" : "missing", at,
                "installation-backups");
            Add("backup.destination", backup.OffHost.State == "not-configured" ? "missing" : "configured", at,
                "installation-backups");
            Add("backup.last-good", backup.LastGoodAgeHours?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none",
                at, "installation-backups");
            foreach (var part in new[] { "instance", "configuration", "secrets" })
            {
                // The overview proves an archive exists, but has no per-part manifest projection.
                // The backup producer may publish these exact ids with its coverage proof.
                Add("backup.coverage." + part, backup.LastGood is null ? "uncovered" : null,
                    backup.LastGood is null ? at : null, "installation-backups");
                Add("backup.last-good." + part, null, null, "backup-part-manifest");
            }
            var verifiedAt = backup.LastVerification is { } verification ? Parse(verification.At) : null;
            Add("backup.restore-test", verifiedAt is null ? "never" : backup.LastVerification!.Outcome == "verified"
                ? "passed" : "failed", verifiedAt ?? at, "installation-backups", RestoreFreshness);
            Add("backup.off-host", backup.OffHost.State == "current" &&
                backup.OffHost.Kind is "sftp" or "s3" ? "current" :
                backup.OffHost.Kind == "directory" ? null : "missing", at,
                "installation-backups");
            // A key id on the backup host is not evidence of an owner-held escrow copy.
            Add("backup.escrow", null, null, "owner-escrow-record", Weekly);
        }
        if (security is not null)
        {
            var at = security.GeneratedAt;
            Add("security.exposure", security.Findings.Any(f => f.Area == SecurityPostureAreas.Exposure && f.Score >= 80)
                ? "unexpected" : "ok", at, SecurityPostureEndpoints.Route);
            Add("security.tls", null, null, "edge-certificate", Daily);
            Add("security.origin", null, null, "edge-origin-check", Daily);
            var credentials = security.Credentials.Tokens.Where(t => t.State is not ("revoked" or "disabled"))
                .Select(t => (t.ExpiresAt, t.State)).Concat(security.Credentials.Inventory
                    .Where(i => i.State is not ("revoked" or "disabled"))
                    .Select(i => (i.ExpiresAt, i.State))).ToArray();
            var credentialState = credentials.Any(c => c.ExpiresAt <= now && c.ExpiresAt is not null || c.State == "expired")
                ? "expired-referenced" : credentials.Any(c => c.ExpiresAt is null) ? "no-expiry"
                : credentials.Any(c => c.ExpiresAt <= now.AddDays(7)) ? "within-7d"
                : credentials.Any(c => c.ExpiresAt <= now.AddDays(14)) ? "within-14d" : "ok";
            Add("security.credentials", security.Credentials.InventorySource == SecurityPostureSources.Unavailable
                    && !security.Credentials.Tokens.Any() ? null : credentialState,
                at, SecurityPostureEndpoints.Route);
            Add("security.second-factor", security.SecondFactor.Users.Any(u => !u.Disabled && u.Role == "owner" && u.State == "missing")
                ? "owner-missing" : "ok", at, SecurityPostureEndpoints.Route);
            Add("security.patch", security.Findings.Any(f => f.Area == SecurityPostureAreas.Versions && f.Score >= 75)
                ? "overdue" : security.Versions.HostSource == SecurityPostureSources.Unavailable ? null : "ok",
                at, SecurityPostureEndpoints.Route);
            Add("security.audit", security.Audit.IntegrityState == ControlPlaneAuditIntegrityStates.Failed ? "broken"
                : security.Audit.IntegrityCheckedAt is null ? null : "verified",
                security.Audit.IntegrityCheckedAt, SecurityPostureEndpoints.Route);
            Add("security.findings", security.Findings.Any(f => f.Area == SecurityPostureAreas.Cards && f.Score >= 75)
                ? "high-overdue" : "ok", at, SecurityPostureEndpoints.Route);
            Add("security.break-glass", security.Audit.ActiveBreakGlassWindows > 0 ? null : "ok",
                at, SecurityPostureEndpoints.Route);
        }
        // Host inspection, edge checks, escrow and website restore proof are published by their own producers.
        // They override a projection only by exact id and carry a bounded state, never raw command output.
        foreach (var fact in published ?? [])
        {
            if (!Known.Contains(fact.Id) || fact.Scope != "installation" ||
                !PublishedSources.Contains(fact.Source) || fact.Value is { Length: > 64 } ||
                fact.Value is not null && !SafeValue(fact.Id, fact.Value)) continue;
            facts[fact.Id] = fact;
        }
        foreach (var id in Known.Where(id => id != "backup.website.restore.signin" && !facts.ContainsKey(id)))
            Add(id, null, null, "unavailable");
        return new(now, facts.Values.OrderBy(f => f.Id, StringComparer.Ordinal).ToArray());
    }

    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal)
    {
        "backup.schedule", "backup.destination", "backup.last-good", "backup.last-good.instance",
        "backup.last-good.configuration", "backup.last-good.secrets", "backup.coverage.instance",
        "backup.coverage.configuration", "backup.coverage.secrets", "backup.restore-test",
        "backup.website.restore.signin", "backup.off-host", "backup.escrow", "security.exposure",
        "security.tls", "security.origin", "security.credentials", "security.second-factor",
        "security.patch", "security.audit", "security.findings", "security.break-glass"
    };
    private static readonly IReadOnlySet<string> PublishedSources = new HashSet<string>(StringComparer.Ordinal)
    {
        "networked-backup-status", "backup-status", "backup-part-manifest", "restore-test",
        "website.restore.signin", "owner-escrow-record", "host.inspect", "edge-certificate",
        "edge-origin-check", "credential-inventory", "audit-verify", "security-posture"
    };
    private static bool SafeValue(string id, string value) => id switch
    {
        var ageId when ageId.StartsWith("backup.last-good", StringComparison.Ordinal) => value == "none" || double.TryParse(value,
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
            out var age) && age is >= 0 and < 100000,
        "backup.schedule" => value is "enabled" or "missing",
        "backup.destination" => value is "configured" or "missing",
        "backup.restore-test" => value is "never" or "passed" or "failed",
        "backup.website.restore.signin" => value is "passed" or "failed",
        "backup.off-host" => value is "current" or "missing",
        "backup.escrow" => value is "matched" or "unconfirmed" or "mismatched",
        "security.exposure" => value is "ok" or "unexpected",
        "security.tls" => value is "ok" or "expired" or "within-14d" ||
            double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var days) && days is >= -1000 and < 10000,
        "security.origin" => value is "protected" or "unprotected",
        "security.credentials" => value is "ok" or "expired-referenced" or "within-7d" or "within-14d" or "no-expiry",
        "security.second-factor" => value is "ok" or "owner-missing",
        "security.patch" => value is "ok" or "overdue",
        "security.audit" => value is "verified" or "broken",
        "security.findings" => value is "ok" or "high-overdue",
        "security.break-glass" => value is "ok" or "open-unreviewed",
        _ when id.StartsWith("backup.coverage.", StringComparison.Ordinal) => value is "covered" or "uncovered",
        _ => false,
    };
    private static DateTime? Parse(string? value) => DateTime.TryParse(value, null,
        System.Globalization.DateTimeStyles.AdjustToUniversal, out var at) ? at.ToUniversalTime() : null;
}

/// <summary>Deterministic floor rules. The descriptor remains the authority for their numbers.</summary>
public static class OpsWardenEvaluator
{
    public static IReadOnlyList<OpsWardenFinding> Evaluate(OpsPostureFactsV1 snapshot, PersonaDescriptorV1 descriptor)
    {
        var result = new List<OpsWardenFinding>();
        var scheduleMissing = snapshot.Facts.Any(f => f.Id == "backup.schedule" && f.Value == "missing");
        foreach (var fact in snapshot.Facts)
        {
            if (scheduleMissing && fact.Id.StartsWith("backup.", StringComparison.Ordinal)
                && fact.Id != "backup.schedule") continue;
            var stale = fact.Value is null || fact.ObservedAt is null ||
                snapshot.TakenAt - fact.ObservedAt > fact.Freshness;
            var rule = stale ? "fact.stale" : Rule(fact, snapshot.TakenAt);
            if (rule is null) continue;
            var floor = PersonaImportanceService.DescriptorFloor(descriptor, rule);
            if (floor < 70) continue;
            var calibration = fact.Id == "backup.schedule" && fact.Value == "missing";
            result.Add(new(fact, rule, floor,
                calibration ? "No scheduled backup on the networked installation"
                    : stale ? "Cannot see " + fact.Id.Replace('.', ' ') : "Operations posture: " + fact.Id.Replace('.', ' '),
                calibration ? 5 : 1, calibration ? 5 : 1, calibration ? .94 : 0));
        }
        return result;
    }

    private static string? Rule(OpsPostureFactV1 fact, DateTime now)
    {
        var v = fact.Value;
        return fact.Id switch
        {
            "backup.schedule" or "backup.destination" when v == "missing" => "backup.none-scheduled",
            var ageId when ageId.StartsWith("backup.last-good", StringComparison.Ordinal) && v == "none" => "backup.last-good-age>48h",
            var ageId when ageId.StartsWith("backup.last-good", StringComparison.Ordinal) && double.TryParse(v, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var age) && age > 48 => "backup.last-good-age>48h",
            var ageId when ageId.StartsWith("backup.last-good", StringComparison.Ordinal) && double.TryParse(v, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var age) && age > 26 => "backup.last-good-age>26h",
            var id when id.StartsWith("backup.coverage.", StringComparison.Ordinal) && v == "uncovered" => "backup.coverage-gap",
            "backup.restore-test" when v == "never" => "restore-test.never",
            "backup.restore-test" when v == "failed" => "restore-test.never",
            "backup.restore-test" when fact.ObservedAt < now.AddDays(-35) => "restore-test.age>35d",
            "backup.website.restore.signin" when v == "failed" => "restore-test.never",
            "backup.off-host" when v == "missing" => "off-host-copy.missing",
            "backup.escrow" when v != "matched" => "escrow.unconfirmed",
            "security.exposure" when v == "unexpected" => "exposure.unexpected-public-port",
            "security.tls" when v == "expired" => "tls.expired",
            "security.tls" when v == "within-14d" => "tls.expires-in<14d",
            "security.tls" when double.TryParse(v, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var days) && days <= 0 => "tls.expired",
            "security.tls" when double.TryParse(v, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var days) && days < 14 => "tls.expires-in<14d",
            "security.origin" when v == "unprotected" => "tls.origin-unprotected",
            "security.credentials" when v == "expired-referenced" => "credential.expired",
            "security.credentials" when v == "within-7d" => "credential.expires-in<7d",
            "security.credentials" when v == "no-expiry" => "credential.no-expiry",
            "security.second-factor" when v == "owner-missing" => "second-factor.owner-missing",
            "security.patch" when v == "overdue" => "patch.security-update-pending>7d",
            "security.audit" when v == "broken" => "audit.chain-broken",
            "security.findings" when v == "high-overdue" => "security-finding.high-open>7d",
            "security.break-glass" when v == "open-unreviewed" => "break-glass.window-unreviewed",
            _ => null,
        };
    }

    public static string Fingerprint(OpsPostureFactV1 fact) => "ops:" + fact.Id + ":" + fact.Scope;
    public static string? OpenBackupCard(IReadOnlyList<AgentStudio.Shared.TaskInfo> cards)
        => cards.FirstOrDefault(c => c.State != AgentStudio.Shared.TaskStates.Archive
            && c.Title.Contains("backup", StringComparison.OrdinalIgnoreCase)
            && (c.Title.Contains("schedul", StringComparison.OrdinalIgnoreCase)
                || c.Title.Contains("timer", StringComparison.OrdinalIgnoreCase)))?.Id;
}

public static class OpsWardenDecision
{
    public static PersonaItemDraftV1 ForMissingSchedule(DateTime now) => new(
        PersonaItemKinds.DecisionRequest, "Choose the backup schedule",
        "Choose when to install the backup timer. With no answer, nothing changes.", null,
        [new PersonaEvidenceV1("ops-source:installation-backups", true),
            new PersonaEvidenceV1("ops-fact:backup.schedule", true)],
        4, 3, .8, "ops:backup.schedule-decision:installation", now.Date.AddDays(2),
        Options: ["Daily 03:15", "Daily 01:00"], RecommendedOption: "Daily 03:15",
        Area: "operations.backups");
}

public static class OpsWardenFixProposal
{
    public static PersonaItemDraftV1 ForOpenBackupCard(string cardId) => new(
        PersonaItemKinds.Proposal, "Install the backup timer and record a restore test",
        "Install the documented daily backup schedule and record the first scratch restore. " +
        "Append the evidence to open backup card " + cardId + ".", null,
        [new PersonaEvidenceV1("card:" + cardId, true),
            new PersonaEvidenceV1("ops-fact:backup.schedule", true)],
        4, 4, .9, "ops:backup.schedule-fix:installation", DraftCard: false,
        DossierKey: "persona-team", DossierSection: "operations-watch",
        Area: "operations.backups");
}
