using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Kinds of review subject. A card subject reviews one delivery. A package
/// subject reviews the members of one Dossier wave together (AGT-3400 PK3 A);
/// it never replaces the card subject of any member.
/// </summary>
public static class ReviewSubjectKinds
{
    public const string Card = "card";
    public const string Package = "package";
}

/// <summary>
/// Project setting <c>review.package</c>. <c>off</c> forms no package.
/// <c>shadow</c> forms and reviews packages beside the unchanged per-card
/// review, with no lane, verdict or settlement effect. <c>dossier</c> is the
/// live two-tier review of slice S5 and is not accepted yet.
/// </summary>
public static class ReviewPackageModes
{
    public const string Off = "off";
    public const string Shadow = "shadow";
    public const string Dossier = "dossier";

    public static string? Normalize(string? value)
    {
        var mode = value?.Trim().ToLowerInvariant();
        return mode is Off or Shadow or Dossier ? mode : null;
    }

    /// <summary>Modes this release executes. <c>dossier</c> needs per-member settlement (S5).</summary>
    public static bool IsImplemented(string? mode) => Normalize(mode) is Off or Shadow;
}

/// <summary>
/// Package-tier aspects under PK4 R-B. Build, tests, deterministic checks and
/// requirement fit stay in the per-card check. The grade is computed per
/// member from the package verdicts, as the remote per-card grade is.
/// </summary>
public static class ReviewPackageAspects
{
    public const string CodeQuality = "code-quality";
    public const string DocumentationImpact = "documentation-impact";
    public const string TestsAndEvidence = "tests-and-evidence";
    public const string CrossCardConsistency = "cross-card-consistency";
    public const string Grade = "grade";

    /// <summary>Agent aspects that run once on the package, in plan order.</summary>
    public static readonly IReadOnlyList<string> Agent =
        [CodeQuality, DocumentationImpact, TestsAndEvidence, CrossCardConsistency];

    /// <summary>Per-card aspects whose verdicts the shadow report compares with the package.</summary>
    public static readonly IReadOnlyList<string> ComparedWithCard =
        [CodeQuality, DocumentationImpact, TestsAndEvidence];

