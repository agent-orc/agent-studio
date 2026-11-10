import { laneName } from '../../../../models/lane-presentation';
import {
  WorkbenchDecisionPoint,
  WorkbenchDecisionResponse,
  WorkbenchDocument,
  WorkbenchRevisionRound,
  WorkbenchTaskDraft,
} from '../../../../models/project-docs.model';

export function selectedDecisionText(
  points: readonly WorkbenchDecisionPoint[],
  responses: readonly WorkbenchDecisionResponse[],
): string {
  const responseById = new Map(responses.map(response => [response.decisionId, response]));
  return points.flatMap(point => {
    const response = responseById.get(point.id);
    if (!response) return [];
    const selected = new Set(response.selectedOptionIds);
    const labels = point.options.filter(option => selected.has(option.id)).map(option => option.label);
    const choice = `${point.label}: ${labels.join(', ') || 'No option selected'}`;
    return [response.comment ? `${choice}. Note: ${response.comment}` : choice];
  }).join('\n');
}

export function cardPrompt(
  document: WorkbenchDocument,
  draft: WorkbenchTaskDraft,
  points: readonly WorkbenchDecisionPoint[],
  responses: readonly WorkbenchDecisionResponse[],
): string {
  return [
    '# Dossier-backed feature',
    '',
    `Source: \`${document.workbench.entryPath}\``,
    '',
    '## Goal',
    '',
    draft.goal,
    '',
    '## Recorded decisions',
    '',
    selectedDecisionText(points, responses) || '(No inline decision points were present.)',
    '',
    '## Acceptance criteria',
    '',
    ...draft.acceptanceCriteria.map(item => `- ${item}`),
  ].join('\n');
}

export function reworkPrompt(
  document: WorkbenchDocument,
  points: readonly WorkbenchDecisionPoint[],
  responses: readonly WorkbenchDecisionResponse[],
  request = '',
): string {
  const selections = selectedDecisionText(points, responses);
  return [
    `Revise Dossier ${document.workbench.key ?? document.workbench.id}: ${document.workbench.title}.`,
    '',
    'The owner requested a new Dossier revision instead of accepting the current decision.',
    `Source: \`${document.workbench.entryPath}\``,
    'Revise both index.html and workbench.json in the project repository through the normal delivery path.',
    'Keep the decision open, preserve lifecycle history, and keep the pending rework receipt in workbench.json: the Dossier reads the owner\'s recorded selections from it.',
    'In the revised inline decision markup, mark the owner\'s recorded selections below as the checked options instead of the authored recommendations.',
    ...(request.trim() ? ['', 'Owner request:', request.trim()] : []),
    ...(selections ? ['', 'Owner selections:', selections] : []),
  ].join('\n');
}

/**
 * After a revision lands, the owner's recorded selections win over the authored
 * defaults wherever the revised markup still offers them. A point that is new,
 * changed its kind, or dropped a recorded option keeps the authored default.
 * Comments belonged to the rework request and are not carried forward.
 */
export function carryRecordedSelections(
  recorded: readonly WorkbenchDecisionResponse[] | null | undefined,
  points: readonly WorkbenchDecisionPoint[],
  authored: readonly WorkbenchDecisionResponse[],
): WorkbenchDecisionResponse[] {
  const byId = new Map((recorded ?? []).map(response => [response.decisionId, response]));
  return authored.map(response => {
    const point = points.find(candidate => candidate.id === response.decisionId);
    const previous = byId.get(response.decisionId);
    if (!point || !previous || previous.kind !== point.kind || previous.selectedOptionIds.length === 0)
      return response;
    const offered = new Set(point.options.map(option => option.id));
    if (!previous.selectedOptionIds.every(id => offered.has(id))) return response;
    return { ...response, selectedOptionIds: [...previous.selectedOptionIds] };
  });
}

/** One readable line for the follow-up round a rework request started or queued. */
export function revisionRoundText(round: WorkbenchRevisionRound): string {
  const card = round.title ? `${round.taskKey} · ${round.title}` : round.taskKey;
  switch (round.status) {
    case 'started': return `Revision round started on ${card}.`;
    case 'queued': return `Revision round queued on ${card}. ${round.detail ?? ''}`.trim();
    case 'saved': return `Revision request saved on ${card}. ${round.detail ?? ''}`.trim();
    default: return `The rework request is recorded, but the revision round on ${card} could not start: ${round.detail ?? 'unknown reason'}`;
  }
}

export function taskKeyTail(taskKey: string): string {
  return taskKey.includes('::') ? taskKey.slice(taskKey.lastIndexOf('::') + 2) : taskKey;
}

export function bounded(value: string, length: number): string {
  return value.length <= length ? value : value.slice(0, length);
}

export function laneLabel(lane: string | null): string {
  return lane ? laneName(lane) : 'Unknown lane';
}

export function actionErrorMessage(error: unknown): string {
  const candidate = error as { error?: { error?: string } | string; message?: string } | null;
  if (typeof candidate?.error === 'string') return candidate.error;
  if (candidate?.error && typeof candidate.error.error === 'string') return candidate.error.error;
  return candidate?.message || 'The Dossier decision action could not be completed.';
}

export function createOperationId(): string {
  const random = globalThis.crypto?.randomUUID?.()
    ?? `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
  return `workbench-ui-${random}`;
}
