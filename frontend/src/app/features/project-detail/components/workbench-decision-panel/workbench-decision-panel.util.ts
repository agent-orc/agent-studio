import { laneName } from '../../../../models/lane-presentation';
import {
  WorkbenchDecisionPoint,
  WorkbenchDecisionResponse,
  WorkbenchDocument,
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
): string {
  return [
    `Revise Dossier ${document.workbench.key ?? document.workbench.id}: ${document.workbench.title}.`,
    '',
    'The operator requested a new Dossier revision instead of accepting the current decision.',
    'Revise both index.html and workbench.json in the project repository through the normal orchestrator delivery path.',
    'Keep the decision open, preserve lifecycle history, remove the pending rework receipt after the new revision is delivered, and update the inline decision markup to match the revision.',
    '',
    'Operator response:',
    selectedDecisionText(points, responses),
  ].join('\n');
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