    public static bool IsAgent(string? aspect)
        => aspect is not null && Agent.Contains(aspect, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Formation limits of PK5 A; the material budget equals the Remote Review prompt budget.</summary>
public sealed record ReviewPackageLimits(
    int WindowMinutes = 60,
    int MaximumMembers = 4,
    int MaximumFiles = 200,
    int MaximumLines = 12_000)
{
    public static ReviewPackageLimits Default { get; } = new();
}

/// <summary>
/// One member of a package. <see cref="Order"/> is the stack position:
/// dependency order (<c>references.dependsOn</c>), then the Dossier
/// implementation index.
/// </summary>
public sealed record ReviewPackageMemberDto(
    string TaskKey,
    string RunAttemptId,
    string ResultSha,
    int Order,
    string? TaskId = null,
    int? ImplementationIndex = null,
    IReadOnlyList<string>? DependsOn = null,
    string? CardCheckAttemptId = null,
    DateTimeOffset? CardCheckPassedAtUtc = null,
    int ChangedFiles = 0,
    int ChangedLines = 0,
    string? Title = null,
    string? AcceptanceCriteria = null,
    string? ResultRef = null);

/// <summary>
/// Frozen package plan. Commands are agent aspects only; their prompts carry
/// the package context, and the executor appends the Dossier excerpt and the
/// stacked material once per command.
/// </summary>
public sealed record ReviewPackagePlanDto(
    string? RepositoryId,
    string? RepositoryUrl,
    string? IntegrationRef,
    IReadOnlyList<ReviewCommandDto> Commands,
    int DossierExcerptCharacters = 64_000,
    int MaximumFiles = 200,
    int MaximumLines = 12_000,
    bool PrivateRepository = false);

public sealed record CreateReviewPackageSubjectRequest(
    string Project,
    string DossierPath,
    string Mode,
    IReadOnlyList<ReviewPackageMemberDto> Members,
    ReviewPackagePlanDto Plan,
    string IdempotencyKey);

public sealed record ReviewPackageSubjectDto(
    string PackageId,
    string Kind,
    string Project,
    string DossierPath,
    string MembershipDigest,
    string Mode,
    IReadOnlyList<ReviewPackageMemberDto> Members,
    ReviewPackagePlanDto Plan,
    DateTime CreatedAt);

public sealed record ReviewPackageClaimRequest(
    string ExecutorId,
    string InstanceId,
    int RequestedTtlSeconds = 900);

public sealed record ReviewPackageLeaseDto(
    string AttemptId,
    string PackageId,
    int AttemptNumber,
    string LeaseId,
    long Fence,
    DateTime ExpiresAt);

/// <param name="Status"><c>claimed</c> or <c>empty</c>.</param>
public sealed record ReviewPackageClaimResponse(
    string Status,
    ReviewPackageSubjectDto? Subject,
    ReviewPackageLeaseDto? Lease,
    string? Message = null);

public sealed record ReviewPackageRenewRequest(
    string ExecutorId,
    string InstanceId,
    string LeaseId,
    long Fence,
    int RequestedTtlSeconds = 900);

/// <summary>Terminal outcomes of a package attempt. None of them affects a member.</summary>
public static class ReviewPackageOutcomes
{
    public const string Completed = "completed";
    /// <summary>The stacked material exceeds the budget; a package splits and is never truncated.</summary>
    public const string OverBudget = "over-budget";
    /// <summary>A member could not be stacked on the integration tip.</summary>
    public const string StackConflict = "stack-conflict";
    public const string Infrastructure = "infrastructure";

    public static bool IsKnown(string? value)
        => value is Completed or OverBudget or StackConflict or Infrastructure;
}

/// <summary>How a member was applied to the stacked candidate.</summary>
public static class ReviewPackageStackStrategies
{
    /// <summary>The member's own commits cherry-picked in order.</summary>
    public const string Linear = "linear";
    /// <summary>The untouched result commit merged into the candidate, attributed to that merge.</summary>
    public const string Merge = "merge";
}

public sealed record ReviewPackageMemberStepDto(
    string TaskKey,
    string TipBeforeSha,
    string TipAfterSha,
    string Strategy,
    IReadOnlyList<string> CommitShas,
    int ChangedFiles,
    int ChangedLines);

public sealed record ReviewPackageCriterionDto(string Criterion, string Status);

public sealed record ReviewPackageMemberVerdictDto(
    string TaskKey,
    string Aspect,
    string Status,
    string Summary,
    string Classification = "",
    IReadOnlyList<ReviewPackageCriterionDto>? AcceptanceCriteria = null);

/// <summary>
/// One cited finding. <see cref="OwnerTaskKey"/> is the hunk owner from the
/// stacked commits; a finding without an owning hunk is
/// <see cref="CrossCard"/> and owned by the later member in stack order.
/// </summary>
public sealed record ReviewPackageFindingDto(
    string Aspect,
    string File,
    int Line,
    string Severity,
    string Summary,
    string OwnerTaskKey,
    bool CrossCard,
    IReadOnlyList<string> RelatedTaskKeys,
    string? ClaimedTaskKey = null);

public sealed record ReviewPackageCommandEvidenceDto(
    string StepId,
    string Aspect,
    string? CliType,
    string? Model,
    string? ThinkingLevel,
    DateTime StartedAt,
    DateTime FinishedAt,
    int? ExitCode,
    string Classification,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    string StdoutSha256);

public sealed record ReviewPackageReportRequest(
    string ExecutorId,
    string InstanceId,
    string LeaseId,
    long Fence,
    string IdempotencyKey,
    string Outcome,
    string Summary,
    string? IntegrationTipSha,
    string? CandidateSha,
    IReadOnlyList<ReviewPackageMemberStepDto> Steps,
    IReadOnlyList<ReviewPackageMemberVerdictDto> Verdicts,
    IReadOnlyList<ReviewPackageFindingDto> Findings,
    IReadOnlyList<ReviewPackageCommandEvidenceDto> Commands,
    int MaterialFiles,
    int MaterialLines,
    string? HostId = null);

public sealed record ReviewPackageReportDto(
    string ReportId,
    string AttemptId,
    string PackageId,
    string Outcome,
    string PayloadSha256,
    DateTime ReceivedAt,
    bool Duplicate);

/// <summary>Stored package attempt with its admitted report, for read models.</summary>
public sealed record ReviewPackageRecordDto(
    ReviewPackageSubjectDto Subject,
    string Status,
    int Attempts,
    ReviewPackageReportRequest? Report,
    DateTime? ReportedAt);

/// <summary>
/// Membership digest: the Dossier path and every member's task, run attempt
/// and result SHA in stack order. A changed member is a different package.
/// </summary>
public static class ReviewPackageMembershipDigest
{
    public static string Compute(string dossierPath, IEnumerable<ReviewPackageMemberDto> members)
    {
        var builder = new StringBuilder("review-package-v1\n").Append(dossierPath.Trim()).Append('\n');
        foreach (var member in members.OrderBy(member => member.Order))
            builder.Append(member.Order).Append('|').Append(member.TaskKey).Append('|')
                .Append(member.RunAttemptId).Append('|').Append(member.ResultSha.ToLowerInvariant()).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    public static string IdempotencyKey(string digest) => "review-package:" + digest;
}

/// <summary>Boundary validation shared by both review stores.</summary>
public static class ReviewPackageSubjectValidation
{
    private static readonly Regex FullSha = new("^[0-9a-fA-F]{40,64}$", RegexOptions.CultureInvariant);

    public static void Validate(CreateReviewPackageSubjectRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Project))
            throw new ArgumentException("A review package needs its project.");
        if (string.IsNullOrWhiteSpace(request.DossierPath))
            throw new ArgumentException("A review package needs its Dossier path.");
        if (!string.Equals(ReviewPackageModes.Normalize(request.Mode), ReviewPackageModes.Shadow, StringComparison.Ordinal))
            throw new ArgumentException("Only shadow review packages are accepted; review.package dossier arrives with slice S5.");
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("A review package needs an idempotency key.");
        var limits = ReviewPackageLimits.Default;
        if (request.Members.Count is 0 || request.Members.Count > limits.MaximumMembers)
            throw new ArgumentException($"A review package holds 1 to {limits.MaximumMembers} members.");
        if (request.Members.Select(member => member.TaskKey).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != request.Members.Count)
            throw new ArgumentException("A task can be a member of a package only once.");
        if (!request.Members.Select(member => member.Order).Order().SequenceEqual(Enumerable.Range(0, request.Members.Count)))
            throw new ArgumentException("Member stack positions must be 0 to n-1.");
        foreach (var member in request.Members)
        {
            if (string.IsNullOrWhiteSpace(member.TaskKey) || string.IsNullOrWhiteSpace(member.RunAttemptId))
                throw new ArgumentException("Every package member needs its task and run attempt.");
            if (!FullSha.IsMatch(member.ResultSha ?? ""))
                throw new ArgumentException($"Member {member.TaskKey} needs a full result SHA.");
        }
        if (request.Plan.Commands.Count == 0
            || request.Plan.Commands.Any(command => !ReviewCommandKinds.IsAgent(command.ExecutionKind)
                || !ReviewPackageAspects.IsAgent(command.Aspect)
                || string.IsNullOrWhiteSpace(command.Prompt)))
            throw new ArgumentException("A package plan runs only package-tier agent aspects with frozen prompts.");
        if (string.IsNullOrWhiteSpace(request.Plan.IntegrationRef))
            throw new ArgumentException("A package plan needs the integration ref the candidate stacks on.");
    }
}

/// <summary>One finding as the reviewer wrote it, before hunk attribution.</summary>
public sealed record ReviewPackageRawFinding(
    string File,
    int Line,
    string Severity,
    string Summary,
    string? Member,
    IReadOnlyList<string> RelatedMembers);

/// <summary>Parsed structured output of one package aspect.</summary>
public sealed record ReviewPackageAspectOutput(
    IReadOnlyList<ReviewPackageMemberVerdictDto> Verdicts,
    IReadOnlyList<ReviewPackageRawFinding> Findings,
    IReadOnlyList<string> Malformed)
{
    public bool Found => Verdicts.Count > 0 || Findings.Count > 0;
}

/// <summary>
/// Parser of the package review block. The reviewer ends with one fenced
/// block tagged <c>package-review</c> holding <c>members</c> (one verdict per
/// member) and <c>findings</c> (each with file and line). The last block wins.
/// A finding without file or line is not a finding: it is counted as
/// <c>finding-without-citation</c>. A member without a verdict gets status
/// <c>missing</c>.
/// </summary>
public static class ReviewPackageOutputParser
{
    public const string FenceTag = "package-review";
    public const string MissingMemberVerdict = "missing-member-verdict";
    public const string FindingWithoutCitation = "finding-without-citation";
    public const string NoBlock = "no-package-review-block";
    public const string InvalidJson = "invalid-json";

