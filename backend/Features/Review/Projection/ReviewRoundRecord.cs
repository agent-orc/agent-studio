namespace AgentStudio.Review;

/// <summary>
/// The canonical record of one review round, written by both review planes.
///
/// <para>
/// Before this record existed, the local plane wrote <c>code-review-grade-*.md</c>
/// plus council and steering files while the remote plane wrote
/// <c>remote-review-grade-*.md</c>, <c>aspect-*.md/json</c> and an
/// <c>integration_failed</c> timeline row. Every surface picked one of those
/// families, so the escalation banner reported "0 review rounds · Grade not
/// recorded" for a card that carried seven remote reports (AGT-2689). The record
/// removes that choice: both planes write the same shape, and every review
/// surface reads only this file.
/// </para>
///
/// <para>
/// The rendered Markdown report stays an artifact and is referenced from
/// <see cref="ReportRef"/>; nothing parses Markdown for state any more. Cards
/// written before the record existed are covered once by
/// <see cref="ReviewRoundMarkdownBackfill"/>, which derives the same shape from
/// the legacy report files.
/// </para>
///
/// <para>
/// Named "round" rather than "attempt" because two other things already own that
/// word: the fenced remote <c>ReviewAttempt</c> in the attempt authority, and the
/// <c>.metadata/review-attempt.json</c> epoch marker. This record is one entry in
/// the operator-visible review history, keyed by
/// <see cref="AttemptId"/> inside its <see cref="Plane"/>.
/// </para>
/// </summary>
public sealed record ReviewRoundRecord
{
    /// <summary>Bumped when the on-disk shape changes incompatibly.</summary>
    public int SchemaVersion { get; init; } = ReviewRoundRecordSchema.CurrentVersion;

    /// <summary>One of <see cref="ReviewPlanes"/>.</summary>
    public string Plane { get; init; } = ReviewPlanes.Local;

    /// <summary>
    /// Identity of the round inside its plane. Remote uses the fenced
    /// ReviewAttempt id; local uses the grade run timestamp, which the local
    /// writer already keeps unique to the millisecond.
    /// </summary>
    public string AttemptId { get; init; } = "";

    /// <summary>Commit the round reviewed. Null when the plane recorded none.</summary>
    public string? SubjectSha { get; init; }

    /// <summary>Instant the round was recorded, UTC.</summary>
    public DateTime ReceivedAt { get; init; }

    /// <summary>
    /// Plane-native terminal outcome, verbatim: <c>Pass</c>, <c>ProductFailure</c>,
    /// <c>ReviewInfra</c>, <c>Inconclusive</c> or <c>Cancellation</c> for remote;
    /// <c>pass</c>, <c>concerns</c> or <c>block</c> for the local grade pass.
    /// </summary>
    public string Outcome { get; init; } = "";

    /// <summary>Quality grade <c>A</c>-<c>D</c>, or null when the plane assigns none.</summary>
    public string? Grade { get; init; }

    /// <summary>One-line reviewer summary; empty when the plane supplied none.</summary>
    public string Summary { get; init; } = "";

    /// <summary>
    /// Job-folder-relative name of the rendered report this record was written
    /// beside, so a surface can link back to the artifact.
    /// </summary>
    public string? ReportRef { get; init; }

    /// <summary>
    /// Deterministic build and test proof for this round. Empty when the round
    /// carried no build-tests verdict at all, which is what
    /// <see cref="ReviewBuildTestsResults.NotProven"/> means downstream.
    /// </summary>
    public List<ReviewRoundCommandRow> BuildTests { get; init; } = [];

    /// <summary>Semantic aspect verdicts, in report order.</summary>
    public List<ReviewRoundAspect> Aspects { get; init; } = [];

    /// <summary>
    /// What the delivery gate did with this round. Null when the round never
    /// reached the gate, which reads as "not attempted".
    /// </summary>
    public ReviewRoundDeliveryGate? DeliveryGate { get; init; }
}

/// <summary>
/// One build or test command a review round ran, with the proof it produced.
/// This is the single source the Evidence tab reads for build-tests.
/// </summary>
public sealed record ReviewRoundCommandRow
{
    /// <summary>Plan step that owns the command, e.g. <c>verify-1</c>.</summary>
    public string StepId { get; init; } = "";

