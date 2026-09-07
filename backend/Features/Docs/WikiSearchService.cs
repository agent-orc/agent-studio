using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.Docs;

/// <summary>
/// Lexical search over the project's wiki (<c>docs/</c>) pages with an optional
/// LLM-assisted query-expansion layer.
///
/// <para>
/// <b>Index.</b> An in-memory per-project index over every wiki page (markdown
/// raw, HTML tag-stripped) with weighted fields: title 4x, headings 2x, body
/// 1x. Tokenization is lowercase with German umlaut folding (ä→ae, ö→oe,
/// ü→ue, ß→ss), split on non-alphanumerics, terms from two characters up.
/// Scoring is BM25 (k1=1.2, b=0.75) over the weighted term frequencies. The
/// index is rebuilt lazily on request when a cheap enumerate-only fingerprint
/// (file paths + mtime + size) of the page set changes - the same
/// self-invalidation idea as the wiki tree cache in
/// <see cref="ProjectDocsService"/>.
/// </para>
/// <para>
/// <b>Semantic layer.</b> With <c>semantic=true</c> the query is expanded via a
/// CLI one-shot (config <c>WikiSearch:Cli</c> / <c>WikiSearch:Model</c>,
/// runtime template <c>wiki-search-expand.md</c>); expanded terms join the same
/// BM25 pass at half weight. The layer is strictly fail-open: a missing CLI,
/// timeout, or parse failure degrades to the lexical result set
/// (<c>semanticUsed=false</c>), never to an error for the client.
/// </para>
/// </summary>
public class WikiSearchService
{
    private const double K1 = 1.2;
    private const double B = 0.75;
    private const double TitleWeight = 4;
    private const double HeadingWeight = 2;
    private const double BodyWeight = 1;
    private const double ExpandedTermWeight = 0.5;
    private const int SnippetRadius = 120;
    internal const int MaxExpansionTerms = 8;

