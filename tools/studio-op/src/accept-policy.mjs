/**
 * Decision OA-D5 (Dossier operator-agent-level): the operator agent accepts a
 * card only with green evidence and no open decision. Red, missing or
 * disputed evidence and decision cards stay with the Owner.
 *
 * Pure: it reads the card (GET /api/tasks/{id}) and its review projection
 * (GET /api/tasks/{id}/review-projection), the same projection the Evidence
 * tab and the escalation banner render, and returns every reason that
 * refuses the acceptance. The server still enforces its own acceptance rules
 * (integration proof) on the lane move.
 */
const HUMAN_REVIEW = '5-human-review';
const REMOTE_PASS = 'pass';
const LOCAL_DISPUTED = new Set(['concerns']);
const OPEN_VERDICTS = new Set(['escalate', 'pending']);
// Every card in Human Review carries a park marker and a `lane-change` or
// `parked-blocker` decision: that is the acceptance itself, which OA-D5 gives
// the operator. Like the server's acceptance rail (AcceptanceRailFacts.IsHeld),
// only these blocker types are a decision someone else must make first.
const DECISION_BLOCKERS = new Set(['human-decision-needed', 'agent-needs-input', 'needs-human-input', 'steer-unanswered']);
// An escalation recorded on the timeline is disputed until the Owner resolves it.
const ESCALATION_SOURCE = 'escalation-event';

export function evaluateAcceptance(card, projection) {
  const reasons = [];
  const refuse = (code, detail) => reasons.push({ code, detail });

  if (card.state !== HUMAN_REVIEW)
    refuse('not-in-human-review', `The card is in ${card.state}; only a card in ${HUMAN_REVIEW} can be accepted.`);
  if (String(card.kind ?? '').toLowerCase() === 'decision')
    refuse('decision-card', 'Decision cards stay with the Owner.');

  const decisions = openDecisions(card, projection);
  if (decisions.length > 0) refuse('decision-open', decisions.join(' '));

  const attempts = projection?.attempts ?? [];
  const latest = attempts[0];
  if (!latest) {
    refuse('evidence-missing', 'No review attempt is recorded for this card.');
  } else {
    const outcome = String(latest.outcome ?? '').toLowerCase();
    const evidence = [];
    if (LOCAL_DISPUTED.has(outcome))
      reasons.push({ code: 'evidence-disputed', detail: `The latest review (${latest.attemptId}) reports concerns.` });
    else if (outcome !== REMOTE_PASS)
      evidence.push(`The latest review (${latest.attemptId}) has outcome ${latest.outcome ?? 'none'}.`);
    if (latest.buildTestsResult === 'failed')
      evidence.push(`Build and tests failed in ${latest.attemptId}.`);
    for (const aspect of projection.blockingAspects ?? [])
      evidence.push(`Aspect ${aspect.aspect} blocks: ${aspect.reason}`);
    if (projection.delivery?.status === 'gate-failed')
      evidence.push(`The delivery gate failed${projection.delivery.reason ? `: ${projection.delivery.reason}` : '.'}`);
    if (card.orchestratorVerdict === 'reissue')
      evidence.push('The orchestrator verdict is reissue.');
    if (evidence.length > 0) refuse('evidence-red', evidence.join(' '));
    else if (latest.buildTestsResult !== 'passed' && !reasons.some(reason => reason.code === 'evidence-disputed'))
      refuse('evidence-missing', `Build and test evidence is not proven in ${latest.attemptId}.`);
  }

  return {
    acceptable: reasons.length === 0,
    reasons,
    evidence: latest ? {
      attemptId: latest.attemptId,
      plane: latest.plane,
      outcome: latest.outcome ?? null,
      buildTests: latest.buildTestsResult ?? null,
      subjectSha: latest.subjectSha ?? null,
      reportRef: latest.reportRef ?? null,
    } : null,
  };
}

function openDecisions(card, projection) {
  const open = [];
  if (DECISION_BLOCKERS.has(card.parkedBlocker?.blockerType))
    open.push(`The card is parked on ${card.parkedBlocker.blockerType ?? 'a blocker'}${card.parkedBlocker.reason ? `: ${card.parkedBlocker.reason}` : '.'}`);
  if (card.needsInput)
    open.push(`The agent needs input: ${card.needsInput.firstLine ?? card.needsInput.message ?? 'see the card'}.`);
  if (projection?.decisionRequired?.required && projection.decisionRequired.source === ESCALATION_SOURCE)
    open.push(`A decision is pending (${projection.decisionRequired.source ?? 'review'})${projection.decisionRequired.reason ? `: ${projection.decisionRequired.reason}` : '.'}`);
  if (OPEN_VERDICTS.has(card.orchestratorVerdict))
    open.push(`The orchestrator verdict is ${card.orchestratorVerdict}.`);
  if (card.steerPendingSince)
    open.push(`A steer has waited for an answer since ${card.steerPendingSince}.`);
  return open;
}
