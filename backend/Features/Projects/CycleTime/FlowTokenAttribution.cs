namespace AgentStudio.Projects;

/// <summary>Attributes stored calls to evidence segments without repricing them.</summary>
public static class FlowTokenAttribution
{
    public static TaskCycleTime Apply(TaskCycleTime row, TaskTokenSummary? summary,
        IReadOnlyList<TimelineEvent> ledger)
    {
        if (summary is null) return row;
        var cardTokens = summary.Entries.Sum(Tokens);
        var cardCost = summary.Entries.Sum(c => c.ModelPriced ? c.EstimatedApiCostUsd : 0);
        if (row.Flow is null) return row with { CardTokens = cardTokens, CardCostUsd = cardCost };
        var segments = row.Flow.Segments.ToList();
        foreach (var call in summary.Entries)
        {
            if (call.Ts < row.Flow.Start || call.Ts > row.Flow.End) continue;
            var participant = call.ParticipantId ?? "";
            var index = -1;
            if (participant.StartsWith("agent:", StringComparison.Ordinal))
            {
                var participantParts = participant.Split(':');
                var claim = ledger.FirstOrDefault(e => e.Kind == TimelineEventKinds.LaneChanged
                    && e.Details?.GetValueOrDefault("to") == TaskStates.Progress
                    && !string.IsNullOrWhiteSpace(e.Details?.GetValueOrDefault("attemptId"))
                    && participantParts.Contains(e.Details!["attemptId"], StringComparer.Ordinal));
                if (claim is not null)
                    index = segments.FindIndex(s => s.Class == "active"
                        && s.Step is ("coding" or "codingNoDelivery") && s.Start <= claim.Ts && s.End > claim.Ts);
                if (index < 0)
                    index = segments.FindIndex(s => s.Class == "active"
                        && s.Step is ("coding" or "codingNoDelivery") && s.Start <= call.Ts && s.End >= call.Ts);
            }
            else if (participant.StartsWith("support:remote-review:", StringComparison.Ordinal))
            {
                var id = participant["support:remote-review:".Length..];
                index = segments.FindIndex(s => s.AttemptId == id
                    && s.Step is ("reviewAspects" or "reviewFailedAttempt"));
                if (index < 0) index = segments.FindIndex(s => s.Step == "reviewAspects"
                    && s.Start <= call.Ts && s.End >= call.Ts);
            }
            if (index < 0 && participant.StartsWith("support:", StringComparison.Ordinal))
            {
                segments.Add(new FlowSegment("active", "supporting", null, null,
                    call.Ts, call.Ts, 0));
                index = segments.Count - 1;
            }
            if (index < 0)
                index = segments.FindIndex(s => s.Start <= call.Ts && s.End >= call.Ts);
            if (index < 0) continue;
            var segment = segments[index];
            segments[index] = segment with
            {
                Tokens = segment.Tokens + Tokens(call),
                InputTokens = segment.InputTokens + call.InputTokens,
                OutputTokens = segment.OutputTokens + call.OutputTokens,
                CacheReadTokens = segment.CacheReadTokens + call.CacheReadTokens,
                CacheCreationTokens = segment.CacheCreationTokens + call.CacheCreationTokens,
                CostUsd = segment.CostUsd + (call.ModelPriced ? call.EstimatedApiCostUsd : 0),
                Calls = segment.Calls + 1,
                UnpricedCalls = segment.UnpricedCalls + (call.ModelPriced ? 0 : 1),
            };
        }
        return row with { Flow = row.Flow with { Segments = segments.OrderBy(s => s.Start).ToList() },
            CardTokens = cardTokens, CardCostUsd = cardCost };
    }

    private static long Tokens(TaskTokenCall call) => call.InputTokens + call.OutputTokens
        + call.CacheReadTokens + call.CacheCreationTokens;
}