    private static readonly Regex Fence = new(
        @"```[ \t]*(?<tag>package-review|json)[ \t]*\r?\n(?<body>[\s\S]*?)```",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex FileWithLine = new(
        @"^(?<file>.+?):(?<line>\d+)(?:[-:]\d+)?$", RegexOptions.CultureInvariant);

    public static ReviewPackageAspectOutput Parse(string? output, string aspect, IReadOnlyList<string> memberKeys)
    {
        var malformed = new List<string>();
        var json = LastBlock(output ?? "");
        if (json is null)
            return Missing(aspect, memberKeys, [NoBlock], "The reviewer emitted no package-review block.");
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException ex)
        {
            return Missing(aspect, memberKeys, [InvalidJson], "The package-review block is not valid JSON: " + ex.Message);
        }

        using (document)
        {
            var root = document.RootElement;
            var verdicts = new List<ReviewPackageMemberVerdictDto>();
            foreach (var item in Array(root, "members"))
            {
                var key = Canonical(Text(item, "taskKey") ?? Text(item, "member") ?? Text(item, "task"), memberKeys);
                if (key is null)
                {
                    malformed.Add("unknown-member");
                    continue;
                }
                if (verdicts.Any(verdict => string.Equals(verdict.TaskKey, key, StringComparison.Ordinal)))
                {
                    malformed.Add(AspectVerdictMarkerParser.DuplicateKey);
                    continue;
                }
                verdicts.Add(new ReviewPackageMemberVerdictDto(
                    key, aspect, NormalizeStatus(Text(item, "status")), Text(item, "summary") ?? "",
                    "", Criteria(item)));
            }
            foreach (var key in memberKeys.Where(key => verdicts.All(verdict => verdict.TaskKey != key)))
            {
                malformed.Add(MissingMemberVerdict);
                verdicts.Add(new ReviewPackageMemberVerdictDto(key, aspect, "missing",
                    "The reviewer gave this member no verdict.", MissingMemberVerdict));
            }

            var findings = new List<ReviewPackageRawFinding>();
            foreach (var item in Array(root, "findings"))
            {
                var file = Text(item, "file") ?? Text(item, "path");
                var line = Line(item);
                if (file is not null && line is null && FileWithLine.Match(file) is { Success: true } cited)
                {
                    file = cited.Groups["file"].Value;
                    line = int.Parse(cited.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture);
                }
                if (string.IsNullOrWhiteSpace(file) || line is null or < 1)
                {
                    malformed.Add(FindingWithoutCitation);
                    continue;
                }
                findings.Add(new ReviewPackageRawFinding(
                    ReviewPackageFindingAttribution.NormalizePath(file), line.Value,
                    NormalizeSeverity(Text(item, "severity")), Text(item, "summary") ?? "",
                    Canonical(Text(item, "member") ?? Text(item, "taskKey"), memberKeys),
                    Array(item, "relatedMembers").Select(related => Canonical(
                            related.ValueKind == JsonValueKind.String ? related.GetString() : null, memberKeys))
                        .OfType<string>().Distinct(StringComparer.Ordinal).ToArray()));
            }
            return new ReviewPackageAspectOutput(verdicts, findings, malformed);
        }
    }

