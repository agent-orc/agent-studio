using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AgentStudio.Tasks;

/// <summary>
/// One option the parking run had already weighed when it stopped. Same field
/// names the decision-card work (Dossier AGT-W54, <c>docs/operations/decision-cards/</c>)
/// specifies for a decision card's options, so a park of type
/// <c>operator-decision</c> can be turned into a decision card by copying the
/// block rather than by translating between two formats.
/// </summary>
/// <param name="Id">Stable option id (<c>a</c>, <c>b</c>, <c>connector</c>).</param>
/// <param name="Label">The option in one line.</param>
/// <param name="Consequences">What choosing it costs or implies; null when the run did not say.</param>
/// <param name="Recommended">The run marked this option as its recommendation.</param>
public sealed record ParkedDecisionOption(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("consequences")] string? Consequences = null,
    [property: JsonPropertyName("recommended")] bool Recommended = false);

/// <summary>
/// The human half of a park: the question a person has to answer.
///
/// <para>AGT-2736 parked with <c>reason: "The remote agent requires operator
/// input: choose-connector-vs-lan-deployment-strategy"</c>. A slug is an
/// identifier, not a question - the operator could not act on it, and the three
/// options the agent had already worked out were only in a repository document
/// nothing linked. This record keeps the slug as <see cref="QuestionId"/> and
/// carries the question, the options, and the documents the run named.</para>
/// </summary>
public sealed record ParkedDecisionRequest
{
    /// <summary>The park slug, preserved as an identifier only.</summary>
    [JsonPropertyName("questionId")]
    public string QuestionId { get; init; } = "";

    /// <summary>One sentence a person can answer. Empty when the parking run
    /// supplied only a slug - which is a defect the surfaces must state rather
    /// than paper over with the slug.</summary>
    [JsonPropertyName("question")]
    public string Question { get; init; } = "";

    /// <summary>The options the run had already weighed; empty when it named none.</summary>
    [JsonPropertyName("options")]
    public IReadOnlyList<ParkedDecisionOption> Options { get; init; } = [];

    /// <summary>Repository-relative documents the run named as the place where
    /// the question is written up.</summary>
    [JsonPropertyName("documents")]
    public IReadOnlyList<string> Documents { get; init; } = [];

    /// <summary>The decision card this park was turned into, once decision cards
    /// exist. Null until then; the park itself stays the record of the question.</summary>
    [JsonPropertyName("decisionCardKey")]
    public string? DecisionCardKey { get; init; }

    /// <summary>True when <see cref="Question"/> came from the parking run
    /// itself rather than being absent.</summary>
    [JsonIgnore]
    public bool Stated => Question.Length > 0;
}

/// <summary>
/// Pure reader that lifts a <see cref="ParkedDecisionRequest"/> out of the two
/// things a parking run already leaves behind: the park reason (which carries
/// the slug) and the agent's final NeedsInput message (which carries the
/// question, the options, and any document the run pointed at).
///
/// <para>Nothing here reads a file or a clock, so the extraction rules are
/// tested as a matrix instead of through a park.</para>
/// </summary>
public static class ParkedDecisionReader
{
    /// <summary>Longest question rendered verbatim; longer text is cut at the
    /// last word boundary so the panel head stays one line.</summary>
    public const int MaximumQuestionLength = 240;

    /// <summary>Upper bound on parsed options and documents. A park is a
    /// decision, not a catalogue; an unbounded list is a parser error.</summary>
    public const int MaximumItems = 8;

    private static readonly Regex SlugCandidate = new(
        @"(?<slug>[a-z0-9]+(?:[-_][a-z0-9]+){1,})\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LabelledOption = new(
        @"^\s*(?:[-*+]|\d+[.)])\s*(?:\*\*)?\s*Option\s+(?<id>[A-Za-z0-9]{1,12})\s*(?:\*\*)?\s*[:.)–-]\s*(?<rest>\S.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LetteredOption = new(
        @"^\s*(?:[-*+]\s*)?(?<id>[A-Za-z]|\d{1,2})[.)]\s+(?<rest>\S.*)$",
        RegexOptions.Compiled);

    private static readonly Regex DocumentPath = new(
        @"(?<path>(?:[\w.-]+/)+[\w.-]+\.(?:md|html|htm|json|ya?ml))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Builds the decision request for a park.
    /// </summary>
    /// <param name="reason">The park reason, formatted or raw. Only the slug is taken from it.</param>
    /// <param name="message">The agent's final NeedsInput message, when the run left one.</param>
    public static ParkedDecisionRequest Read(string? reason, string? message)
    {
        var body = (message ?? string.Empty).Replace("\r\n", "\n").Trim();
        return new ParkedDecisionRequest
        {
            QuestionId = ReadQuestionId(reason),
            Question = ReadQuestion(body),
            Options = ReadOptions(body),
            Documents = ReadDocuments(body),
        };
    }

    /// <summary>
    /// The slug the run parked under, stripped of the <c>[category]</c> prefix
    /// and of any leading prose. Returns an empty string when the reason carries
    /// no slug-shaped token.
    /// </summary>
    public static string ReadQuestionId(string? reason)
    {
        var value = (reason ?? string.Empty).Trim();
        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            if (close > 0) value = value[(close + 1)..].Trim();
        }
        // "The remote agent requires operator input: choose-connector-vs-lan"
        var colon = value.LastIndexOf(':');
        if (colon >= 0 && colon < value.Length - 1) value = value[(colon + 1)..].Trim();
        value = value.Trim('.', ' ', '`');
        if (value.Length == 0 || value.Contains(' ')) return SlugTail(value);
        return SlugCandidate.IsMatch(value) ? value.ToLowerInvariant() : "";
    }

