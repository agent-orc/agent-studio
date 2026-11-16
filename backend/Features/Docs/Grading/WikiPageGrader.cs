using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.Docs;

/// <summary>
/// Grades one wiki page. The seam that lets the run loop stay identical whether
/// the verdict comes from a strong model on the one-shot CLI rail
/// (<see cref="CliWikiPageGrader"/>, production) or a deterministic offline
/// fallback (<see cref="HeuristicWikiPageGrader"/>, used for probes and tests).
/// </summary>
public interface IWikiPageGrader
{
    /// <summary>Grade one page. Never throws on grader-level failure - those land
    /// as an <c>Ok=false</c> verdict so the run records an honest failure.</summary>
    Task<WikiPageGradeVerdict> GradeAsync(
        WikiPageGradeInput input, WikiGradingRunRequest run, CancellationToken ct);
}

/// <summary>
/// Shared JSON-verdict parsing so the CLI grader and any future grader agree on
/// the wire shape the model is asked to return.
/// </summary>
public static class WikiGradeVerdictParser
{
    /// <summary>
    /// Parse a grader reply into a verdict. Tolerant of prose around the JSON
    /// (```json fences, a leading sentence): it extracts the first balanced JSON
    /// object and reads the known fields. Returns null when no JSON object is
    /// present or the JSON is malformed, so the caller can record a failure.
    /// </summary>
    public static WikiPageGradeVerdict? TryParse(string? rawText)
    {
        var json = JsonReplyExtractor.ExtractJsonObject(rawText);
        if (json == null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var grade = NormalizeGrade(GetString(root, "grade"));
            var assessment = GetString(root, "assessment")
                ?? GetString(root, "summary")
                ?? "No assessment text was returned.";
            var notes = GetStringArray(root, "notes");

            return new WikiPageGradeVerdict(
                Grade: grade,
                Assessment: assessment.Trim(),
                Outdated: GetBool(root, "outdated"),
                Contradictory: GetBool(root, "contradictory"),
                Gaps: GetBool(root, "gaps"),
                Notes: notes,
                Ok: true,
                Error: null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Coerces a free-form grade token to one of A/B/C/D/unknown.</summary>
    public static string NormalizeGrade(string? value)
    {
        var clean = value?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(clean)) return "unknown";
        var first = clean[..1];
        return first is "A" or "B" or "C" or "D" ? first : "unknown";
    }

    private static string? GetString(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? GetBool(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static IReadOnlyList<string> GetStringArray(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.Array) return [];
        return v.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Take(10)
            .ToList();
    }
}

/// <summary>
/// Builds the grading prompt shared by graders so the wire contract lives in one
/// place. Kept static + pure for testing.
/// </summary>
public static class WikiGradingPrompt
{
    /// <summary>Assemble the per-page grading prompt. The page content is capped
    /// so a very large page still fits comfortably in one turn.</summary>
    public static string Build(WikiPageGradeInput input)
    {
        const int maxChars = 24_000;
        var content = input.Content.Length > maxChars
            ? input.Content[..maxChars] + "\n\n[... page truncated for grading ...]"
            : input.Content;

        var sb = new StringBuilder();
        sb.Append("You are a documentation maintenance reviewer grading one wiki page.\n\n");
        sb.Append("Project: ").Append(input.ProjectName).Append('\n');
        sb.Append("Page path: ").Append(input.RelPath).Append('\n');
        sb.Append("Page title: ").Append(input.Title).Append("\n\n");
        sb.Append("Grade how healthy this page is as living knowledge. Judge whether it is:\n");
        sb.Append("- outdated (describes behaviour or structure that has moved on),\n");
        sb.Append("- contradictory (contradicts itself or well-known project facts),\n");
        sb.Append("- has gaps (materially incomplete for its stated purpose).\n\n");
        sb.Append("Return STRICT JSON only, no prose, in exactly this shape:\n");
        sb.Append("{\n");
        sb.Append("  \"grade\": \"A|B|C|D\",            // A excellent, B solid, C weak, D poor\n");
        sb.Append("  \"assessment\": \"one paragraph, <= 60 words\",\n");
        sb.Append("  \"outdated\": true|false,\n");
        sb.Append("  \"contradictory\": true|false,\n");
        sb.Append("  \"gaps\": true|false,\n");
        sb.Append("  \"notes\": [\"short evidence line\", \"...\"]\n");
        sb.Append("}\n\n");
        sb.Append("--- BEGIN PAGE CONTENT ---\n");
        sb.Append(content);
        sb.Append("\n--- END PAGE CONTENT ---\n");
        return sb.ToString();
    }
}

/// <summary>
/// Production grader: runs the grading prompt through the shared one-shot CLI
/// rail (<see cref="CliOneShotRegistry"/>), tagged as wiki-grading usage so spend
/// is recorded and visible like any other rail traffic. Mirrors the drift
/// post-step's use of the rail; the reply is parsed by
/// <see cref="WikiGradeVerdictParser"/>.
/// </summary>
public sealed class CliWikiPageGrader : IWikiPageGrader
{
    private static readonly TimeSpan PerPageTimeout = TimeSpan.FromMinutes(3);

    private readonly CliOneShotRegistry _registry;
    private readonly ILogger<CliWikiPageGrader> _logger;

    public CliWikiPageGrader(CliOneShotRegistry registry, ILogger<CliWikiPageGrader> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    public async Task<WikiPageGradeVerdict> GradeAsync(
        WikiPageGradeInput input, WikiGradingRunRequest run, CancellationToken ct)
    {
        var cli = string.IsNullOrWhiteSpace(run.CliType) ? "claude" : run.CliType.Trim();
        var oneShot = _registry.Get(cli);
        if (oneShot == null)
            return WikiPageGradeVerdict.Fail($"No CLI one-shot implementation registered for '{cli}'.");

        var prompt = WikiGradingPrompt.Build(input);
        CliOneShotResult result;
        try
        {
            result = await oneShot.RunAsync(new CliOneShotRequest(
                CliType: cli,
                Model: run.Model,
                Prompt: prompt)
            {
                ThinkingLevel = run.ThinkingLevel,
                Timeout = PerPageTimeout,
                Source = AdHocUsageSources.WikiGrading,
                RecordUsage = true,
                Project = input.ProjectName,
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Wiki grading one-shot threw for {RelPath}", input.RelPath);
            return WikiPageGradeVerdict.Fail($"CLI call threw: {ex.Message}");
        }

        if (!result.Ok)
        {
            _logger.LogWarning(
                "Wiki grading one-shot failed for {RelPath}: exit={Exit} error={Error}",
                input.RelPath, result.ExitCode, result.Error);
            return WikiPageGradeVerdict.Fail(result.Error ?? $"CLI exited {result.ExitCode}.");
        }

        var verdict = WikiGradeVerdictParser.TryParse(result.ParsedText);
        return verdict ?? WikiPageGradeVerdict.Fail("Model reply did not contain a parseable JSON verdict.");
    }
}

/// <summary>
/// Deterministic offline grader. Produces a plausible, reproducible grade from
/// cheap textual signals (age markers, TODO/placeholder density, contradiction
/// keywords, thinness, deprecation markers) so an end-to-end probe or unit test
/// can exercise the full enumerate -&gt; grade -&gt; write-companion -&gt; Pulse
/// path without spending model tokens or needing a configured CLI. NOT a
/// substitute for the model grade in production - the run wires
/// <see cref="CliWikiPageGrader"/> there.
/// </summary>
public sealed class HeuristicWikiPageGrader : IWikiPageGrader
{
    private static readonly Regex TodoMarker =
        new(@"\b(TODO|TBD|FIXME|WIP|placeholder|coming soon)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OutdatedMarker =
        new(@"\b(deprecated|obsolete|legacy|no longer|removed|superseded|outdated)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ContradictionMarker =
        new(@"\b(however|but note|contradict|inconsistent|conflicts? with|used to)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public Task<WikiPageGradeVerdict> GradeAsync(
        WikiPageGradeInput input, WikiGradingRunRequest run, CancellationToken ct)
    {
        var text = input.Content ?? string.Empty;
        var wordCount = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

        var todo = TodoMarker.Matches(text).Count;
        var outdated = OutdatedMarker.IsMatch(text);
        var contradictory = ContradictionMarker.Matches(text).Count >= 3;
        var thin = wordCount < 60;
        var gaps = thin || todo >= 2;

        var notes = new List<string>();
        if (outdated) notes.Add("Contains deprecation / obsolescence markers.");
        if (todo > 0) notes.Add($"{todo} placeholder / TODO marker(s) present.");
        if (thin) notes.Add($"Thin page ({wordCount} words).");
        if (contradictory) notes.Add("Multiple hedging / contradiction cues.");
        if (notes.Count == 0) notes.Add("No obvious staleness, gaps, or contradictions detected.");

        // Severity score: higher is worse. Band into A/B/C/D so the fallback
        // spreads a real repo across grades instead of collapsing to one band.
        var score = (outdated ? 2 : 0) + (contradictory ? 2 : 0) + (thin ? 2 : 0) + Math.Min(todo, 3);
        var grade = score >= 4 ? "D" : score >= 2 ? "C" : score >= 1 ? "B" : "A";

        var assessment = grade switch
        {
            "D" => "Multiple maintenance signals (staleness, gaps, or contradictions) suggest this page needs a rewrite.",
            "C" => "Some maintenance signals detected; the page likely needs a refresh.",
            "B" => "Broadly healthy with a minor maintenance signal.",
            _ => "No maintenance signals detected by the deterministic pass.",
        };

        return Task.FromResult(new WikiPageGradeVerdict(
            Grade: grade,
            Assessment: assessment,
            Outdated: outdated,
            Contradictory: contradictory,
            Gaps: gaps,
            Notes: notes,
            Ok: true,
            Error: null));
    }
}
