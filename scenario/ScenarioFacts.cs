using System.Globalization;

namespace AgentStudio.Scenario;

/// <summary>
/// Type of an observed scenario fact. The kind participates in comparison, so a
/// textual "2" never satisfies a numeric expectation of 2.
/// </summary>
public enum ScenarioFactKind
{
    Text,
    Number,
    Boolean,
}

/// <summary>
/// One typed observation produced by a step action. Actions publish facts;
/// the scenario document asserts over them. Rendering is invariant so a report
/// is byte-identical on every host.
/// </summary>
public readonly record struct ScenarioFactValue
{
    private readonly string? _text;
    private readonly long _number;
    private readonly bool _boolean;

    private ScenarioFactValue(ScenarioFactKind kind, string? text, long number, bool boolean)
    {
        Kind = kind;
        _text = text;
        _number = number;
        _boolean = boolean;
    }

    public ScenarioFactKind Kind { get; }

    public static ScenarioFactValue Text(string value)
        => new(ScenarioFactKind.Text, value ?? string.Empty, 0, false);

    public static ScenarioFactValue Number(long value)
        => new(ScenarioFactKind.Number, null, value, false);

    public static ScenarioFactValue Boolean(bool value)
        => new(ScenarioFactKind.Boolean, null, 0, value);

    /// <summary>Numeric payload, or null when this fact is not a number.</summary>
    public long? AsNumber() => Kind == ScenarioFactKind.Number ? _number : null;

    public string Render() => Kind switch
    {
        ScenarioFactKind.Number => _number.ToString(CultureInfo.InvariantCulture),
        ScenarioFactKind.Boolean => _boolean ? "true" : "false",
        _ => _text ?? string.Empty,
    };

    public override string ToString() => $"{Kind.ToString().ToLowerInvariant()}:{Render()}";
}

/// <summary>
/// Typed assertion set for one step. <see cref="Exact"/> demands the same kind
/// and the same value; <see cref="AtLeast"/> demands a numeric fact that is not
/// below the floor, for counts whose exact value is not part of the contract.
/// </summary>
public sealed record ScenarioExpectation(
    IReadOnlyDictionary<string, ScenarioFactValue> Exact,
    IReadOnlyDictionary<string, long> AtLeast)
{
    public static ScenarioExpectation Empty { get; } = new(
        new Dictionary<string, ScenarioFactValue>(StringComparer.Ordinal),
        new Dictionary<string, long>(StringComparer.Ordinal));

    public int Count => Exact.Count + AtLeast.Count;
}

/// <summary>Why a single assertion passed or failed. Reasons are stable ids.</summary>
public static class ScenarioAssertionReasons
{
    public const string Satisfied = "satisfied";
    public const string FactMissing = "fact-missing";
    public const string KindMismatch = "kind-mismatch";
    public const string ValueMismatch = "value-mismatch";
    public const string BelowFloor = "below-floor";
}

public sealed record ScenarioAssertionOutcome(
    string Fact,
    string Expected,
    string Actual,
    bool Satisfied,
    string Reason);

/// <summary>
/// Pure comparison of a step's declared expectation against the facts the
/// action observed. No clock, no IO, no ordering surprises: results follow the
/// document order of the expectation keys, sorted for a stable report.
/// </summary>
public static class ScenarioAssertions
{
    public static IReadOnlyList<ScenarioAssertionOutcome> Evaluate(
        ScenarioExpectation expectation,
        IReadOnlyDictionary<string, ScenarioFactValue> observed)
    {
        var outcomes = new List<ScenarioAssertionOutcome>(expectation.Count);
        foreach (var fact in expectation.Exact.Keys.OrderBy(key => key, StringComparer.Ordinal))
            outcomes.Add(EvaluateExact(fact, expectation.Exact[fact], observed));
        foreach (var fact in expectation.AtLeast.Keys.OrderBy(key => key, StringComparer.Ordinal))
            outcomes.Add(EvaluateAtLeast(fact, expectation.AtLeast[fact], observed));
        return outcomes;
    }

    private static ScenarioAssertionOutcome EvaluateExact(
        string fact,
        ScenarioFactValue expected,
        IReadOnlyDictionary<string, ScenarioFactValue> observed)
    {
        if (!observed.TryGetValue(fact, out var actual))
            return new ScenarioAssertionOutcome(
                fact, expected.ToString(), "<missing>", false, ScenarioAssertionReasons.FactMissing);
        if (actual.Kind != expected.Kind)
            return new ScenarioAssertionOutcome(
                fact, expected.ToString(), actual.ToString(), false, ScenarioAssertionReasons.KindMismatch);
        var satisfied = string.Equals(actual.Render(), expected.Render(), StringComparison.Ordinal);
        return new ScenarioAssertionOutcome(
            fact,
            expected.ToString(),
            actual.ToString(),
            satisfied,
            satisfied ? ScenarioAssertionReasons.Satisfied : ScenarioAssertionReasons.ValueMismatch);
    }

    private static ScenarioAssertionOutcome EvaluateAtLeast(
        string fact,
        long floor,
        IReadOnlyDictionary<string, ScenarioFactValue> observed)
    {
        var expected = $">= {floor.ToString(CultureInfo.InvariantCulture)}";
        if (!observed.TryGetValue(fact, out var actual))
            return new ScenarioAssertionOutcome(
                fact, expected, "<missing>", false, ScenarioAssertionReasons.FactMissing);
        if (actual.AsNumber() is not { } number)
            return new ScenarioAssertionOutcome(
                fact, expected, actual.ToString(), false, ScenarioAssertionReasons.KindMismatch);
        return number >= floor
            ? new ScenarioAssertionOutcome(
                fact, expected, actual.ToString(), true, ScenarioAssertionReasons.Satisfied)
            : new ScenarioAssertionOutcome(
                fact, expected, actual.ToString(), false, ScenarioAssertionReasons.BelowFloor);
    }
}
