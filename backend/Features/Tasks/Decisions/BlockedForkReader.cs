namespace AgentStudio.Tasks;

/// <summary>
/// Pure reader that turns a blocked run's final message into decision content
/// when the run stopped at a fork: a stated question and two to four options.
/// It reuses <see cref="ParkedDecisionReader"/>, whose option fields already
/// match the decision card's, so a Blocked outcome and a park read the same way.
/// A message without a fork returns null and the card keeps the prose escalation.
/// </summary>
public static class BlockedForkReader
{
    public const string RecommendationReason = "The blocked run recommended this option.";

    public static DecisionContent? Read(string? reason, string? message)
    {
        var request = ParkedDecisionReader.Read(reason, message);
        if (!request.Stated) return null;
        if (request.Options.Count is < DecisionCardPolicy.MinOptions or > DecisionCardPolicy.MaxOptions) return null;

        var recommended = request.Options.Where(option => option.Recommended).ToList();
        var content = new DecisionContent
        {
            Question = request.Question,
            Options = request.Options.Select(option => new DecisionOption
            {
                Id = option.Id,
                Label = option.Label,
                Consequences = option.Consequences,
            }).ToList(),
            RecommendedOptionId = recommended.Count == 1 ? recommended[0].Id : null,
            RecommendationReason = recommended.Count == 1 ? RecommendationReason : null,
        };
        return DecisionCardPolicy.ValidateContent(content).Count == 0 ? content : null;
    }
}
