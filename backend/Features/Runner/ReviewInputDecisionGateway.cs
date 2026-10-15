using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Runner;

/// <summary>Typed, bounded question submitted by a legacy review caller.</summary>
public sealed record ReviewInputDecisionRequest(
    string Kind,
    string ProjectId,
    string TaskId,
    string SourceState,
    string Subject,
    string Summary,
    string SourceDigest,
    IReadOnlyList<GroundsOmissionV1> Omissions,
    string AuthorityAttemptId,
    long AuthorityFence = 0,
    long AuthorityEpoch = 0,
    LoopExhaustionSignalV1? LoopSignal = null);

/// <summary>
/// The ReviewDecision switch is the sole routing selector. Legacy callers
/// keep their in-process behavior in Monolith mode; Engine mode freezes the
/// question before creating a claimable occurrence.
/// </summary>
public sealed class ReviewInputDecisionGateway(
    IConfiguration configuration,
    IServiceProvider services,
    OrchestratorDecisionStore receipts,
    TimeProvider time)
{
    public const string Transition = "ReviewDecision";
    public const int MaxSummaryChars = 8 * 1024;
    public const int SummaryChunkChars = 400;
    private static readonly Regex RawSessionPath = new(
        @"(?i)(?:[a-z]:[\\/]|~[\\/]|/(?!api/))\S{0,240}(?:sessions[\\/]|cli-output\.log|tool-calls\.jsonl)\S*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Credential = new(
        @"(?i)(?:sk-[a-z0-9_-]{12,}|gh[opusr]_[a-z0-9_]{12,}|(?:api[_-]?key|access[_-]?token|authorization|password|secret)\s*[:=]\s*\S+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public bool EngineDecides => MonolithTransitionSwitch.For(configuration, Transition)
        == OrchestrationExecutionMode.Engine;

    public OrchestrationRunDto Submit(ReviewInputDecisionRequest request)
    {
        if (!EngineDecides) throw new InvalidOperationException("review-decision-profile-not-engine");
        var occurrences = services.GetRequiredService<MonolithOrchestrationStore>();
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceDigest);
        if (request.Kind is not ("needs-input" or "abort-review" or "supervision" or "loop-exhaustion"))
            throw new ArgumentException("Unknown review decision kind.", nameof(request));
        var omissions = request.Omissions.ToList();
        string Sanitize(string value, string field)
        {
            value = RawSessionPath.Replace(value, _ =>
            {
                omissions.Add(new(field, "raw-session-path"));
                return "[omitted: raw session path]";
            });
            return Credential.Replace(value, _ =>
            {
                omissions.Add(new(field, "secret"));
                return "[omitted: secret]";
            });
        }
        var summary = Sanitize(request.Summary, "review.summary");
        var subject = Sanitize(request.Subject, "review.subject");
        if (summary.Length > MaxSummaryChars)
        {
            summary = summary[..MaxSummaryChars];
            omissions.Add(new("review.summary", "oversize"));
        }
        request = request with { Summary = summary,
            Subject = subject.Length <= 512 ? subject : subject[..512],
            Omissions = omissions.Take(24).ToArray() };
        var subjectHash = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(request.Kind + "\n" + request.Subject + "\n" + request.SourceDigest)));
        var run = occurrences.CreateReviewInputDecision(request.ProjectId, request.TaskId,
            JsonSerializer.Serialize(request, DecisionGroundsPolicy.Json),
            subjectHash, request.SourceState, request.AuthorityAttemptId,
            request.AuthorityFence, request.AuthorityEpoch);
        if (receipts.GetInputs(run.RunId) is not null)
        {
            occurrences.Activate(run.RunId);
            return run;
        }
        var at = time.GetUtcNow().UtcDateTime;
        var source = new GroundsSourceRefV1("bounded-review-input",
            $"/api/orchestration/review-inputs/{run.RunId}", request.SourceDigest);
        var boundedSummary = request.Summary;
        var facts = new List<GroundsFactV1>
        {
            new("review.kind", "truth", "Decision kind", request.Kind, null, source, at,
                "complete", GroundsReliability.Reliable, true),
            new("review.subject", "truth", "Decision subject", request.Subject, null, source, at,
                "complete", GroundsReliability.Reliable, true),
            new("card.lane", "truth", "Expected card lane", request.SourceState, null,
                new("task", $"task:{request.TaskId}"), at, "complete", GroundsReliability.Reliable, true),
        };
        for (var offset = 0; offset < boundedSummary.Length; offset += SummaryChunkChars)
            facts.Add(new GroundsFactV1($"review.summary.{offset / SummaryChunkChars + 1}",
                "evidence", "Bounded typed summary fragment",
                boundedSummary.Substring(offset, Math.Min(SummaryChunkChars, boundedSummary.Length - offset)),
                null, source, at, "bounded", GroundsReliability.Reliable, true));
        var alternatives = request.Kind == "supervision"
            ? new GroundsAlternativeV1[]
            {
                new(DecisionActionKinds.Ask, "operator:ai", "reversible", "A0", true),
                new(DecisionActionKinds.Observe, "operator:feed", "reversible", "A0", true),
            }
            : request.Kind == "loop-exhaustion"
            ? new GroundsAlternativeV1[]
            {
                new(DecisionActionKinds.Ask, "operator:ai", "reversible", "A0", true),
                new(DecisionActionKinds.Retry, "card:ready", "reversible", "A1", true),
            }
            : new GroundsAlternativeV1[]
            {
                new(DecisionActionKinds.Ask, "operator:ai", "reversible", "A0", true),
                new(DecisionActionKinds.Retry, "card:ready", "reversible", "A1", true),
                new(DecisionActionKinds.Continue, "card:review", "reversible", "A1", true),
            };
        var ledger = new CardRunLedgerV1(0, 0, 0, DecisionTokenUsageV1.Zero, 0,
            DecisionTokenUsageV1.Zero, 0, 0, 0, [], DecisionPriceStatuses.NoUsage,
            "not-collected", []);
        var progress = new ProgressDefinitionV1("review-input.v1",
            "No delivery progress is inferred from this bounded review input.", null, 0, 0, 0);
        var grounds = new DecisionGroundsInputV1(run.RunId,
            new GroundsTriggerV1("review-decision", request.Kind, at),
            new GroundsScopeV1("card", request.ProjectId, request.TaskId,
                request.AuthorityAttemptId, request.AuthorityFence, request.AuthorityEpoch, null),
            request.Kind, null, "review-input.v1", DecisionGroundsCollector.Route(configuration),
            facts, alternatives, [], ledger, progress, omissions)
        {
            LoopSignals = request.LoopSignal is null ? [] : [request.LoopSignal],
        };
        receipts.FreezeInputs(new DecisionShadowInputV1(grounds, DecisionActionKinds.Ask,
            "operator:ai", "The legacy review path did not decide an engine occurrence.",
            DecisionReceiptPolicy.ActiveMode));
        occurrences.Activate(run.RunId);
        return run;
    }
}