    private static ReviewPackageAspectOutput Missing(
        string aspect, IReadOnlyList<string> memberKeys, IReadOnlyList<string> malformed, string summary)
        => new(memberKeys.Select(key => new ReviewPackageMemberVerdictDto(
            key, aspect, "missing", summary, malformed[0])).ToArray(), [], malformed);

    private static string? LastBlock(string output)
    {
        var matches = Fence.Matches(output).Cast<Match>().ToArray();
        var tagged = matches.LastOrDefault(match => string.Equals(
            match.Groups["tag"].Value, FenceTag, StringComparison.OrdinalIgnoreCase));
        var chosen = tagged ?? matches.LastOrDefault(match => match.Groups["body"].Value.Contains("\"members\"", StringComparison.Ordinal));
        return chosen?.Groups["body"].Value;
    }

    private static string NormalizeStatus(string? status)
        => ReviewGradingPolicy.IsBlockingToken(status) ? "block"
            : ReviewGradingPolicy.IsConcernToken(status) ? "concerns"
            : string.Equals(status?.Trim(), "pass", StringComparison.OrdinalIgnoreCase) ? "pass"
            : "missing";

    private static string NormalizeSeverity(string? severity)
        => ReviewGradingPolicy.IsBlockingToken(severity) ? "block"
            : ReviewGradingPolicy.IsConcernToken(severity) ? "concern"
            : "note";

