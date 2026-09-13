using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace AgentStudio.Shared;

/// <summary>
/// Lifecycle of a decision card (AGT-2795). Orthogonal to the lane state: a
/// requested decision waits in <c>1-preparation</c> with the decision badge, a
/// decided one becomes a durable record, and a reopened one re-blocks its
/// dependants until a new choice is made. Persisted as
/// <c>decision.status</c> in <c>task.json</c>; keep values stable.
/// </summary>
public static class DecisionStatuses
{
    /// <summary>Awaiting the decider's choice (the "active" state).</summary>
    public const string Requested = "requested";
    /// <summary>A choice was recorded; the card is a durable decision record.</summary>
    public const string Decided = "decided";
    /// <summary>The decider reopened the decision; dependants are blocked again.</summary>
    public const string Reopened = "reopened";

    public static readonly string[] All = [Requested, Decided, Reopened];

    /// <summary>Coerce a free-form value to a known status; unknown / empty -> Requested.</summary>
    public static string Normalize(string? value)
    {
        var v = value?.Trim().ToLowerInvariant();
        return v switch
        {
            Decided => Decided,
            Reopened => Reopened,
            _ => Requested,
        };
    }

    /// <summary>True when the decision is still open (requested or reopened).</summary>
    public static bool IsOpen(string? value) => Normalize(value) is Requested or Reopened;
}

/// <summary>
/// One choosable option on a decision card. The consequences, effort, and risks
/// are structured fields (not free markdown) so board and detail can render them
/// without parsing prose. <see cref="Id"/> is a stable, caller-supplied slug
/// referenced by the recommendation and by the recorded choice.
/// </summary>
public sealed record DecisionOption
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string? Consequences { get; init; }
    public string? Effort { get; init; }
    public string? Risks { get; init; }
    /// <summary>
    /// Optional implementation requirements for this option. When the decision
    /// is decided and no implementation card already depends on it, the chosen
    /// option's requirements seed a new 2-ready coding card (AGT-2795 req 5).
    /// </summary>
    public string? Requirements { get; init; }
}

/// <summary>
/// Structured content of a <see cref="TaskKinds.Decision"/> card. Persisted as
/// the <c>"decision"</c> object in <c>task.json</c>. The prose question stays
/// possible, but the options are fields, and the recorded choice, rationale,
/// decider, and timestamp make the settled card a durable, linkable record.
/// </summary>
public sealed record DecisionContent
{
    /// <summary>The question the decider must answer.</summary>
    public string Question { get; init; } = "";

    /// <summary>Two to four options, each with structured consequences/effort/risks.</summary>
    public List<DecisionOption> Options { get; init; } = [];

    /// <summary>Optional id of the recommended option.</summary>
    public string? RecommendedOptionId { get; init; }

    /// <summary>Why the recommendation is made (paired with <see cref="RecommendedOptionId"/>).</summary>
    public string? RecommendationReason { get; init; }

    /// <summary>
    /// Who must decide: a registered client identity id, or the role
    /// <see cref="DecisionDeciders.Operator"/> when any operator may decide.
    /// </summary>
    public string Decider { get; init; } = DecisionDeciders.Operator;

    /// <summary>Optional due date for the decision.</summary>
    public DateTime? DueDate { get; init; }

    /// <summary>Stable keys of the cards blocked by this decision (its dependants).</summary>
    public List<string> BlockedCards { get; init; } = [];

    /// <summary>Lifecycle status; see <see cref="DecisionStatuses"/>.</summary>
    public string Status { get; init; } = DecisionStatuses.Requested;

    /// <summary>The chosen option id once decided; null while open.</summary>
    public string? ChosenOptionId { get; init; }

    /// <summary>The decider's rationale for the recorded choice.</summary>
    public string? Rationale { get; init; }

    /// <summary>Client id / role that recorded the choice.</summary>
    public string? DecidedBy { get; init; }

    /// <summary>UTC instant the choice was recorded.</summary>
    public DateTime? DecidedAt { get; init; }

    /// <summary>Reason the decision was reopened, when applicable.</summary>
    public string? ReopenNote { get; init; }

    /// <summary>
    /// Task-relative path of the ADR-style decision record written on decide
    /// (e.g. <c>decision-record.md</c>); null while open. Linkable from the
    /// implementation cards this decision unblocks.
    /// </summary>
    public string? RecordPath { get; init; }
}

/// <summary>Well-known decider roles for <see cref="DecisionContent.Decider"/>.</summary>
public static class DecisionDeciders
{
    /// <summary>Any operator may decide (not a specific client identity).</summary>
    public const string Operator = "operator";
}

/// <summary>Failure category for a rejected decision-card operation.</summary>
public enum DecisionCardErrorCode
{
    /// <summary>The question is missing.</summary>
    MissingQuestion,
    /// <summary>Fewer than two or more than four options were supplied.</summary>
    OptionCount,
    /// <summary>An option is missing its id or label.</summary>
    OptionShape,
    /// <summary>Two options share the same id.</summary>
    DuplicateOptionId,
    /// <summary>The recommendation names an option id that does not exist.</summary>
    UnknownRecommendation,
    /// <summary>The choice names an option id that does not exist.</summary>
    UnknownOption,
    /// <summary>A choice was recorded without a rationale.</summary>
    MissingRationale,
    /// <summary>The card is not currently open for a decision.</summary>
    NotOpen,
    /// <summary>The card is not currently decided (reopen requires a settled decision).</summary>
    NotDecided,
}

