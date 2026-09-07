using System.Text.Json;

namespace AgentStudio.Watcher;

/// <summary>
/// The bounded analysis receipt of dossier section 3. The model names competing
/// hypotheses and what it does not know; it never grants authority, and the
/// proposal is written by deterministic code from this receipt plus the pack.
/// </summary>
public sealed record WatcherAnalysis
{
    public required string RootCause { get; init; }
    public IReadOnlyList<string> CompetingHypotheses { get; init; } = [];
    /// <summary>One of low, medium, high. Free-form values are normalised away.</summary>
    public string Confidence { get; init; } = "low";
    public IReadOnlyList<string> Unknowns { get; init; } = [];
    public IReadOnlyList<string> SuggestedChanges { get; init; } = [];
    public IReadOnlyList<WatcherModelCall> Calls { get; init; } = [];
}

/// <summary>
/// Bounded analysis seam. Implementations must not mutate task or Git state and
/// must return null rather than throw when the route is unavailable, so an
/// unreachable analyser degrades a proposal to evidence only instead of losing
/// the case.
/// </summary>
public interface IWatcherAnalyst
{
    /// <summary>
    /// Analyse one case. Callers only reach this after the case declared an
    /// uncertain cause and the contingent admitted a model call.
    /// </summary>
    Task<WatcherAnalysis?> AnalyseAsync(WatcherEvidencePack pack, CancellationToken ct = default);
}

/// <summary>
/// Sol/medium analysis through the shared one-shot seam, with a Mini/high
/// compression pass first when the pack exceeds the compression threshold.
/// </summary>
/// <remarks>
/// The routes are the ones dossier section 5 fixes: no model for detection,
/// Mini/high only for bounded compression, Sol/medium as the strong analysis
/// floor. Quota or price cannot lower either, so this class does not read a
/// cheaper fallback anywhere.
/// </remarks>
public sealed class CliWatcherAnalyst : IWatcherAnalyst
{
    private readonly CliOneShotRegistry _oneShots;
    private readonly ModelRoutingPolicyRegistry _routing;
    private readonly IConfiguration _configuration;
    private readonly ITokenPriceProvider _prices;
    private readonly ILogger<CliWatcherAnalyst> _logger;

    public CliWatcherAnalyst(
        CliOneShotRegistry oneShots,
        ModelRoutingPolicyRegistry routing,
        IConfiguration configuration,
        ILogger<CliWatcherAnalyst> logger,
        ITokenPriceProvider? prices = null)
    {
        _oneShots = oneShots;
        _routing = routing;
        _configuration = configuration;
        _logger = logger;
        _prices = prices ?? new TokenEconomyPriceProvider();
    }

    public async Task<WatcherAnalysis?> AnalyseAsync(WatcherEvidencePack pack, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var options = WatcherOptions.FromConfiguration(_configuration);
        var oneShot = _oneShots.Get(PipelineStepModelDefaults.DefaultCli);
        if (oneShot is null)
        {
            _logger.LogWarning(
                "watcher-analysis-unavailable case={CaseId} reason=no-one-shot-for-cli cli={Cli}",
                pack.CaseId,
                PipelineStepModelDefaults.DefaultCli);
            return null;
        }

        var (analysisModel, analysisThinking) = WatcherModelRouting.Route(_routing, options.AnalysisTier);
        var calls = new List<WatcherModelCall>();
        var evidence = pack.ToMarkdown();
        if (evidence.Length > options.CompressionThresholdCharacters)
        {
            var compressed = await RunAsync(
                oneShot,
                WatcherModelCallPurposes.Compression,
                PipelineStepModelDefaults.SupportModel,
                PipelineStepModelDefaults.SupportThinkingLevel,
                CompressionPrompt(evidence),
                pack,
                options.AnalysisTimeout,
                calls,
                ct);
            if (!string.IsNullOrWhiteSpace(compressed)) evidence = compressed!;
        }

        var answer = await RunAsync(
            oneShot,
            WatcherModelCallPurposes.Analysis,
            analysisModel,
            analysisThinking,
            AnalysisPrompt(pack, evidence),
            pack,
            options.AnalysisTimeout,
            calls,
            ct);
        if (string.IsNullOrWhiteSpace(answer)) return null;

        var parsed = Parse(answer!);
        if (parsed is null)
        {
            _logger.LogWarning(
                "watcher-analysis-unparseable case={CaseId} model={Model}",
                pack.CaseId,
                analysisModel);
            return null;
        }

        return parsed with { Calls = calls };
    }