    private static readonly HashSet<string> PageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".md", ".html", ".htm" };

    private static readonly Regex FrontmatterRegex =
        new(@"\A---\r?\n.*?\r?\n---\r?\n?", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex HtmlHeadingRegex =
        new(@"<h[1-6][^>]*>(?<body>.*?)</h[1-6]>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex HtmlNoiseRegex =
        new(@"<script[^>]*>.*?</script>|<style[^>]*>.*?</style>|<head[^>]*>.*?</head>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TagRegex = new("<.*?>", RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly TaskScannerService _scanner;
    private readonly ProjectRegistry _registry;
    private readonly CliOneShotRegistry _oneShots;
    private readonly IConfiguration _configuration;
    private readonly AgentStudio.Prompts.RuntimePromptService _prompts;
    private readonly ILogger<WikiSearchService> _logger;
    private readonly WikiContentCache? _wikiContentCache;

    // projectName -> built index. The fingerprint inside decides staleness.
    private readonly ConcurrentDictionary<string, WikiSearchIndex> _indexes =
        new(StringComparer.OrdinalIgnoreCase);

    public WikiSearchService(
        TaskScannerService scanner,
        ProjectRegistry registry,
        CliOneShotRegistry oneShots,
        IConfiguration configuration,
        AgentStudio.Prompts.RuntimePromptService prompts,
        ILogger<WikiSearchService> logger,
        WikiContentCache? wikiContentCache = null)
    {
        _scanner = scanner;
        _registry = registry;
        _oneShots = oneShots;
        _configuration = configuration;
        _prompts = prompts;
        _logger = logger;
        _wikiContentCache = wikiContentCache;
    }

    /// <summary>
    /// Runs one search. Null only when the project is unknown; a wiki with no
    /// docs folder or no hits returns an empty result list. <paramref
    /// name="limit"/> is expected pre-clamped by the endpoint.
    /// </summary>
    public async Task<WikiSearchResponse?> SearchAsync(
        string projectName, string query, bool semantic, int limit, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var baseDir = ProjectRepoResolver.ResolveForProject(projectName, _scanner, _registry);
        if (baseDir == null) return null;

        using var _t = GitProcessTelemetry.BeginRequest("wiki/search", _logger);

        var wikiDir = Path.GetFullPath(Path.Combine(baseDir, ProjectDocsService.WikiRel));
        var index = GetOrBuildIndex(projectName, wikiDir);

        var queryTerms = Tokenize(query).Distinct(StringComparer.Ordinal).ToList();

        var semanticUsed = false;
        var expandedDisplay = new List<string>();
        var expandedTokens = new List<string>();
        if (semantic && queryTerms.Count > 0)
        {
            var expansion = await TryExpandAsync(query, ct).ConfigureAwait(false);
            if (expansion != null)
            {
                semanticUsed = true;
                var known = new HashSet<string>(queryTerms, StringComparer.Ordinal);
                foreach (var term in expansion)
                {
                    if (expandedDisplay.Count >= MaxExpansionTerms) break;
                    var fresh = new List<string>();
                    foreach (var token in Tokenize(term).Distinct(StringComparer.Ordinal))
                        if (known.Add(token)) fresh.Add(token);
                    if (fresh.Count == 0) continue; // repeats the original terms only
                    expandedDisplay.Add(term);
                    expandedTokens.AddRange(fresh);
                }
            }
        }

        var results = Score(index, queryTerms, expandedTokens, limit);
        sw.Stop();
        return new WikiSearchResponse(query, semanticUsed, expandedDisplay, sw.ElapsedMilliseconds, results);
    }

    // -------- Scoring --------

    private static List<WikiSearchResult> Score(
        WikiSearchIndex index, List<string> queryTerms, List<string> expandedTerms, int limit)
    {
        var docs = index.Docs;
        if (docs.Count == 0 || (queryTerms.Count == 0 && expandedTerms.Count == 0))
            return [];

        var searchTerms = queryTerms.Select(t => (Term: t, Weight: 1.0))
            .Concat(expandedTerms.Select(t => (Term: t, Weight: ExpandedTermWeight)))
            .ToList();

        var n = docs.Count;
        var avgdl = index.AverageWeightedLength <= 0 ? 1 : index.AverageWeightedLength;
        var scores = new double[n];
        // Per matched doc: the highlight terms present and the strongest term
        // (by weighted idf) that anchors the snippet.
        var matched = new Dictionary<int, (HashSet<string> Terms, string Anchor, double AnchorRank)>();

        foreach (var (term, weight) in searchTerms)
        {
            if (!index.DocumentFrequency.TryGetValue(term, out var df) || df == 0) continue;
            var idf = Math.Log(1 + (n - df + 0.5) / (df + 0.5));
            for (var i = 0; i < n; i++)
            {
                if (!docs[i].WeightedTf.TryGetValue(term, out var tf)) continue;
                var norm = tf * (K1 + 1) / (tf + K1 * (1 - B + B * docs[i].WeightedLength / avgdl));
                scores[i] += weight * idf * norm;

                var rank = weight * idf;
                if (!matched.TryGetValue(i, out var m))
                {
                    matched[i] = (new HashSet<string>(StringComparer.Ordinal) { term }, term, rank);
                }
                else
                {
                    m.Terms.Add(term);
                    if (rank > m.AnchorRank) matched[i] = (m.Terms, term, rank);
                }
            }
        }

        return matched.Keys
            .OrderByDescending(i => scores[i])
            .ThenBy(i => docs[i].RelPath, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(i => new WikiSearchResult(
                RelPath: docs[i].RelPath,
                Title: docs[i].Title,
                Kind: docs[i].Kind,
                Snippet: BuildSnippet(docs[i].PlainText, matched[i].Anchor, matched[i].Terms),
                Score: Math.Round(scores[i], 4),
                UpdatedAt: docs[i].UpdatedAt))
            .ToList();
    }

    // -------- Snippet --------

    /// <summary>
    /// ±120 characters of plain text around the first occurrence of the anchor
    /// term (fallback: the first occurrence of any matched term). Everything is
    /// HTML-escaped; matched terms are wrapped in <c>&lt;em&gt;</c> - the only
    /// markup the snippet may contain.
    /// </summary>
    internal static string BuildSnippet(string plainText, string anchorTerm, IReadOnlyCollection<string> highlightTerms)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;

        var highlight = highlightTerms as HashSet<string> ?? new HashSet<string>(highlightTerms, StringComparer.Ordinal);
        var tokens = TokenizeWithPositions(plainText);

        SnippetToken? anchor = null;
        foreach (var t in tokens)
        {
            if (t.Folded == anchorTerm) { anchor = t; break; }
            anchor ??= highlight.Contains(t.Folded) ? t : anchor;
        }

        int start, end;
        if (anchor is { } a)
        {
            start = Math.Max(0, a.Start - SnippetRadius);
            end = Math.Min(plainText.Length, a.Start + a.Length + SnippetRadius);
        }
        else
        {
            start = 0;
            end = Math.Min(plainText.Length, 2 * SnippetRadius);
        }

        var sb = new StringBuilder();
        if (start > 0) sb.Append('…');
        var cursor = start;
        foreach (var t in tokens)
        {
            if (t.Start < start || t.Start + t.Length > end) continue;
            sb.Append(EscapeHtml(plainText[cursor..t.Start]));
            var word = plainText.Substring(t.Start, t.Length);
            if (highlight.Contains(t.Folded))
                sb.Append("<em>").Append(EscapeHtml(word)).Append("</em>");
            else
                sb.Append(EscapeHtml(word));
            cursor = t.Start + t.Length;
        }
        sb.Append(EscapeHtml(plainText[cursor..end]));
        if (end < plainText.Length) sb.Append('…');

        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    /// <summary>
    /// Minimal HTML escape for snippet text. Deliberately not
    /// <see cref="WebUtility.HtmlEncode"/>, which would also turn umlauts into
    /// numeric entities and make German snippets unreadable as data.
    /// </summary>
    private static string EscapeHtml(string text) => text
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&#39;");

    // -------- Tokenization --------

    /// <summary>
    /// Lowercase, umlaut-folded (ä→ae, ö→oe, ü→ue, ß→ss) tokens split on
    /// non-alphanumerics; tokens shorter than two characters are dropped.
    /// </summary>
    internal static List<string> Tokenize(string? text)
    {
        var tokens = new List<string>();
        foreach (var t in TokenizeWithPositions(text ?? string.Empty))
            tokens.Add(t.Folded);
        return tokens;
    }

    private readonly record struct SnippetToken(int Start, int Length, string Folded);

    private static List<SnippetToken> TokenizeWithPositions(string text)
    {
        var tokens = new List<SnippetToken>();
        var sb = new StringBuilder();
        var start = -1;

        void Flush(int end)
        {
            if (start >= 0 && sb.Length >= 2)
                tokens.Add(new SnippetToken(start, end - start, sb.ToString()));
            sb.Clear();
            start = -1;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = char.ToLowerInvariant(text[i]);
            var folded = c switch
            {
                'ä' => "ae",
                'ö' => "oe",
                'ü' => "ue",
                'ß' => "ss",
                _ => char.IsLetterOrDigit(c) ? null : string.Empty,
            };
            if (folded == string.Empty)
            {
                Flush(i);
                continue;
            }
            if (start < 0) start = i;
            if (folded == null) sb.Append(c);
            else sb.Append(folded);
        }
        Flush(text.Length);
        return tokens;
    }

    // -------- Index --------

    private sealed record WikiIndexedDoc(
        string RelPath,
        string Title,
        string Kind,
        DateTime UpdatedAt,
        string PlainText,
        IReadOnlyDictionary<string, double> WeightedTf,
        double WeightedLength);

    private sealed record WikiSearchIndex(
        string Fingerprint,
        IReadOnlyList<WikiIndexedDoc> Docs,
        IReadOnlyDictionary<string, int> DocumentFrequency,
        double AverageWeightedLength);

    private WikiSearchIndex GetOrBuildIndex(string projectName, string wikiDir)
    {
        // Staleness gate via the central wiki cache (AGT-2382). Its docs/
        // signature is the same path+mtime+size hash this service used to
        // compute for itself, over a superset of the indexed files - so it can
        // only ever over-invalidate, never miss a change. Reusing it turns a
        // warm search from a full docs/ walk into a dictionary lookup and
        // removes the last parallel staleness probe in the docs area.
        //
        // The gate only applies when the snapshot projects the very directory
        // this search reads. It returns null otherwise (a project with a
        // configured wikiSourceBranch publishes the branch worktree, not the
        // checkout searched here, and a placeholder signature describes no tree
        // at all), and the local enumeration below then decides staleness.
        var centralFingerprint = _wikiContentCache?.GetDocsSignature(projectName, wikiDir);
        if (centralFingerprint != null
            && _indexes.TryGetValue(projectName, out var warm)
            && warm.Fingerprint == centralFingerprint)
            return warm;

        var pages = EnumeratePages(wikiDir);
        var fingerprint = centralFingerprint ?? ComputeFingerprint(pages);
        if (_indexes.TryGetValue(projectName, out var cached) && cached.Fingerprint == fingerprint)
            return cached;

        var sw = Stopwatch.StartNew();
        var built = BuildIndex(wikiDir, pages, fingerprint);
        _indexes[projectName] = built;
        _logger.LogDebug("wiki-search index rebuilt project={Project} docs={Docs} in {Ms}ms",
            projectName, built.Docs.Count, sw.ElapsedMilliseconds);
        return built;
    }

    /// <summary>Every indexable wiki page (md/html, no companions, no hidden segments), stable order.</summary>
    private static List<FileInfo> EnumeratePages(string wikiDir)
    {
        var pages = new List<FileInfo>();
        if (!Directory.Exists(wikiDir)) return pages;
        var root = Path.GetFullPath(wikiDir);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (!PageExtensions.Contains(Path.GetExtension(path))) continue;
            var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (rel.EndsWith(".report.html", StringComparison.OrdinalIgnoreCase)
                || rel.EndsWith(".report.htm", StringComparison.OrdinalIgnoreCase)) continue;
            if (rel.Split('/').Any(s => s.StartsWith('.'))) continue;
            pages.Add(new FileInfo(path));
        }
        pages.Sort((a, b) => string.Compare(a.FullName, b.FullName, StringComparison.Ordinal));
        return pages;
    }

    /// <summary>
    /// Enumerate-only staleness probe: every page's path + last-write time +
    /// size, hashed. No content reads - the cheap gate in front of the O(N)
    /// content-reading rebuild.
    /// </summary>
    private static string ComputeFingerprint(List<FileInfo> pages)
    {
        var sb = new StringBuilder();
        foreach (var f in pages)
            sb.Append(f.FullName).Append('')
              .Append(f.LastWriteTimeUtc.Ticks).Append('')
              .Append(f.Length).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    private static WikiSearchIndex BuildIndex(string wikiDir, List<FileInfo> pages, string fingerprint)
    {
        var root = Path.GetFullPath(wikiDir);
        var docs = new List<WikiIndexedDoc>();

        foreach (var file in pages)
        {
            string text;
            try
            {
                GitProcessTelemetry.RecordFileRead();
                text = File.ReadAllText(file.FullName);
            }
            catch (Exception __ex)
            {
                SilentCatch.Note(__ex, "WikiSearchService: unreadable wiki page skipped from the search index.");
                continue;
            }

            var rel = Path.GetRelativePath(root, file.FullName).Replace('\\', '/');
            var isMd = file.Extension.Equals(".md", StringComparison.OrdinalIgnoreCase);
            var title = ProjectDocsService.ExtractWikiPageTitle(file.FullName, file.Extension)
                ?? Path.GetFileNameWithoutExtension(file.Name);

            string plainText, bodyText;
            List<string> headings;
            if (isMd)
            {
                plainText = ProjectDocsService.StripWikiFrontmatter(text); // md is indexed raw
                (headings, bodyText) = SplitMarkdownHeadings(plainText, title);
            }
            else
            {
                var cleaned = HtmlNoiseRegex.Replace(text, " ");
                headings = HtmlHeadingRegex.Matches(cleaned)
                    .Select(m => WebUtility.HtmlDecode(TagRegex.Replace(m.Groups["body"].Value, " ")).Trim())
                    .Where(h => h.Length > 0)
                    .ToList();
                plainText = WebUtility.HtmlDecode(TagRegex.Replace(cleaned, " "));
                bodyText = WebUtility.HtmlDecode(TagRegex.Replace(HtmlHeadingRegex.Replace(cleaned, " "), " "));
            }

            var tf = new Dictionary<string, double>(StringComparer.Ordinal);
            void Add(IEnumerable<string> tokens, double weight)
            {
                foreach (var token in tokens)
                    tf[token] = tf.GetValueOrDefault(token) + weight;
            }

            var titleTokens = Tokenize(title);
            var headingTokens = Tokenize(string.Join('\n', headings));
            var bodyTokens = Tokenize(bodyText);
            Add(titleTokens, TitleWeight);
            Add(headingTokens, HeadingWeight);
            Add(bodyTokens, BodyWeight);

            var weightedLength = TitleWeight * titleTokens.Count
                + HeadingWeight * headingTokens.Count
                + BodyWeight * bodyTokens.Count;
            if (tf.Count == 0) continue; // nothing indexable

            docs.Add(new WikiIndexedDoc(
                RelPath: rel,
                Title: title,
                Kind: isMd ? "md" : "html",
                UpdatedAt: file.LastWriteTimeUtc,
                PlainText: plainText,
                WeightedTf: tf,
                WeightedLength: weightedLength));
        }

        var df = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var doc in docs)
            foreach (var term in doc.WeightedTf.Keys)
                df[term] = df.GetValueOrDefault(term) + 1;

        var avg = docs.Count == 0 ? 0 : docs.Average(d => d.WeightedLength);
        return new WikiSearchIndex(fingerprint, docs, df, avg);
    }

    /// <summary>
    /// Splits a raw markdown body into heading text (all <c>#</c> lines, minus
    /// the first H1 when it already supplied the title - it is counted at title
    /// weight) and the remaining body lines.
    /// </summary>
    private static (List<string> Headings, string Body) SplitMarkdownHeadings(string markdown, string title)
    {
        var headings = new List<string>();
        var body = new StringBuilder();
        var titleSkipped = false;
        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#'))
            {
                var headingText = trimmed.TrimStart('#').Trim();
                if (!titleSkipped && trimmed.StartsWith("# ", StringComparison.Ordinal)
                    && string.Equals(headingText, title, StringComparison.Ordinal))
                {
                    titleSkipped = true;
                    continue;
                }
                if (headingText.Length > 0) headings.Add(headingText);
                continue;
            }
            body.Append(line).Append('\n');
        }
        return (headings, body.ToString());
    }

    // -------- Semantic expansion (fail-open) --------

    /// <summary>
    /// Query expansion via a CLI one-shot rendering the
    /// <c>wiki-search-expand.md</c> runtime template. Null on ANY failure
    /// (unregistered CLI, non-zero exit, timeout, unparsable output) - the
    /// caller then serves lexical results with <c>semanticUsed=false</c>.
    /// </summary>
    private async Task<List<string>?> TryExpandAsync(string query, CancellationToken ct)
    {
        try
        {
            var cli = _configuration["WikiSearch:Cli"] ?? "claude";
            var model = _configuration["WikiSearch:Model"] ?? ModelFamilyResolver.Resolve(ModelFamilies.ClaudeHaiku);
            var impl = _oneShots.Get(cli);
            if (impl == null) return null;

            var prompt = _prompts.Render(
                AgentStudio.Prompts.RuntimePromptService.WikiSearchExpand,
                new Dictionary<string, string?> { ["query"] = query });

            var result = await impl.RunAsync(new CliOneShotRequest(cli, model, prompt)
            {
                Timeout = TimeSpan.FromSeconds(30),
                Source = "wiki-search",
                TemplateRef = AgentStudio.Prompts.RuntimePromptService.WikiSearchExpand,
            }, ct).ConfigureAwait(false);
            if (!result.Ok) return null;

            var text = string.IsNullOrWhiteSpace(result.ParsedText) ? result.Stdout : result.ParsedText;
            return ParseExpansionTerms(text);
        }
        catch (Exception __ex)
        {
            SilentCatch.Note(__ex, "WikiSearchService: semantic expansion failed; serving lexical results only.");
            return null;
        }
    }

    /// <summary>Parses <c>{"terms":["…"]}</c> out of the CLI reply; null when the shape is missing.</summary>
    internal static List<string>? ParseExpansionTerms(string raw)
    {
        try
        {
            var text = raw.Trim();
            var first = text.IndexOf('{');
            var last = text.LastIndexOf('}');
            if (first < 0 || last <= first) return null;
            using var json = JsonDocument.Parse(text[first..(last + 1)]);
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || !json.RootElement.TryGetProperty("terms", out var terms)
                || terms.ValueKind != JsonValueKind.Array)
                return null;

            var list = new List<string>();
            foreach (var term in terms.EnumerateArray())
            {
                if (term.ValueKind != JsonValueKind.String) continue;
                var value = term.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(value)) continue;
                list.Add(value);
                if (list.Count >= MaxExpansionTerms) break;
            }
            return list;
        }
        catch (Exception __ex)
        {
            SilentCatch.Note(__ex, "WikiSearchService: unparsable expansion reply; serving lexical results only.");
            return null;
        }
    }
}

/// <summary>Wiki search response envelope. <c>ExpandedTerms</c> is empty unless
/// the semantic layer ran and contributed terms.</summary>
public record WikiSearchResponse(
    string Query,
    bool SemanticUsed,
    List<string> ExpandedTerms,
    long DurationMs,
    List<WikiSearchResult> Results);

/// <summary>One search hit. <c>Snippet</c> is HTML-escaped text with matched
/// terms wrapped in <c>&lt;em&gt;</c> (the only markup allowed).</summary>
public record WikiSearchResult(
    string RelPath,
    string Title,
    string Kind,
    string Snippet,
    double Score,
    DateTime UpdatedAt);
