using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>
/// Wire shape for one structured aspect finding written into a timeline
/// event's <c>details["findings"]</c> bag (a JSON array string). The
/// frontend's central aspect-findings list renders these as a list of
/// toned verdict chips instead of the legacy preformatted
/// <c>**{aspect}** [{verdict}]: {reason}</c> blob. <c>Verdict</c> is the
/// normalised status token (<c>pass|concerns|block</c>) so the FE can map
/// it straight onto a tone without re-parsing markdown.
/// </summary>
/// <param name="Aspect">Aspect identifier, e.g. <c>code-quality</c>.</param>
/// <param name="Verdict">Normalised status token: <c>pass|concerns|block</c>.</param>
/// <param name="Reason">One-line summary the aspect produced.</param>
public sealed record AspectFinding(string Aspect, string Verdict, string Reason);

/// <summary>
/// Outcome of a single aspect-runner pass against a 4-auto-review job.
/// The orchestrator aggregates these across all configured aspects to
/// decide reissue / accept-as-done / accept-with-concerns.
/// </summary>
public enum AspectStatus
{
    /// <summary>The aspect found no issues. No tag is hung.</summary>
    Pass,

    /// <summary>
    /// The aspect found something worth flagging but not severe enough
    /// to send the task back to 3-progress. The tag <c>{namespace}:concerns</c>
    /// is added to the job; the user sees a small ⚠ chip on the card.
    /// </summary>
    Concerns,

    /// <summary>
    /// The aspect found a regression or missing piece that must be
    /// addressed before the task can be considered complete. Any single
    /// block triggers a reissue back to 3-progress with a follow-up
    /// summarising the per-aspect findings.
    /// </summary>
    Block
}

/// <summary>
/// One aspect runner's verdict for one job, as parsed from the fast-model
/// response and written to <c>aspect-{name}.md</c> in the job folder.
/// </summary>
/// <param name="Aspect">Aspect identifier, e.g. <c>code-quality</c>.</param>
/// <param name="Status">Pass / Concerns / Block.</param>
/// <param name="Summary">One-line summary used as the body's first line.</param>
/// <param name="Body">Full body markdown (without frontmatter).</param>
/// <param name="ConcernTagId">Tag id to hang on the job for Concerns / Block,
/// e.g. <c>quality:concerns</c>. Null on Pass.</param>
public sealed record AspectVerdict(
    string Aspect,
    AspectStatus Status,
    string Summary,
    string Body,
    string? ConcernTagId)
{
    /// <summary>
    /// True when this verdict is a POST-STEP INFRA failure - the aspect's
    /// reviewing CLI call died / returned nothing, so there is no real model
    /// opinion here - even after the single environmental retry (AGT-2021). Such
    /// a verdict is an environmental infra crash, NEVER the card's unfinished
    /// work: the orchestrator records it as an <c>InfraCrash</c> flagged
    /// <c>environmental</c> and does not burn the reissue budget. Default false;
    /// added as an init-only property so every positional construction stays
    /// source-compatible.
    /// </summary>
    public bool IsInfraFailure { get; init; }
}

/// <summary>
/// Structured, machine-readable source of truth for one aspect verdict,
/// written as <c>aspect-{id}.json</c> next to the human-readable
/// <c>aspect-{id}.md</c>. This is the "one JSON source, two renderings"
/// contract from <c>docs/concepts/result-view-and-case-templates.md</c> §5:
/// the Files tab renders it structurally (meta header + status badge +
/// collapsible details) instead of dumping frontmatter-laden markdown, and
/// the same payload can feed the Result view's metric head.
///
/// <para>
/// The <c>.md</c> twin is still written unchanged so every existing reader
/// (<see cref="AgentStudio.Pipeline.AspectConcernReader"/>, the orchestrator's
/// tag routing, legacy Files-tab rendering) keeps working with zero change -
/// the JSON is strictly additive. <c>metrics</c> is an open, forward-compat
/// map (empty today) reserved for files-changed / tests-passed once those
/// counts are plumbed to the aspect writer.
/// </para>
/// </summary>
/// <param name="SchemaVersion">Wire-format version; bump on breaking shape changes.</param>
/// <param name="Aspect">Aspect identifier, e.g. <c>code-quality</c>.</param>
/// <param name="Status">Normalised status token: <c>pass|concerns|block</c>.</param>
/// <param name="Summary">One-line summary the aspect produced.</param>
/// <param name="Details">The model's narrative reply (freetext / light markdown).</param>
/// <param name="CreatedAt">UTC write time (round-trip "O" format on the wire).</param>
/// <param name="Model">Model id that produced the verdict, when known.</param>
/// <param name="Tag">Concern tag id (<c>{namespace}:concerns</c>) or null on pass.</param>
/// <param name="Metrics">Optional extensible metric map; omitted when empty.</param>
public sealed record AspectDocument(
    int SchemaVersion,
    string Aspect,
    string Status,
    string Summary,
    string Details,
    DateTime CreatedAt,
    string? Model,
    string? Tag,
    IReadOnlyDictionary<string, string>? Metrics);

