using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Review;

/// <summary>
/// File-backed review store for package subjects (AGT-3400 S4) on the
/// monolith review plane. It is separate from the per-card review subject
/// sidecar and the attempt authority, so a package can never change a card's
/// subject, attempt, lane or settlement. Each package keeps a write-once
/// subject, one atomically replaced attempt state and its admitted report.
/// </summary>
public sealed class ReviewPackageStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly JsonSerializerOptions Line = new(JsonSerializerDefaults.Web);
    private readonly string _root;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, ReviewPackageFormationDecision> _lastLogged = new(StringComparer.OrdinalIgnoreCase);

    public ReviewPackageStore(string root, TimeProvider clock)
    {
        _root = root;
        _clock = clock;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Appends a formation decision to <c>plans/&lt;project&gt;.jsonl</c>, as the
    /// batch gate logs its plans. A decision that repeats the previous one for
    /// the same Dossier (and, for per-card, the same card) is not appended again,
    /// so a 30-second tick that keeps waiting logs the wait once.
    /// </summary>
    public bool AppendFormationDecision(string project, ReviewPackageFormationDecision decision)
    {
        lock (_gate)
        {
            var key = project + "\n" + decision.DossierPath + "\n"
                + (decision.Decision == ReviewPackageFormationDecisions.PerCard ? string.Join(",", decision.Members) : "");
            if (_lastLogged.TryGetValue(key, out var previous) && SameDecision(previous, decision))
                return false;
            var folder = Path.Combine(_root, "plans");
            Directory.CreateDirectory(folder);
            using (var stream = new FileStream(Path.Combine(folder, SafeName(project) + ".jsonl"),
                       FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                JsonSerializer.Serialize(stream, new ReviewPackagePlanLogEntry(project, decision), Line);
                stream.WriteByte((byte)'\n');
                stream.Flush(flushToDisk: true);
            }
            _lastLogged[key] = decision;
            return true;
        }
    }

    public IReadOnlyList<ReviewPackageFormationDecision> ReadFormationDecisions(string project)
    {
        var path = Path.Combine(_root, "plans", SafeName(project) + ".jsonl");
        if (!File.Exists(path)) return [];
        return File.ReadLines(path)
            .Where(line => line.Length > 0)
            .Select(line => JsonSerializer.Deserialize<ReviewPackagePlanLogEntry>(line, Line)?.Decision)
            .OfType<ReviewPackageFormationDecision>()
            .ToArray();
    }

    /// <summary>The package that already holds this delivery, if any.</summary>
    public string? PackageOf(string taskKey, string runAttemptId)
    {
        var path = MemberPath(taskKey, runAttemptId);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    /// <summary>
    /// Creates a queued package subject. A replay of the same membership
    /// returns the stored subject; a member delivery already in another
    /// package is a conflict.
    /// </summary>
    public ReviewPackageSubjectDto Create(CreateReviewPackageSubjectRequest request)
    {
        ReviewPackageSubjectValidation.Validate(request);
        var digest = ReviewPackageMembershipDigest.Compute(request.DossierPath, request.Members);
        var packageId = "rpkg_" + digest[..24];
        lock (_gate)
        {
            var subjectPath = Path.Combine(PackageDirectory(packageId), "subject.json");
            if (File.Exists(subjectPath)) return Read<ReviewPackageSubjectDto>(subjectPath);
            foreach (var member in request.Members)
                if (PackageOf(member.TaskKey, member.RunAttemptId) is { } other && other != packageId)
                    throw new ReviewPackageConflictException("member-already-packaged",
                        $"{member.TaskKey} run {member.RunAttemptId} is already a member of {other}.");
            var subject = new ReviewPackageSubjectDto(packageId, ReviewSubjectKinds.Package, request.Project,
                request.DossierPath, digest, ReviewPackageModes.Shadow,
                request.Members.OrderBy(member => member.Order).ToArray(), request.Plan, Now);
            Directory.CreateDirectory(PackageDirectory(packageId));
            WriteState(packageId, ReviewPackageAttemptState.Queued);
            WriteOnce(subjectPath, subject);
            foreach (var member in subject.Members)
            {
                var path = MemberPath(member.TaskKey, member.RunAttemptId);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, packageId);
            }
            return subject;
        }
    }

    public ReviewPackageClaimResponse Claim(ReviewPackageClaimRequest request)
    {
        lock (_gate)
        {
            var now = Now;
            foreach (var (subject, state) in Packages().OrderBy(entry => entry.Subject.CreatedAt))
            {
                var current = ReviewPackageLeasePolicy.Expire(state, now);
                if (current != state) WriteState(subject.PackageId, current);
                if (current.Status != ReviewPackageAttemptStatuses.Queued) continue;
                var (leased, lease) = ReviewPackageLeasePolicy.Claim(subject.PackageId, current, request, now);
                WriteState(subject.PackageId, leased);
                return new ReviewPackageClaimResponse("claimed", subject, lease);
            }
            return new ReviewPackageClaimResponse("empty", null, null, "No review package is queued.");
        }
    }

    public ReviewPackageLeaseDto Renew(string attemptId, ReviewPackageRenewRequest request)
    {
        lock (_gate)
        {
            var (subject, state) = ByAttempt(attemptId);
            var (renewed, lease) = ReviewPackageLeasePolicy.Renew(subject.PackageId, attemptId, state, request, Now);
            WriteState(subject.PackageId, renewed);
            return lease;
        }
    }

    /// <summary>
    /// Admits a package report under the shared report policy. The normalized
    /// report is returned so the caller can project shadow observations; a
    /// duplicate returns the stored one.
    /// </summary>
    public (ReviewPackageReportDto Receipt, ReviewPackageSubjectDto Subject, ReviewPackageReportRequest Report) Report(
        string attemptId, ReviewPackageReportRequest request)
    {
        lock (_gate)
        {
            var (subject, state) = ByAttempt(attemptId);
            var hash = ReviewPackageLeasePolicy.PayloadSha256(request);
            var reportPath = Path.Combine(PackageDirectory(subject.PackageId), "report.json");
            var now = Now;
            if (ReviewPackageLeasePolicy.IsDuplicateReport(attemptId, state, request, hash, now))
                return (new ReviewPackageReportDto(state.ReportId!, attemptId, subject.PackageId, state.Outcome!,
                    hash, state.ReportedAt!.Value, true), subject, Read<ReviewPackageReportRequest>(reportPath));
            var normalized = ReviewPackageReportPolicy.Normalize(subject, request);
            var reportId = "rprp_" + Guid.NewGuid().ToString("N");
            WriteOnce(reportPath, normalized);
            WriteState(subject.PackageId, ReviewPackageLeasePolicy.Reported(state, request, hash, reportId, now));
            return (new ReviewPackageReportDto(reportId, attemptId, subject.PackageId, request.Outcome, hash, now, false),
                subject, normalized);
        }
    }

    /// <summary>Every package of a project with its state and admitted report.</summary>
    public IReadOnlyList<ReviewPackageRecordDto> List(string project)
    {
        lock (_gate)
        {
            return Packages()
                .Where(entry => string.Equals(entry.Subject.Project, project, StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.Subject.CreatedAt)
                .Select(entry =>
                {
                    var report = Path.Combine(PackageDirectory(entry.Subject.PackageId), "report.json");
                    return new ReviewPackageRecordDto(entry.Subject, entry.State.Status, entry.State.Attempts,
                        File.Exists(report) ? Read<ReviewPackageReportRequest>(report) : null, entry.State.ReportedAt);
                })
                .ToArray();
        }
    }

    private (ReviewPackageSubjectDto Subject, ReviewPackageAttemptState State) ByAttempt(string attemptId)
    {
        var separator = attemptId.LastIndexOf("-a", StringComparison.Ordinal);
        var packageId = separator > 0 ? attemptId[..separator] : attemptId;
        var subjectPath = Path.Combine(PackageDirectory(packageId), "subject.json");
        if (!File.Exists(subjectPath)) throw new KeyNotFoundException($"Review package attempt '{attemptId}' was not found.");
        return (Read<ReviewPackageSubjectDto>(subjectPath), ReadState(packageId));
    }

    private IEnumerable<(ReviewPackageSubjectDto Subject, ReviewPackageAttemptState State)> Packages()
    {
        var folder = Path.Combine(_root, "packages");
        if (!Directory.Exists(folder)) yield break;
        foreach (var directory in Directory.EnumerateDirectories(folder))
        {
            var subjectPath = Path.Combine(directory, "subject.json");
            if (!File.Exists(subjectPath)) continue;
            var subject = Read<ReviewPackageSubjectDto>(subjectPath);
            yield return (subject, ReadState(subject.PackageId));
        }
    }

    private ReviewPackageAttemptState ReadState(string packageId)
    {
        var path = Path.Combine(PackageDirectory(packageId), "state.json");
        return File.Exists(path) ? Read<ReviewPackageAttemptState>(path) : ReviewPackageAttemptState.Queued;
    }

    private void WriteState(string packageId, ReviewPackageAttemptState state)
    {
        var path = Path.Combine(PackageDirectory(packageId), "state.json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, state, Json);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private static void WriteOnce<T>(string path, T value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, Json);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: false);
    }

    private static T Read<T>(string path)
        => JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Json)
            ?? throw new InvalidDataException($"Missing {typeof(T).Name} at {path}.");

    private string PackageDirectory(string packageId) => Path.Combine(_root, "packages", SafeName(packageId));

    private string MemberPath(string taskKey, string runAttemptId)
        => Path.Combine(_root, "members", SafeName(taskKey), SafeName(runAttemptId));

    private static bool SameDecision(ReviewPackageFormationDecision previous, ReviewPackageFormationDecision next)
        => previous.Decision == next.Decision && previous.Reason == next.Reason
           && previous.CloseAtUtc == next.CloseAtUtc
           && previous.Members.SequenceEqual(next.Members) && previous.Waiting.SequenceEqual(next.Waiting);

    private static string SafeName(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32];
}

/// <summary>One line of the formation log.</summary>
public sealed record ReviewPackagePlanLogEntry(string Project, ReviewPackageFormationDecision Decision);
