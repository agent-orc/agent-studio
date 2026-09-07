using AgentStudio.Cli;
using AgentStudio.Prompts;
using AgentStudio.Shared;

namespace AgentStudio.Watcher;

/// <summary>
/// Bounded strong-model analysis (§3 "Analysis receipt", §5 model economy).
/// Called only for declared uncertainty - a Contradiction or Drift case,
/// where two sources disagree or a version change needs a hypothesis.
/// Repetition and Hygiene proposals are drafted straight from the
/// deterministic evidence pack with no model call, matching §5's "No model"
/// row for exact recipe matching. One call per evidence-pack digest: a
/// caller must not invoke this twice for the same digest.
/// </summary>
public sealed class WatcherAnalysisService
{
    private const string PromptTemplate = "watcher-analysis.md";

    private static readonly string[] AnalysisWarrantedClasses =
        [WatcherDetectorClasses.Contradiction, WatcherDetectorClasses.Drift];

    private readonly CliOneShotRegistry? _oneShots;
    private readonly RuntimePromptService _prompts;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WatcherAnalysisService> _logger;

    public WatcherAnalysisService(
        IConfiguration configuration,
        RuntimePromptService prompts,
        ILogger<WatcherAnalysisService> logger,
        CliOneShotRegistry? oneShots = null)
    {
        _configuration = configuration;
        _prompts = prompts;
        _logger = logger;
        _oneShots = oneShots;
    }

    /// <summary>True when this case's detector class carries declared uncertainty and warrants a strong-model call.</summary>
    public bool NeedsAnalysis(string detectorClass) => AnalysisWarrantedClasses.Contains(detectorClass);

    public async Task<WatcherAnalysisReceipt> AnalyzeAsync(WatcherCase watcherCase, WatcherEvidencePack pack, CancellationToken ct)
    {
        var cli = _configuration.GetValue("Watcher:AnalysisCli", "claude") ?? "claude";
        var model = _configuration.GetValue("Watcher:AnalysisModel", ModelIds.ClaudeSonnet5) ?? ModelIds.ClaudeSonnet5;
        var thinkingLevel = _configuration.GetValue("Watcher:AnalysisThinkingLevel", "medium") ?? "medium";

        var oneShot = _oneShots?.Get(cli);
        if (oneShot == null)
        {
            _logger.LogWarning("Watcher analysis unavailable: no ICliOneShot registered for '{Cli}'", cli);
            return new WatcherAnalysisReceipt
            {
                CaseId = watcherCase.Id,
                Model = model,
                ThinkingLevel = thinkingLevel,
                Ok = false,
                Error = $"No CLI one-shot implementation registered for '{cli}'.",
            };
        }

        var prompt = BuildPrompt(watcherCase, pack);
        var result = await oneShot.RunAsync(new CliOneShotRequest(cli, model, prompt)
        {
            ThinkingLevel = thinkingLevel,
            Timeout = TimeSpan.FromSeconds(120),
            Source = AdHocUsageSources.Watcher,
            RecordUsage = true,
            Project = watcherCase.Project,
        }, ct);

        if (!result.Ok)
        {
            _logger.LogWarning(
                "Watcher analysis call failed for case {CaseId}: exit={ExitCode} error={Error}",
                watcherCase.Id, result.ExitCode, result.Error);
            return new WatcherAnalysisReceipt
            {
                CaseId = watcherCase.Id,
                Model = model,
                ThinkingLevel = thinkingLevel,
                Ok = false,
                Error = result.Error ?? "Analysis call failed without a diagnostic.",
            };
        }

        var summary = string.IsNullOrWhiteSpace(result.ParsedText) ? result.Stdout : result.ParsedText;
        return new WatcherAnalysisReceipt
        {
            CaseId = watcherCase.Id,
            Model = model,
            ThinkingLevel = thinkingLevel,
            Summary = summary.Trim(),
            InputTokens = result.Usage?.InputTokens ?? 0,
            OutputTokens = result.Usage?.OutputTokens ?? 0,
            Ok = true,
        };
    }

    private string BuildPrompt(WatcherCase watcherCase, WatcherEvidencePack pack)
    {
        var evidence = string.Join("\n", pack.Signals.Select(s => $"- {s.Label}: {s.Value}"));
        var missing = pack.MissingEvidence.Count == 0
            ? "(none)"
            : string.Join("\n", pack.MissingEvidence.Select(m => $"- {m}"));
        var values = new Dictionary<string, string?>
        {
            ["case_id"] = watcherCase.Id,
            ["detector_class"] = watcherCase.DetectorClass,
            ["project"] = watcherCase.Project,
            ["fingerprint"] = watcherCase.Fingerprint,
            ["evidence"] = evidence,
            ["missing_evidence"] = missing,
        };
        try
        {
            return _prompts.Render(PromptTemplate, values);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falling back to inline watcher-analysis prompt template");
            return $"Watcher case {watcherCase.Id} ({watcherCase.DetectorClass}) fingerprint {watcherCase.Fingerprint}: {evidence}. Respond with root cause, confidence, and a recommendation, then [[TASK_DONE]].";
        }
    }
}
