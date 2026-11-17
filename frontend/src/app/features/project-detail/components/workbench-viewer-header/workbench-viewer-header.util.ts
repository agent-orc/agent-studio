import type { TaskReferenceStatus } from '../../../../components/task-reference-microcard/task-reference-microcard';
import type { WorkbenchDocument } from '../../../../models/project-docs.model';
import { TaskState } from '../../../../models/task.model';

export function uniqueKeys(keys: readonly string[]): string[] {
  const result: string[] = [];
  const seen = new Set<string>();
  for (const key of keys) {
    const normalized = normalizeKey(key);
    if (!normalized || seen.has(normalized)) continue;
    seen.add(normalized);
    result.push(key.trim());
  }
  return result;
}

export function normalizeKey(value: string | null | undefined): string {
  return (value ?? '').trim().toUpperCase();
}

/**
 * Keys of the concept card that wrote and revises this Dossier. It is linked
 * like any other card, but it is not an implementation slice (AGT-3649-D13).
 */
export function conceptCardKeys(document: WorkbenchDocument): ReadonlySet<string> {
  return new Set([
    ...document.workbench.sourceTaskKeys,
    document.revisionCard?.key ?? '',
  ].map(normalizeKey).filter(Boolean));
}

/** Adds the Dossier's own member cards the reference index has not resolved. */
export function withDossierCards(
  references: readonly TaskReferenceStatus[],
  document: WorkbenchDocument,
  projectName: string,
): TaskReferenceStatus[] {
  const known = new Set(references.map((task) => normalizeKey(task.key)));
  const members = (document.cards?.cards ?? [])
    .filter((card) => !known.has(normalizeKey(card.key)))
    .map((card) => ({ ...ghostStatus(card.key, projectName), exists: true, lane: card.lane }));
  return [...references, ...members];
}

export function implementationStatusFor(
  workbenchStatus: string,
  allReferences: readonly TaskReferenceStatus[],
  conceptKeys: ReadonlySet<string> = new Set(),
): string | null {
  if (workbenchStatus !== 'decision-pending' && workbenchStatus !== 'decided') return null;
  const references = allReferences.filter((task) => !conceptKeys.has(normalizeKey(task.key)));
  const known = references.filter((task) => task.exists && task.lane);
  if (known.length === 0) return null;
  const allTerminal = references.every(
    (task) => task.exists && task.lane !== null && isTerminalLane(task.lane),
  );
  return !allTerminal && known.some((task) => hasImplementationStarted(task.lane))
    ? 'In implementation'
    : null;
}

function isTerminalLane(lane: string | null): boolean {
  return lane === TaskState.Completed || lane === TaskState.Archive;
}

function hasImplementationStarted(lane: string | null): boolean {
  return lane !== null
    && lane !== TaskState.Backlog
    && lane !== TaskState.Preparation
    && lane !== TaskState.Ready;
}

export function ghostStatus(key: string, projectName: string): TaskReferenceStatus {
  return {
    key,
    exists: false,
    taskKey: null,
    title: null,
    lane: null,
    projectId: '',
    projectName,
    projectColor: null,
    merge: null,
    reviewGrade: null,
  };
}