/// <summary>One reason a decision-card operation was rejected.</summary>
public sealed record DecisionCardError(DecisionCardErrorCode Code, string Message);

/// <summary>
/// Pure, dependency-free policy for decision cards (AGT-2795). Lives in the
/// Shared library so the validation and transition matrix is unit-testable
/// without the web host, per the .NET backend style guide (pure decision first,
/// bounded side effects in the service).
/// </summary>
public static class DecisionCardPolicy
{
    public const int MinOptions = 2;
    public const int MaxOptions = 4;

    private static readonly StringComparer IdComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Validates the structured content of a decision request. Enforces the
    /// AGT-2795 shape: a question, two to four options with unique ids and
    /// non-empty labels, and a recommendation (if any) that names a real option.
    /// </summary>
    public static IReadOnlyList<DecisionCardError> ValidateContent(DecisionContent? content)
    {
        var errors = new List<DecisionCardError>();
        if (content == null)
        {
            errors.Add(new(DecisionCardErrorCode.MissingQuestion, "A decision card needs a question and options."));
            return errors;
        }

        if (string.IsNullOrWhiteSpace(content.Question))
            errors.Add(new(DecisionCardErrorCode.MissingQuestion, "A decision card needs a question."));

        var options = content.Options ?? [];
        if (options.Count < MinOptions || options.Count > MaxOptions)
            errors.Add(new(DecisionCardErrorCode.OptionCount,
                $"A decision card needs between {MinOptions} and {MaxOptions} options."));

        var seen = new HashSet<string>(IdComparer);
        foreach (var option in options)
        {
            if (string.IsNullOrWhiteSpace(option.Id) || string.IsNullOrWhiteSpace(option.Label))
            {
                errors.Add(new(DecisionCardErrorCode.OptionShape, "Every option needs an id and a label."));
                continue;
            }
            if (!seen.Add(option.Id.Trim()))
                errors.Add(new(DecisionCardErrorCode.DuplicateOptionId,
                    $"Option id '{option.Id.Trim()}' is used more than once."));
        }

        if (!string.IsNullOrWhiteSpace(content.RecommendedOptionId)
            && !options.Any(o => IdComparer.Equals(o.Id?.Trim(), content.RecommendedOptionId!.Trim())))
            errors.Add(new(DecisionCardErrorCode.UnknownRecommendation,
                $"The recommended option '{content.RecommendedOptionId!.Trim()}' is not one of the options."));

        return errors;
    }

    /// <summary>
    /// Validates a proposed choice against the current content. The card must be
    /// open, the option must exist, and a non-empty rationale is required.
    /// </summary>
    public static IReadOnlyList<DecisionCardError> ValidateChoice(
        DecisionContent content, string? optionId, string? rationale)
    {
        var errors = new List<DecisionCardError>();
        if (!DecisionStatuses.IsOpen(content.Status))
            errors.Add(new(DecisionCardErrorCode.NotOpen, "This decision has already been decided."));
        if (string.IsNullOrWhiteSpace(optionId)
            || !(content.Options ?? []).Any(o => IdComparer.Equals(o.Id?.Trim(), optionId!.Trim())))
            errors.Add(new(DecisionCardErrorCode.UnknownOption, "Choose one of the listed options."));
        if (string.IsNullOrWhiteSpace(rationale))
            errors.Add(new(DecisionCardErrorCode.MissingRationale, "A decision needs a one-line rationale."));
        return errors;
    }

    /// <summary>Returns settled content that records the chosen option, rationale, decider, and timestamp.</summary>
    public static DecisionContent Decide(
        DecisionContent content, string optionId, string rationale, string decidedBy, DateTime nowUtc,
        string? recordPath = null) => content with
    {
        Status = DecisionStatuses.Decided,
        ChosenOptionId = optionId.Trim(),
        Rationale = rationale.Trim(),
        DecidedBy = string.IsNullOrWhiteSpace(decidedBy) ? DecisionDeciders.Operator : decidedBy.Trim(),
        DecidedAt = nowUtc,
        ReopenNote = null,
        RecordPath = recordPath ?? content.RecordPath,
    };

    /// <summary>Validates a reopen request: the card must currently be decided.</summary>
    public static IReadOnlyList<DecisionCardError> ValidateReopen(DecisionContent content)
    {
        var errors = new List<DecisionCardError>();
        if (!string.Equals(DecisionStatuses.Normalize(content.Status), DecisionStatuses.Decided, StringComparison.Ordinal))
            errors.Add(new(DecisionCardErrorCode.NotDecided, "Only a decided card can be reopened."));
        return errors;
    }

    /// <summary>Returns reopened content that clears the recorded choice and re-blocks dependants.</summary>
    public static DecisionContent Reopen(DecisionContent content, string? note) => content with
    {
        Status = DecisionStatuses.Reopened,
        ChosenOptionId = null,
        Rationale = null,
        DecidedBy = null,
        DecidedAt = null,
        RecordPath = null,
        ReopenNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
    };

    /// <summary>The option the recorded choice points at, or null when open / unknown.</summary>
    public static DecisionOption? ChosenOption(DecisionContent content) =>
        string.IsNullOrWhiteSpace(content.ChosenOptionId)
            ? null
            : (content.Options ?? []).FirstOrDefault(o => IdComparer.Equals(o.Id?.Trim(), content.ChosenOptionId!.Trim()));
}
