using System.Text.Json;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>Task Server admission for the active coding-completion model choice.</summary>
public sealed class ActiveChoiceAdmissionAdapter(
    OrchestratorDecisionStore decisions, AttemptAuthorityService authority)
{
    public bool HasActiveGrounds(string occurrenceId)
        => decisions.GetInputs(occurrenceId)?.Mode == DecisionReceiptPolicy.ActiveMode;

    public DecisionChoiceV1 Validate(MonolithOrchestrationOccurrence occurrence, TaskInfo task,
        OrchestrationAction stageAction, JsonElement output)
    {
        if (!output.TryGetProperty("receiptId", out var receiptId)
            || receiptId.ValueKind != JsonValueKind.String
            || decisions.GetReceipt(receiptId.GetString()!) is not { } receipt
            || receipt.Mode != DecisionReceiptPolicy.ActiveMode
            || receipt.OccurrenceId != occurrence.Run.RunId
            || receipt.GroundsDigest != output.GetProperty("groundsDigest").GetString()
            || !DecisionGroundsPolicy.IsIntact(receipt.Grounds))
            throw new InvalidOperationException("decision-receipt-mismatch");
        if (decisions.GetInputs(occurrence.Run.RunId) is not { Mode: DecisionReceiptPolicy.ActiveMode } input
            || DecisionGroundsPolicy.Freeze(input.Grounds).ContentDigest != receipt.GroundsDigest)
            throw new InvalidOperationException("decision-grounds-mismatch");
        var choice = output.GetProperty("activeChoice").Deserialize<DecisionChoiceV1>(DecisionGroundsPolicy.Json)
            ?? throw new InvalidOperationException("decision-choice-missing");
        var recorded = receipt.Chosen.LastOrDefault(item => !item.Outcome.StartsWith("rejected:",
            StringComparison.Ordinal));
        if (recorded is null || recorded.Action != choice.Action || recorded.Target != choice.Target)
            throw new InvalidOperationException("decision-choice-mismatch");
        if (receipt.Call.Status != DecisionCallStatuses.Completed)
        {
            if (choice.Action != DecisionActionKinds.Ask || choice.Target != "operator:ai")
                throw new InvalidOperationException("decision-degraded-must-ask");
        }
        else
        {
            if (receipt.Call.CallTokenCapExceeded)
                throw new InvalidOperationException("decision-call-cap-exceeded");
            var parsed = DecisionReceiptPolicy.Parse(receipt.ResponseContent, receipt.Grounds);
            if (!parsed.Valid || parsed.Chosen.Count != 1
                || parsed.Chosen[0].Action != choice.Action || parsed.Chosen[0].Target != choice.Target)
                throw new InvalidOperationException("decision-response-mismatch");
        }
        var offered = receipt.Grounds.Alternatives.SingleOrDefault(item => item.Action == choice.Action
            && item.Target == choice.Target);
        if (receipt.Call.Status == DecisionCallStatuses.Completed
            && (offered is not { Available: true }
                || offered.RequiredApproval is not ("A0" or "A1")))
            throw new InvalidOperationException("decision-forbidden-action");

        var run = authority.GetRun(occurrence.AuthorityAttemptId);
        var taskKey = run?.TaskKey ?? task.Id;
        var projection = authority.GetTaskProjection(taskKey);
        var replayingSalvageMove = choice is { Action: DecisionActionKinds.Adapt,
                Target: "salvage-handoff" }
            && task.State == TaskStates.Ready
            && EngineDecisionWaitMarker.Read(task.FolderPath) == occurrence.Run.RunId;
        if (task.State != occurrence.ExpectedSourceState && !replayingSalvageMove
            || projection.AuthorityEpoch != occurrence.AuthorityEpoch
            || run is null || run.AttemptId != occurrence.AuthorityAttemptId
            || authority.CurrentFence(taskKey) != occurrence.AuthorityFence
            || run.LastFence != occurrence.AuthorityFence
            || run.AuthorityEpoch != occurrence.AuthorityEpoch)
            throw new InvalidOperationException("decision-stale-authority");
        using var payload = JsonDocument.Parse(occurrence.Run.PayloadJson);
        var expectedSha = payload.RootElement.GetProperty("resultSha").GetString();
        var groundedSha = receipt.Grounds.Facts.SingleOrDefault(fact => fact.Id == "result.sha")?.Value;
        if (run.ResultSha != expectedSha || groundedSha != (expectedSha ?? "missing")
            || run.ResultEnvelope is { } envelope && (envelope.ResultSha != expectedSha
                || envelope.ImmutableRemoteRef != payload.RootElement.GetProperty("resultRef").GetString()))
            throw new InvalidOperationException("decision-result-sha-mismatch");
        if (choice.Action is DecisionActionKinds.Prepare or DecisionActionKinds.Adapt
            && (string.IsNullOrWhiteSpace(expectedSha) || run.ResultEnvelope is null))
            throw new InvalidOperationException("decision-result-envelope-required");
        if (choice is { Action: DecisionActionKinds.Prepare, Target: "review-subject" }
            && (!payload.RootElement.TryGetProperty("reviewAttemptRequest", out var request)
                || request.ValueKind != JsonValueKind.Object
                || !payload.RootElement.TryGetProperty("reviewSubject", out var subject)
                || subject.ValueKind != JsonValueKind.Object
                || !(subject.TryGetProperty("resultSha", out var subjectSha)
                    || subject.TryGetProperty("ResultSha", out subjectSha))
                || subjectSha.GetString() != expectedSha))
            throw new InvalidOperationException("decision-review-subject-mismatch");
        var expectedTransition = MonolithTransitionAdapter.ActiveCodingTransition(choice, taskKey,
            occurrence.AuthorityAttemptId);
        var expectedStageAction = expectedTransition is null ? OrchestrationAction.Continue
            : StageDecisionPolicy.ToStageAction(expectedTransition.Action);
        if (stageAction != expectedStageAction)
            throw new InvalidOperationException("decision-stage-action-mismatch");
        var actualTransition = output.TryGetProperty("transition", out var transition)
            && transition.ValueKind == JsonValueKind.Object
            ? transition.Deserialize<TransitionDecision>(DecisionGroundsPolicy.Json) : null;
        if (actualTransition?.Action != expectedTransition?.Action)
            throw new InvalidOperationException("decision-transition-mismatch");
        return choice;
    }

    public string VerifiedTree(string occurrenceId)
    {
        var input = decisions.GetInputs(occurrenceId)
            ?? throw new InvalidOperationException("decision-grounds-missing");
        var sha = input.Grounds.Progress.LastVerifiedTreeSha;
        if (string.IsNullOrWhiteSpace(sha))
            throw new InvalidOperationException("decision-verified-tree-missing");
        return sha;
    }
}