/// <summary>
/// Pure helpers for the aspect-runner pipeline: parsing the fast-model
/// reply for an <c>[[ASPECT_VERDICT]]</c> sentinel, rendering the
/// per-aspect markdown report (frontmatter + body), rendering the
/// structured JSON twin, and parsing either back when tests / future
/// reviewers want to read the same files.
/// </summary>
public static class AspectVerdictParsing
{
    // Tolerant fallback: a model that drops the sentinel sometimes still
    // says "Status: concerns" on its own line. We accept that as a
    // legitimate verdict (no summary captured — caller fills the gap)
    // so the operator gets a real chip instead of "no parseable verdict".
    // Tolerant: accept optional **bold** around either "Status", the
    // separator, or the whole "Status:" prefix (the markdown a model
    // often emits in narrative replies). The status token itself may
    // be quoted, backtick'd, or followed by a punctuation mark.
    private static readonly Regex LineStatusRegex = new(
        @"^\s*\**\s*status\s*\**\s*[:=]\s*\**\s*[`'""]?(?<status>pass|concerns?|block(?:ed)?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>
    /// Parse a fast-model reply for the last <c>[[ASPECT_VERDICT]]</c>
    /// sentinel. The grammar is
    /// <c>[[ASPECT_VERDICT: status=&lt;pass|concerns|block&gt;; summary=&lt;short&gt;]]</c>.
    /// Returns null when no parseable verdict can be recovered (not even
    /// via the tolerant fallback) so the caller can fall back to a
    /// deterministic Concerns verdict — the agent must not be silently
    /// waved through.
    /// </summary>
    public static (AspectStatus Status, string Summary)? ParseVerdict(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var cleaned = StripWrappers(output);

        // Preferred path: the canonical [[ASPECT_VERDICT: ...]] sentinel.
        var marker = AspectVerdictMarkerParser.ParseLast(cleaned);
        if (marker != null)
        {
            var statusRaw = marker.Status.Trim().ToLowerInvariant();
            var summary = marker.Summary.Trim();
            var status = TokenToStatus(statusRaw);
            if (status != null) return (status.Value, summary);
        }

        // Tolerant fallback: scan for a "Status: <pass|concerns|block>"
        // line. Use the LAST one in the reply so trailing summary
        // statements win over earlier mentions. Build a summary from the
        // last non-empty trailing paragraph (capped at 200 chars).
        var fallback = LineStatusRegex.Matches(cleaned);
        if (fallback.Count > 0)
        {
            var token = fallback[^1].Groups["status"].Value;
            var status = TokenToStatus(token.ToLowerInvariant());
            if (status != null)
            {
                var summary = ExtractFallbackSummary(cleaned);
                return (status.Value, summary);
            }
        }

        return null;
    }

    private static AspectStatus? TokenToStatus(string? token) => token switch
    {
        "pass" => AspectStatus.Pass,
        "concerns" or "concern" => AspectStatus.Concerns,
        "block" or "blocked" => AspectStatus.Block,
        _ => null,
    };

    /// <summary>
    /// Strip common wrapper shapes the model adds around the sentinel:
    /// outer triple-backtick fences, leading "&lt;" XML-comment-style
    /// quotation, and Markdown blockquotes. Cheap pre-pass so the regex
    /// finds the sentinel even when the model wraps its reply.
    /// </summary>
    private static string StripWrappers(string raw)
    {
        // Drop fenced code blocks but keep their bodies — sentinels often
        // land inside an inline ```...``` the model added for emphasis.
        var withoutFences = Regex.Replace(raw, "```[a-zA-Z0-9_-]*\\r?\\n?", string.Empty);
        withoutFences = Regex.Replace(withoutFences, "```", string.Empty);
        return withoutFences;
    }

    private static string ExtractFallbackSummary(string cleaned)
    {
        // Look for a "Summary: ..." or "summary=..." line; otherwise grab
        // the last non-trivial paragraph and cap at 200 chars.
        var explicitSummary = Regex.Match(
            cleaned,
            @"^\s*(?:\*\*)?summary(?:\*\*)?\s*[:=]\s*(?<text>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        if (explicitSummary.Success)
        {
            var text = explicitSummary.Groups["text"].Value.Trim().Trim('"', '\'');
            return Cap(text, 200);
        }
        var paragraphs = cleaned.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("#") && !l.StartsWith("---"))
            .ToList();
        if (paragraphs.Count == 0) return string.Empty;
        return Cap(paragraphs[^1], 200);
    }

    private static string Cap(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max - 1).TrimEnd() + "…";

    /// <summary>
    /// Render the per-aspect report markdown with a small structured
    /// frontmatter so tests, future readers, and a possible "review the
    /// review" pass can parse it without re-running the model. The body
    /// is the model's narrative reply with the verdict sentinel left
    /// intact so the chain of evidence is auditable.
    /// </summary>
    public static string RenderReport(AspectVerdict verdict, DateTime now)
    {
        return
            "---\n" +
            $"aspect: {verdict.Aspect}\n" +
            $"status: {StatusToken(verdict.Status)}\n" +
            $"summary: {EscapeYaml(verdict.Summary)}\n" +
            $"created_at: {now:O}\n" +
            (verdict.ConcernTagId is null ? string.Empty : $"tag: {verdict.ConcernTagId}\n") +
            "---\n\n" +
            $"# Aspect: {verdict.Aspect}\n\n" +
            $"**Status:** {StatusToken(verdict.Status)}\n\n" +
            (string.IsNullOrWhiteSpace(verdict.Summary) ? string.Empty : $"**Summary:** {verdict.Summary}\n\n") +
            verdict.Body;
    }

    public static string StatusToken(AspectStatus status) => status switch
    {
        AspectStatus.Pass => "pass",
        AspectStatus.Concerns => "concerns",
        AspectStatus.Block => "block",
        _ => "pass"
    };

    /// <summary>Current wire-format version for <see cref="AspectDocument"/>.</summary>
    public const int AspectDocumentSchemaVersion = 1;

    private static readonly JsonSerializerOptions AspectJsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>
    /// Render the structured <c>aspect-{id}.json</c> source-of-truth twin.
    /// The <paramref name="rawReply"/> (the model's narrative) becomes
    /// <c>details</c>; when it is blank the summary is reused so the
    /// document never carries an empty body. Empty <paramref name="metrics"/>
    /// is dropped from the wire (the field is forward-compat, not required).
    /// A trailing newline keeps the file tidy in a text editor / git diff.
    /// </summary>
    public static string RenderJson(
        AspectVerdict verdict,
        string? model,
        DateTime now,
        IReadOnlyDictionary<string, string>? metrics = null)
    {
        var details = string.IsNullOrWhiteSpace(verdict.Body)
            ? verdict.Summary
            : verdict.Body.Trim();
        var doc = new AspectDocument(
            SchemaVersion: AspectDocumentSchemaVersion,
            Aspect: verdict.Aspect,
            Status: StatusToken(verdict.Status),
            Summary: verdict.Summary ?? string.Empty,
            Details: details,
            CreatedAt: now,
            Model: string.IsNullOrWhiteSpace(model) ? null : model,
            Tag: verdict.ConcernTagId,
            Metrics: metrics is { Count: > 0 } ? metrics : null);
        return JsonSerializer.Serialize(doc, AspectJsonOpts) + "\n";
    }

    /// <summary>
    /// Parse a previously written <c>aspect-{id}.json</c> back into an
    /// <see cref="AspectDocument"/>. Returns null on empty / malformed input
    /// or when the payload is not a JSON object (e.g. a legacy markdown file
    /// handed here by mistake) so callers can fall back to the markdown path.
    /// </summary>
    public static AspectDocument? TryParseJson(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        var trimmed = content.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{') return null;
        try
        {
            return JsonSerializer.Deserialize<AspectDocument>(content, AspectJsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions FindingsJsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Serialise per-aspect verdicts into the JSON array string carried by a
    /// timeline event's <c>details["findings"]</c>. Each element is an
    /// <see cref="AspectFinding"/> (<c>{aspect, verdict, reason}</c>) so the
    /// frontend can render a list of toned chips without re-parsing the
    /// preformatted <c>**{aspect}** [{verdict}]: {reason}</c> blob. The
    /// caller decides which verdicts to include (the reopen path passes only
    /// the non-pass ones that triggered the reissue, mirroring
    /// <c>FollowUpSummary</c>).
    /// </summary>
    public static string SerializeFindings(IEnumerable<AspectVerdict> verdicts)
    {
        var items = verdicts
            .Select(v => new AspectFinding(v.Aspect, StatusToken(v.Status), v.Summary ?? string.Empty))
            .ToList();
        return JsonSerializer.Serialize(items, FindingsJsonOpts);
    }

    /// <summary>
    /// Read back the frontmatter status token from a previously written
    /// aspect report. Tolerant of missing frontmatter (returns null).
    /// Uses the canonical
    /// <see cref="AgentStudio.Cli.FrontmatterParser"/>
    /// so the regex+block-detection lives in exactly one place.
    /// </summary>
    public static AspectStatus? ReadStatusFromReport(string content)
    {
        var result = AgentStudio.Cli.FrontmatterParser.Parse(content);
        if (!result.Ok) return null;
        if (!result.Fields.TryGetValue("status", out var value)) return null;
        return value.ToLowerInvariant() switch
        {
            "pass" => AspectStatus.Pass,
            "concerns" => AspectStatus.Concerns,
            "block" => AspectStatus.Block,
            _ => null,
        };
    }

    private static string EscapeYaml(string s)
    {
        if (string.IsNullOrEmpty(s)) return "''";
        var oneLine = s.Replace("\r", " ").Replace("\n", " ").Trim();
        if (oneLine.Contains(':') || oneLine.Contains('#') || oneLine.StartsWith('-'))
        {
            return "\"" + oneLine.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
        return oneLine;
    }
}