    private async Task<string?> RunAsync(
        ICliOneShot oneShot,
        string purpose,
        string model,
        string thinkingLevel,
        string prompt,
        WatcherEvidencePack pack,
        TimeSpan timeout,
        List<WatcherModelCall> calls,
        CancellationToken ct)
    {
        var result = await oneShot.RunAsync(
            new CliOneShotRequest(PipelineStepModelDefaults.DefaultCli, model, prompt)
            {
                ThinkingLevel = thinkingLevel,
                Timeout = timeout,
                Source = $"watcher-{purpose}",
                Project = pack.Project,
            },
            ct);

        var input = result.Usage?.InputTokens ?? 0;
        var output = result.Usage?.OutputTokens ?? 0;
        var estimate = _prices.Estimate(model, input, output, 0, 0);
        calls.Add(new WatcherModelCall
        {
            Purpose = purpose,
            Model = model,
            ThinkingLevel = thinkingLevel,
            InputTokens = input,
            OutputTokens = output,
            CostUsd = estimate.ModelKnown ? estimate.Total : null,
            PriceKnown = estimate.ModelKnown,
            AtUtc = DateTime.UtcNow,
        });

        if (!result.Ok)
        {
            _logger.LogWarning(
                "watcher-analysis-call-failed case={CaseId} purpose={Purpose} model={Model} error={Error}",
                pack.CaseId,
                purpose,
                model,
                result.Error);
            return null;
        }
        return result.ParsedText;
    }

    private static string CompressionPrompt(string evidence) =>
        """
        Compress the evidence table below for a later root-cause analysis.

        Rules:
        - Keep every distinct fact, timestamp, path, identifier, and count.
        - Merge only literal repetitions.
        - Do not diagnose, do not speculate, do not add facts.
        - Answer with the compressed evidence only.

        """ + evidence;

    private static string AnalysisPrompt(WatcherEvidencePack pack, string evidence) =>
        $$"""
        You analyse one monitoring case for an autonomous delivery platform. You have
        no authority to change anything. Your answer prepares a ticket a human will
        review.

        Case title: {{pack.Title}}
        Detector class: {{pack.DetectorClass}}
        Detector rule: {{pack.DetectorRule}}

        Evidence:
        {{evidence}}

        Answer with one JSON object and nothing else:
        {
          "rootCause": "one sentence, or 'unknown' when the evidence does not support one",
          "competingHypotheses": ["at most three, each one sentence"],
          "confidence": "low | medium | high",
          "unknowns": ["what evidence is missing to decide"],
          "suggestedChanges": ["at most four concrete, verifiable steps"]
        }

        Never claim a cause the evidence does not show. Prefer "unknown" with named
        unknowns over a confident guess.
        """;

    private static WatcherAnalysis? Parse(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var document = JsonDocument.Parse(text[start..(end + 1)]);
            var root = document.RootElement;
            var confidence = Text(root, "confidence").ToLowerInvariant();
            return new WatcherAnalysis
            {
                RootCause = Text(root, "rootCause") is { Length: > 0 } cause ? cause : "unknown",
                CompetingHypotheses = List(root, "competingHypotheses"),
                Confidence = confidence is "low" or "medium" or "high" ? confidence : "low",
                Unknowns = List(root, "unknowns"),
                SuggestedChanges = List(root, "suggestedChanges"),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? ""
            : "";

    private static List<string> List(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) return [];
        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()?.Trim() ?? "")
            .Where(item => item.Length > 0)
            .Take(6)
            .ToList();
    }
}
