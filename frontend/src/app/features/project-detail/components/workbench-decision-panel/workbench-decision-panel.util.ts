import type { StatusTone } from '../../../../components/status-indicator/status-indicator.component';
import { laneName } from '../../../../models/lane-presentation';
import { HttpErrorResponse } from '@angular/common/http';
import { CLI_TYPES, CliType } from '../../../../models/task.model';
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
    const choice = `${point.label}: ${labels.join(', ') || 'Still open'}`;
    return [response.comment ? `${choice}. Note: ${response.comment}` : choice];
  }).join('\n');
}

export function answeredDecisionCount(
  points: readonly WorkbenchDecisionPoint[], responses: readonly WorkbenchDecisionResponse[],
): number {
  const byId = new Map(responses.map(response => [response.decisionId, response]));
  return points.filter(point => (byId.get(point.id)?.selectedOptionIds.length ?? 0) > 0).length;
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

export function featureTaskDraft(
  document: WorkbenchDocument, projectName: string, title: string,
  goal: string, selectedSummary: string,
): WorkbenchTaskDraft {
  return {
    title: title.trim() || `Implement ${document.workbench.title}`,
    goal: goal.trim(),
    acceptanceCriteria: [
      'Implement every recorded Dossier selection and preserve its stated constraints.',
      'Verify the resulting behavior with the checks required by the affected surface.',
    ],
    evidenceLinks: [document.workbench.entryPath],
    chosenOption: bounded(selectedSummary, 2_000) || null,
    relatedTaskKeys: document.workbench.sourceTaskKeys,
    targetProject: projectName, initialLane: '2-ready', mode: 'coding', taskType: 'feature',
  };
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
    case 'updated': return `Revision round updated the Dossier on ${card}.`;
    case 'unchanged': return `Revision round finished on ${card}; the Dossier is unchanged.`;
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

export function readDefaultCli(): CliType {
  const value = readStorage('defaultCliType');
  return value && (CLI_TYPES as readonly string[]).includes(value) ? value as CliType : 'claude';
}

export function readDefaultModel(cli: CliType): string { return readStorage(`defaultModel:${cli}`) ?? ''; }
export function readDefaultThinkingLevel(cli: CliType): string | null {
  return readStorage(`defaultThinkingLevel:${cli}`);
}

function readStorage(key: string): string | null {
  try { return globalThis.localStorage?.getItem(key) ?? null; }
  catch { return null; }
}

/** Preserve the server's cleanup result when a newly created card was not linked. */
export function unlinkedCardFailure(
  card: { id?: string; key: string }, error: unknown,
): { removed: boolean; error: unknown } {
  const payload = (error as HttpErrorResponse | null)?.error as {
    removedTaskIds?: string[]; retainedTaskIds?: string[]; error?: string;
  } | null;
  if (card.id && payload?.removedTaskIds?.includes(card.id)) return { removed: true, error };
  if (card.id && payload?.retainedTaskIds?.includes(card.id)) return { removed: false, error };
  if (!card.id) return { removed: false, error: new Error(
    `The Dossier decision could not be saved. Task ${card.key} could not be checked, so it was not removed and may remain on the board without a Dossier link. Remove it before you try again.`) };
  if (payload?.error) return { removed: false, error };
  return { removed: false, error: new Error(
    `The Dossier decision could not be confirmed. Task ${card.key} was created and is kept for the retry. Try again to link it.`) };
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

const DECISION_STAGE_TONES: Readonly<Record<string, StatusTone>> = {
  prepared: 'info',
  pending: 'running',
  failed: 'danger',
  succeeded: 'success',
  archived: 'neutral',
};

/** Tone of the decision stage chip; a documented Dossier is done, no stage yet is an open fact. */
export function decisionStageTone(stage: string | null | undefined, documented: boolean): StatusTone {
  if (documented) return 'success';
  if (!stage) return 'info';
  return DECISION_STAGE_TONES[stage] ?? 'neutral';
}

const DECISION_OUTCOME_TONES: Readonly<Record<string, StatusTone>> = {
  'feature-spawn': 'success',
  archive: 'neutral',
  rework: 'warning',
};

/** Tone of a recorded decision receipt; an unknown outcome reads neutral. */
export function decisionOutcomeTone(outcome: string | null | undefined): StatusTone {
  return DECISION_OUTCOME_TONES[outcome ?? ''] ?? 'neutral';
}