    private static string? Canonical(string? key, IReadOnlyList<string> memberKeys)
        => key is null ? null
            : memberKeys.FirstOrDefault(member => string.Equals(member, key.Trim(), StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<ReviewPackageCriterionDto>? Criteria(JsonElement item)
    {
        var criteria = Array(item, "acceptanceCriteria")
            .Select(criterion => new ReviewPackageCriterionDto(
                Text(criterion, "criterion") ?? Text(criterion, "text") ?? "",
                Text(criterion, "status") ?? "not-assessed"))
            .Where(criterion => criterion.Criterion.Length > 0)
            .ToArray();
        return criteria.Length == 0 ? null : criteria;
    }

    private static int? Line(JsonElement item)
    {
        if (!TryProperty(item, "line", out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String
            && Regex.Match(value.GetString() ?? "", @"^\s*(\d+)") is { Success: true } match)
            return int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        return null;
    }

    private static IEnumerable<JsonElement> Array(JsonElement item, string name)
        => TryProperty(item, name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().ToArray() : [];

    private static string? Text(JsonElement item, string name)
        => TryProperty(item, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() is { Length: > 0 } text ? text : null
            : null;

    private static bool TryProperty(JsonElement item, string name, out JsonElement value)
    {
        value = default;
        if (item.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in item.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        return false;
    }
}

/// <summary>A line range of the stacked candidate introduced by one member's commits.</summary>
public sealed record ReviewPackageOwnedRange(string File, int StartLine, int EndLine, string TaskKey);

/// <summary>
/// Hunk ownership of a stacked candidate: the line ranges each member's
/// commits introduced, and the members that touched each file.
/// </summary>
public sealed record ReviewPackageOwnership(
    IReadOnlyList<ReviewPackageOwnedRange> Ranges,
    IReadOnlyDictionary<string, IReadOnlyList<string>> FileMembers);

/// <summary>
/// Assigns every cited finding to the member that owns the cited line. A line
/// no member's hunk introduced has no owner: the finding is cross-card and is
/// assigned to the later member in stack order among the members that touched
/// the file or that the reviewer named, else to the last member. The earlier
/// candidates are named as related and are not charged.
/// </summary>
public static class ReviewPackageFindingAttribution
{
    public static ReviewPackageFindingDto Attribute(
        string aspect,
        ReviewPackageRawFinding finding,
        ReviewPackageOwnership ownership,
        IReadOnlyList<string> stackOrder)
    {
        var file = NormalizePath(finding.File);
        var owner = ownership.Ranges.LastOrDefault(range =>
            string.Equals(range.File, file, StringComparison.Ordinal)
            && finding.Line >= range.StartLine && finding.Line <= range.EndLine)?.TaskKey;
        if (owner is not null)
            return new ReviewPackageFindingDto(aspect, file, finding.Line, finding.Severity, finding.Summary,
                owner, false,
                finding.RelatedMembers.Where(key => key != owner).ToArray(), finding.Member);

        var touched = ownership.FileMembers.TryGetValue(file, out var members) ? members : [];
        var named = new[] { finding.Member }.Concat(finding.RelatedMembers).OfType<string>();
        var relevant = stackOrder.Where(key => touched.Contains(key) || named.Contains(key)).ToArray();
        if (relevant.Length == 0) relevant = stackOrder.ToArray();
        var later = relevant[^1];
        return new ReviewPackageFindingDto(aspect, file, finding.Line, finding.Severity, finding.Summary,
            later, true, relevant.Where(key => key != later).ToArray(), finding.Member);
    }

    /// <summary>Repository-relative forward-slash path without diff prefixes.</summary>
    public static string NormalizePath(string path)
    {
        var normalized = path.Trim().Trim('`', '"', '\'').Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        if (normalized.StartsWith("a/", StringComparison.Ordinal) || normalized.StartsWith("b/", StringComparison.Ordinal))
            normalized = normalized[2..];
        return normalized.TrimStart('/');
    }
}

/// <summary>
/// Admission rules for a package report, applied by both review stores. It
/// keeps one verdict per member and agent aspect, computes each member's
/// grade from its package verdicts, and downgrades a member block that no
/// finding owned by or assigned to that member cites.
/// </summary>
public static class ReviewPackageReportPolicy
{
    public static ReviewPackageReportRequest Normalize(ReviewPackageSubjectDto subject, ReviewPackageReportRequest request)
    {
        if (!ReviewPackageOutcomes.IsKnown(request.Outcome))
            throw new ArgumentException($"Unknown package outcome '{request.Outcome}'.");
        var members = subject.Members.OrderBy(member => member.Order).Select(member => member.TaskKey).ToArray();
        foreach (var finding in request.Findings)
        {
            if (!members.Contains(finding.OwnerTaskKey, StringComparer.Ordinal))
                throw new ArgumentException($"Finding owner '{finding.OwnerTaskKey}' is not a package member.");
            if (string.IsNullOrWhiteSpace(finding.File) || finding.Line < 1)
                throw new ArgumentException("Every package finding cites a file and a line.");
        }
        if (request.Outcome != ReviewPackageOutcomes.Completed)
            return request with { Verdicts = [], Findings = [] };

        var aspects = subject.Plan.Commands.Select(command => command.Aspect).Distinct(StringComparer.Ordinal).ToArray();
        var verdicts = new List<ReviewPackageMemberVerdictDto>();
        foreach (var member in members)
        {
            foreach (var aspect in aspects)
            {
                var verdict = request.Verdicts.FirstOrDefault(item => item.TaskKey == member && item.Aspect == aspect)
                    ?? new ReviewPackageMemberVerdictDto(member, aspect, "missing",
                        "The package report has no verdict for this member.", ReviewPackageOutputParser.MissingMemberVerdict);
                var cited = request.Findings.Any(finding => finding.Aspect == aspect && finding.OwnerTaskKey == member);
                if (verdict.Status == "block" && !cited)
                    verdict = verdict with
                    {
                        Status = "concerns",
                        Classification = ReviewVerdictCitationPolicy.BlockWithoutCitation,
                    };
                verdicts.Add(verdict);
            }
            var grade = ReviewGradingPolicy.Grade(verdicts.Where(item => item.TaskKey == member).Select(item => item.Status));
            verdicts.Add(new ReviewPackageMemberVerdictDto(member, ReviewPackageAspects.Grade,
                grade switch
                {
                    ReviewGrade.ProductFailure => "block",
                    ReviewGrade.PassWithConcerns => "concerns",
                    _ => "pass",
                },
                $"Computed from {aspects.Length} package aspect verdicts.", "computed"));
        }
        return request with { Verdicts = verdicts };
    }
}

/// <summary>Where a Dossier card stands for package formation.</summary>
public static class ReviewPackageCandidateStages
{
    /// <summary>The per-card check of the current delivery passed.</summary>
    public const string CardCheckPassed = "card-check-passed";
    /// <summary>The card waits for its per-card check; a package may wait for it.</summary>
    public const string InReview = "in-review";
    /// <summary>The card is being coded; a package never waits for it.</summary>
    public const string Coding = "coding";
}

/// <summary>
/// One card of a Dossier at a formation tick. <see cref="Paths"/> and
/// <see cref="ChangedLines"/> are its review material; <see cref="SizeKnown"/>
/// is false when the host could not measure it.
/// </summary>
public sealed record ReviewPackageCandidate(
    string TaskKey,
    string DossierPath,
    string Stage,
    DateTimeOffset? CardCheckPassedAtUtc,
    IReadOnlyList<string> Paths,
    int ChangedLines,
    bool SizeKnown = true,
    int? ImplementationIndex = null,
    IReadOnlyList<string>? DependsOn = null);

public sealed record ReviewPackageFormationInput(
    DateTimeOffset NowUtc,
    IReadOnlyList<ReviewPackageCandidate> Candidates,
    ReviewPackageLimits Limits);

public static class ReviewPackageFormationDecisions
{
    public const string Form = "form";
    public const string Wait = "wait";
    /// <summary>The card stays on the per-card review only; no package can hold it.</summary>
    public const string PerCard = "per-card";
}

/// <summary>
/// One formation decision for a Dossier. <see cref="Members"/> are in stack
/// order. <see cref="Waiting"/> names the cards in review a wait is for, and
/// <see cref="Considered"/> every card whose check had passed.
/// </summary>
public sealed record ReviewPackageFormationDecision(
    string DossierPath,
    string Decision,
    string Reason,
    IReadOnlyList<string> Members,
    DateTimeOffset? CloseAtUtc,
    int UnionFiles,
    int UnionLines,
    bool SizeUnknown,
    IReadOnlyList<string> Waiting,
    IReadOnlyList<string> Considered,
    DateTimeOffset DecidedAtUtc);

public static class ReviewPackageAttemptStatuses
{
    public const string Queued = "queued";
    public const string Leased = "leased";
    public const string Reported = "reported";
    /// <summary>Every allowed attempt lost its lease; the shadow package is not retried.</summary>
    public const string Abandoned = "abandoned";
}

/// <summary>Durable attempt state of one package, persisted by either review store.</summary>
public sealed record ReviewPackageAttemptState(
    string Status,
    int Attempts,
    string? AttemptId = null,
    string? LeaseId = null,
    long Fence = 0,
    string? ExecutorId = null,
    string? InstanceId = null,
    DateTime? ExpiresAt = null,
    string? ReportKey = null,
    string? ReportSha256 = null,
    string? ReportId = null,
    DateTime? ReportedAt = null,
    string? Outcome = null)
{
    public static ReviewPackageAttemptState Queued { get; } = new(ReviewPackageAttemptStatuses.Queued, 0);
}

/// <summary>A fenced package write that does not match the current lease or report.</summary>
public sealed class ReviewPackageConflictException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Lease rules of a package attempt. A shadow package gets at most
/// <see cref="MaximumAttempts"/> leases; an expired last lease abandons it.
/// Every renewal and report must carry the current lease, fence, executor
/// and instance; a report replay with the same key and payload is a duplicate.
/// </summary>
public static class ReviewPackageLeasePolicy
{
    public const int MaximumAttempts = 2;

    public static int Ttl(int requested) => Math.Clamp(requested, 60, 3600);

    /// <summary>The state after lease expiry is applied at <paramref name="now"/>.</summary>
    public static ReviewPackageAttemptState Expire(ReviewPackageAttemptState state, DateTime now)
        => state.Status == ReviewPackageAttemptStatuses.Leased && state.ExpiresAt <= now
            ? state.Attempts >= MaximumAttempts
                ? state with { Status = ReviewPackageAttemptStatuses.Abandoned }
                : state with { Status = ReviewPackageAttemptStatuses.Queued }
            : state;

    public static bool Claimable(ReviewPackageAttemptState state, DateTime now)
        => Expire(state, now).Status == ReviewPackageAttemptStatuses.Queued;

    public static (ReviewPackageAttemptState State, ReviewPackageLeaseDto Lease) Claim(
        string packageId, ReviewPackageAttemptState state, ReviewPackageClaimRequest request, DateTime now)
    {
        if (!Claimable(state, now))
            throw new ReviewPackageConflictException("package-not-claimable", "The review package is not queued.");
        var attempt = state.Attempts + 1;
        var next = state with
        {
            Status = ReviewPackageAttemptStatuses.Leased,
            Attempts = attempt,
            AttemptId = $"{packageId}-a{attempt}",
            LeaseId = "rpl_" + Guid.NewGuid().ToString("N"),
            Fence = state.Fence + 1,
            ExecutorId = request.ExecutorId,
            InstanceId = request.InstanceId,
            ExpiresAt = now.AddSeconds(Ttl(request.RequestedTtlSeconds)),
        };
        return (next, Lease(packageId, next));
    }

    public static (ReviewPackageAttemptState State, ReviewPackageLeaseDto Lease) Renew(
        string packageId, string attemptId, ReviewPackageAttemptState state, ReviewPackageRenewRequest request, DateTime now)
    {
        RequireLease(attemptId, state, request.ExecutorId, request.InstanceId, request.LeaseId, request.Fence, now);
        var next = state with { ExpiresAt = now.AddSeconds(Ttl(request.RequestedTtlSeconds)) };
        return (next, Lease(packageId, next));
    }

    /// <summary>
    /// True for a replay of the stored report; false when the report is new and
    /// admissible. Anything else is a conflict.
    /// </summary>
    public static bool IsDuplicateReport(
        string attemptId, ReviewPackageAttemptState state, ReviewPackageReportRequest request,
        string payloadSha256, DateTime now)
    {
        if (state.Status == ReviewPackageAttemptStatuses.Reported)
        {
            if (state.AttemptId == attemptId && state.ReportKey == request.IdempotencyKey
                && state.ReportSha256 == payloadSha256)
                return true;
            throw new ReviewPackageConflictException("idempotency-conflict",
                "The review package already holds a different report.");
        }
        RequireLease(attemptId, state, request.ExecutorId, request.InstanceId, request.LeaseId, request.Fence, now);
        return false;
    }

    public static ReviewPackageAttemptState Reported(
        ReviewPackageAttemptState state, ReviewPackageReportRequest request, string payloadSha256, string reportId, DateTime now)
        => state with
        {
            Status = ReviewPackageAttemptStatuses.Reported,
            ReportKey = request.IdempotencyKey,
            ReportSha256 = payloadSha256,
            ReportId = reportId,
            ReportedAt = now,
            Outcome = request.Outcome,
        };

    public static ReviewPackageLeaseDto Lease(string packageId, ReviewPackageAttemptState state)
        => new(state.AttemptId!, packageId, state.Attempts, state.LeaseId!, state.Fence, state.ExpiresAt!.Value);

    private static void RequireLease(string attemptId, ReviewPackageAttemptState state, string executorId,
        string instanceId, string leaseId, long fence, DateTime now)
    {
        if (state.Status != ReviewPackageAttemptStatuses.Leased || state.AttemptId != attemptId)
            throw new ReviewPackageConflictException("package-attempt-not-leased", "The package attempt holds no lease.");
        if (state.LeaseId != leaseId || state.Fence != fence
            || state.ExecutorId != executorId || state.InstanceId != instanceId)
            throw new ReviewPackageConflictException("stale-fence", "The package lease belongs to another holder.");
        if (state.ExpiresAt <= now)
            throw new ReviewPackageConflictException("package-lease-expired", "The package lease expired.");
    }

    public static string PayloadSha256(ReviewPackageReportRequest request)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)))).ToLowerInvariant();
}

/// <summary>The package that holds one member delivery.</summary>
public sealed record ReviewPackageMembershipDto(string PackageId, string TaskKey, string RunAttemptId);

/// <summary>
/// Builds the hunk ownership of a stacked candidate from each member's
/// zero-context diff (<c>git diff -U0</c> from the candidate before the member
/// to the candidate after it), in stack order. A later member's hunk shifts,
/// replaces or deletes the lines earlier members introduced, so every line of
/// the final candidate names the member whose commits last wrote it. A line
/// no member wrote has no owner.
/// </summary>
public static class ReviewPackageOwnershipIndex
{
    private static readonly Regex Hunk = new(
        @"^@@ -(?<os>\d+)(?:,(?<oc>\d+))? \+(?<ns>\d+)(?:,(?<nc>\d+))? @@", RegexOptions.CultureInvariant);

    private sealed record HunkRange(int OldStart, int OldCount, int NewStart, int NewCount);

    public static ReviewPackageOwnership Build(IReadOnlyList<(string TaskKey, string ZeroContextDiff)> steps)
    {
        var owned = new Dictionary<string, Dictionary<int, string>>(StringComparer.Ordinal);
        var fileMembers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (taskKey, diff) in steps)
        foreach (var file in Files(diff))
        {
            var lines = file.OldPath is not null && owned.Remove(file.OldPath, out var previous)
                ? previous : new Dictionary<int, string>();
            var members = file.OldPath is not null && fileMembers.Remove(file.OldPath, out var earlier)
                ? earlier : new List<string>();
            if (!members.Contains(taskKey)) members.Add(taskKey);
            if (file.NewPath is null)
            {
                fileMembers[file.OldPath!] = members;
                continue;
            }
            owned[file.NewPath] = Shift(lines, file.Hunks, taskKey);
            fileMembers[file.NewPath] = members;
        }

        var ranges = new List<ReviewPackageOwnedRange>();
        foreach (var (path, lines) in owned.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            ReviewPackageOwnedRange? open = null;
            foreach (var (line, owner) in lines.OrderBy(entry => entry.Key))
            {
                if (open is not null && open.TaskKey == owner && open.EndLine + 1 == line)
                {
                    open = open with { EndLine = line };
                    continue;
                }
                if (open is not null) ranges.Add(open);
                open = new ReviewPackageOwnedRange(path, line, line, owner);
            }
            if (open is not null) ranges.Add(open);
        }
        return new ReviewPackageOwnership(ranges,
            fileMembers.ToDictionary(entry => entry.Key, entry => (IReadOnlyList<string>)entry.Value.ToArray(),
                StringComparer.Ordinal));
    }

    private static Dictionary<int, string> Shift(
        Dictionary<int, string> lines, IReadOnlyList<HunkRange> hunks, string taskKey)
    {
        var shifted = new Dictionary<int, string>();
        foreach (var (line, owner) in lines)
        {
            var delta = 0;
            var removed = false;
            foreach (var hunk in hunks)
            {
                if (hunk.OldCount > 0)
                {
                    if (line >= hunk.OldStart && line < hunk.OldStart + hunk.OldCount)
                    {
                        removed = true;
                        break;
                    }
                    if (hunk.OldStart + hunk.OldCount - 1 < line) delta += hunk.NewCount - hunk.OldCount;
                }
                else if (hunk.OldStart < line)
                {
                    delta += hunk.NewCount;
                }
            }
            if (!removed) shifted[line + delta] = owner;
        }
        foreach (var hunk in hunks)
            for (var offset = 0; offset < hunk.NewCount; offset++)
                shifted[hunk.NewStart + offset] = taskKey;
        return shifted;
    }

    private sealed record FileDiff(string? OldPath, string? NewPath, IReadOnlyList<HunkRange> Hunks);

    private static IEnumerable<FileDiff> Files(string diff)
    {
        string? oldPath = null, newPath = null;
        var hunks = new List<HunkRange>();
        var open = false;
        foreach (var raw in diff.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (raw.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                if (open) yield return new FileDiff(oldPath, newPath, hunks);
                var (left, right) = GitHeaderPaths(raw["diff --git ".Length..]);
                oldPath = left;
                newPath = right;
                hunks = [];
                open = true;
            }
            else if (!open) continue;
            else if (raw.StartsWith("rename from ", StringComparison.Ordinal)) oldPath = raw["rename from ".Length..];
            else if (raw.StartsWith("rename to ", StringComparison.Ordinal)) newPath = raw["rename to ".Length..];
            else if (raw.StartsWith("new file mode", StringComparison.Ordinal)) oldPath = null;
            else if (raw.StartsWith("deleted file mode", StringComparison.Ordinal)) newPath = null;
            else if (raw == "--- /dev/null") oldPath = null;
            else if (raw == "+++ /dev/null") newPath = null;
            else if (Hunk.Match(raw) is { Success: true } match)
                hunks.Add(new HunkRange(
                    Number(match, "os", 0), Number(match, "oc", 1), Number(match, "ns", 0), Number(match, "nc", 1)));
        }
        if (open) yield return new FileDiff(oldPath, newPath, hunks);
    }

    private static (string? Left, string? Right) GitHeaderPaths(string paths)
    {
        var split = paths.IndexOf(" b/", StringComparison.Ordinal);
        if (!paths.StartsWith("a/", StringComparison.Ordinal) || split < 0) return (null, null);
        return (paths[2..split], paths[(split + 3)..]);
    }

    private static int Number(Match match, string group, int fallback)
        => match.Groups[group].Success
            ? int.Parse(match.Groups[group].Value, System.Globalization.CultureInfo.InvariantCulture)
            : fallback;
}
