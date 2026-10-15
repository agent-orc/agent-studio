using AgentStudio.OrchestrationDecisions;
using AgentStudio.Orchestrator;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

public interface IAiOperatorDecisionQueue
{
    void Enqueue(TaskInfo task, string occurrenceId, DecisionChoiceV1 choice);
}

/// <summary>Hands a held decision to the task-scoped AI Operator session.</summary>
public sealed class AiOperatorDecisionQueue(OrchestratorTurnService turns) : IAiOperatorDecisionQueue
{
    public void Enqueue(TaskInfo task, string occurrenceId, DecisionChoiceV1 choice)
    {
        var context = $"task:{task.ProjectName}/{task.Key ?? task.Id}";
        turns.Enqueue(context, new OrchestratorTurnRequest(
            $"Decision occurrence {occurrenceId} needs an AI Operator answer. "
            + $"The decision step chose {choice.Action} ({choice.Target}). "
            + "Review the receipt and card, then decide the next permitted action. "
            + "Ask the human operator for any A2 or A3 mutation. "
            + "Do not mutate the card without the required authority."));
    }
}