    /// <summary>
    /// The one-sentence question, taken from the first interrogative line of the
    /// message and otherwise from its first sentence. Empty when the run left no
    /// message at all - the surfaces then say the run stated no question instead
    /// of presenting the slug as one.
    /// </summary>
    public static string ReadQuestion(string? message)
    {
        var body = (message ?? string.Empty).Replace("\r\n", "\n").Trim();
        if (body.Length == 0) return "";

        string? first = null;
        foreach (var raw in body.Split('\n'))
        {
            var line = StripMarkdown(raw);
            if (line.Length == 0) continue;
            first ??= line;
            if (line.Contains('?')) return Bound(FirstSentence(line));
        }
        return first is null ? "" : Bound(FirstSentence(first));
    }

    /// <summary>
    /// The options the run listed, from an explicit <c>- Option A: …</c> list or
    /// from a plain lettered/numbered list. An unlabelled bullet list is NOT an
    /// option list: a park that invents options it never weighed is worse than a
    /// park with none.
    /// </summary>
    public static IReadOnlyList<ParkedDecisionOption> ReadOptions(string? message)
    {
        var body = (message ?? string.Empty).Replace("\r\n", "\n");
        if (body.Trim().Length == 0) return [];

        var labelled = new List<ParkedDecisionOption>();
        var lettered = new List<ParkedDecisionOption>();
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.TrimEnd();
            var match = LabelledOption.Match(line);
            if (match.Success)
            {
                labelled.Add(ToOption(match.Groups["id"].Value, match.Groups["rest"].Value));
                continue;
            }
            match = LetteredOption.Match(line);
            if (match.Success) lettered.Add(ToOption(match.Groups["id"].Value, match.Groups["rest"].Value));
        }

        var chosen = labelled.Count > 0 ? labelled : lettered.Count > 1 ? lettered : [];
        return chosen.Count > MaximumItems ? chosen.Take(MaximumItems).ToList() : chosen;
    }

    /// <summary>Repository-relative documents named anywhere in the message,
    /// de-duplicated in first-mention order.</summary>
    public static IReadOnlyList<string> ReadDocuments(string? message)
    {
        var body = message ?? string.Empty;
        if (body.Trim().Length == 0) return [];
        var seen = new List<string>();
        foreach (Match match in DocumentPath.Matches(body))
        {
            var path = match.Groups["path"].Value.Trim('`', '(', ')', '<', '>', '.', ',');
            if (path.Length == 0) continue;
            if (seen.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
            seen.Add(path);
            if (seen.Count == MaximumItems) break;
        }
        return seen;
    }

    private static ParkedDecisionOption ToOption(string id, string rest)
    {
        var text = StripMarkdown(rest);
        var label = FirstSentence(text);
        var consequences = text.Length > label.Length ? text[label.Length..].Trim() : "";
        return new ParkedDecisionOption(
            id.Trim().ToLowerInvariant(),
            Bound(label),
            consequences.Length == 0 ? null : Bound(consequences),
            text.Contains("recommend", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Drops list bullets, emphasis markers, and code fences from one line.</summary>
    private static string StripMarkdown(string line)
        => line.Trim()
            .TrimStart('>', ' ')
            .TrimStart('-', '*', '+', ' ')
            .Replace("**", "")
            .Replace("`", "")
            .Trim();

    /// <summary>The first sentence of a line, ending at <c>. ! ?</c> or the line end.</summary>
    private static string FirstSentence(string line)
    {
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] is not ('.' or '!' or '?')) continue;
            // Not a sentence end when it splits a number, an abbreviation, or a path.
            if (i + 1 < line.Length && line[i + 1] is not (' ' or '\t')) continue;
            return line[..(i + 1)].Trim();
        }
        return line.Trim();
    }

    private static string Bound(string value)
    {
        var text = value.Trim();
        if (text.Length <= MaximumQuestionLength) return text;
        var cut = text.LastIndexOf(' ', MaximumQuestionLength - 1);
        return (cut > 0 ? text[..cut] : text[..MaximumQuestionLength]).TrimEnd() + "…";
    }

    /// <summary>Last slug-shaped token of a prose reason, e.g. the trailing
    /// identifier in "parked on choose-connector-vs-lan".</summary>
    private static string SlugTail(string value)
    {
        var match = SlugCandidate.Match(value);
        return match.Success ? match.Groups["slug"].Value.ToLowerInvariant() : "";
    }
}