    /// <summary>Command line as executed. Empty when the plane recorded none.</summary>
    public string Command { get; init; } = "";

    /// <summary>Process exit code, or null when the command was signalled or not run.</summary>
    public int? ExitCode { get; init; }

    /// <summary>One of <see cref="ReviewVerdicts"/>.</summary>
    public string Status { get; init; } = ReviewVerdicts.Missing;

    /// <summary>Verdict summary as the reviewer phrased it.</summary>
    public string Summary { get; init; } = "";
}

/// <summary>One semantic aspect verdict inside a review round.</summary>
public sealed record ReviewRoundAspect
{
    /// <summary>Aspect name, e.g. <c>documentation-impact</c>.</summary>
    public string Name { get; init; } = "";

    /// <summary>One of <see cref="ReviewVerdicts"/>.</summary>
    public string Verdict { get; init; } = ReviewVerdicts.Missing;

    /// <summary>
    /// The reviewer's reason, quoted verbatim on every surface. A blocking
    /// aspect without a reason is why the banner used to say nothing useful.
    /// </summary>
    public string Summary { get; init; } = "";
}

/// <summary>What the delivery gate decided for one review round.</summary>
public sealed record ReviewRoundDeliveryGate
{
    /// <summary>One of <see cref="ReviewDeliveryStates"/>.</summary>
    public string Result { get; init; } = ReviewDeliveryStates.NotAttempted;

    /// <summary>Why the gate reached that result; empty when it needs no reason.</summary>
    public string Reason { get; init; } = "";

    /// <summary>Integration branch the gate targeted, when one was resolved.</summary>
    public string? IntegrationBranch { get; init; }
}

/// <summary>Which plane produced a review round.</summary>
public static class ReviewPlanes
{
    public const string Local = "local";
    public const string Remote = "remote";

    /// <summary>Normalize a plane token; anything unknown reads as local.</summary>
    public static string Normalize(string? plane) =>
        string.Equals(plane?.Trim(), Remote, StringComparison.OrdinalIgnoreCase) ? Remote : Local;
}

/// <summary>
/// Normalized verdict vocabulary shared by aspects and build-tests rows. The
/// planes speak different dialects (<c>pass</c>/<c>block</c> locally,
/// <c>pass</c>/<c>fail</c> remotely); writers normalize onto these four so no
/// reader has to know which plane produced a row.
/// </summary>
public static class ReviewVerdicts
{
    public const string Pass = "pass";
    public const string Concerns = "concerns";
    public const string Block = "block";
    /// <summary>The plane recorded no verdict for this row.</summary>
    public const string Missing = "missing";

    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "pass" or "passed" or "ok" or "green" => Pass,
        "concerns" or "concern" or "warn" or "warning" => Concerns,
        "block" or "blocked" or "fail" or "failed" or "error" => Block,
        _ => Missing,
    };

    /// <summary>True when the verdict stops the round from being a clean pass.</summary>
    public static bool IsBlocking(string? value) => Normalize(value) == Block;
}

/// <summary>Collapsed build-tests result across one round or the whole card.</summary>
public static class ReviewBuildTestsResults
{
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string NotProven = "not-proven";
}

/// <summary>Where the delivery produced by a review round ended up.</summary>
public static class ReviewDeliveryStates
{
    public const string Integrated = "integrated";
    public const string GateFailed = "gate-failed";
    public const string NotAttempted = "not-attempted";
}

/// <summary>On-disk naming and versioning for <see cref="ReviewRoundRecord"/>.</summary>
public static class ReviewRoundRecordSchema
{
    public const int CurrentVersion = 1;

    /// <summary>Glob the store enumerates inside a job folder.</summary>
    public const string FilePattern = "review-round-*.json";

    /// <summary>
    /// Deterministic file name so a replayed report overwrites its own record
    /// instead of appending a phantom review round.
    /// </summary>
    public static string FileName(string plane, string attemptId) =>
        $"review-round-{ReviewPlanes.Normalize(plane)}-{SafePart(attemptId)}.json";

    private static string SafePart(string value) =>
        new(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray());
}
