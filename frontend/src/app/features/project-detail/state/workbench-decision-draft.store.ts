import { Injectable, signal } from '@angular/core';
import { WorkbenchDecisionResponse } from '../../../models/project-docs.model';

const STORAGE_KEY = 'atp.workbench-decision-drafts.v1';

export interface WorkbenchDecisionDraftCard {
  id?: string;
  watchPath?: string;
  key: string;
  taskKey: string;
  title: string;
  lane: string;
}

export interface WorkbenchDecisionDraft {
  mode: 'feature-spawn' | 'rework' | null;
  actor: string;
  title: string;
  goal: string;
  operationId: string | null;
  responses: WorkbenchDecisionResponse[];
  createdCard: WorkbenchDecisionDraftCard | null;
  cliType?: string;
  model?: string;
  thinkingLevel?: string | null;
  /** The owner's free-text rework request (AGT-3703). */
  request?: string;
  /** Set once the owner was warned about leaving these answers unsaved (AGT-3875). */
  leaveWarned?: boolean;
  updatedAt: string;
}

@Injectable({ providedIn: 'root' })
export class WorkbenchDecisionDraftStore {
  private readonly entries = signal<Record<string, WorkbenchDecisionDraft>>(readEntries());

  draft(projectName: string, workbenchId: string): WorkbenchDecisionDraft | null {
    return this.entries()[entryKey(projectName, workbenchId)] ?? null;
  }

  saveResponses(
    projectName: string,
    workbenchId: string,
    responses: readonly WorkbenchDecisionResponse[],
  ): void {
    // A changed answer is a new unsaved state, so leaving warns again.
    this.patch(projectName, workbenchId, {
      responses: responses.map(copyResponse),
      leaveWarned: false,
    });
  }

  markLeaveWarned(projectName: string, workbenchId: string): void {
    if (!this.draft(projectName, workbenchId)) return;
    this.patch(projectName, workbenchId, { leaveWarned: true });
  }

  beginFeature(
    projectName: string,
    workbenchId: string,
    defaults: Pick<WorkbenchDecisionDraft, 'actor' | 'title' | 'goal' | 'operationId'>,
    responses: readonly WorkbenchDecisionResponse[],
  ): WorkbenchDecisionDraft {
    const current = this.draft(projectName, workbenchId);
    const draft = this.patch(projectName, workbenchId, {
      mode: 'feature-spawn',
      actor: current?.mode === 'feature-spawn' ? current.actor : defaults.actor,
      title: current?.mode === 'feature-spawn' ? current.title : defaults.title,
      goal: current?.mode === 'feature-spawn' ? current.goal : defaults.goal,
      operationId: current?.operationId ?? defaults.operationId,
      responses: responses.map(copyResponse),
    });
    return draft;
  }

  beginRework(
    projectName: string,
    workbenchId: string,
    defaults: Pick<WorkbenchDecisionDraft, 'actor' | 'operationId' | 'cliType' | 'model' | 'thinkingLevel'>,
    responses: readonly WorkbenchDecisionResponse[],
  ): WorkbenchDecisionDraft {
    const current = this.draft(projectName, workbenchId);
    return this.patch(projectName, workbenchId, {
      mode: 'rework',
      actor: current?.mode === 'rework' ? current.actor : defaults.actor,
      operationId: current?.operationId ?? defaults.operationId,
      responses: responses.map(copyResponse),
      cliType: current?.mode === 'rework' ? current.cliType : defaults.cliType,
      model: current?.mode === 'rework' ? current.model : defaults.model,
      thinkingLevel: current?.mode === 'rework' ? current.thinkingLevel : defaults.thinkingLevel,
    });
  }

  updateFeature(
    projectName: string,
    workbenchId: string,
    patch: Partial<Pick<WorkbenchDecisionDraft, 'actor' | 'title' | 'goal' | 'operationId'>>,
  ): void {
    this.patch(projectName, workbenchId, patch);
  }

  updateAction(
    projectName: string,
    workbenchId: string,
    patch: Partial<Pick<WorkbenchDecisionDraft,
      'actor' | 'title' | 'goal' | 'operationId' | 'cliType' | 'model' | 'thinkingLevel' | 'request'>>,
  ): void {
    this.patch(projectName, workbenchId, patch);
  }

  closeAction(projectName: string, workbenchId: string): void {
    this.patch(projectName, workbenchId, { mode: null, operationId: null });
  }

  rememberCreatedCard(
    projectName: string,
    workbenchId: string,
    card: WorkbenchDecisionDraftCard,
  ): void {
    this.patch(projectName, workbenchId, { createdCard: { ...card } });
  }

  clearCreatedCard(projectName: string, workbenchId: string): void {
    this.patch(projectName, workbenchId, { createdCard: null });
  }

  discard(projectName: string, workbenchId: string): void {
    const key = entryKey(projectName, workbenchId);
    this.entries.update(entries => {
      if (!entries[key]) return entries;
      const next = { ...entries };
      delete next[key];
      persistEntries(next);
      return next;
    });
  }

  private patch(
    projectName: string,
    workbenchId: string,
    patch: Partial<WorkbenchDecisionDraft>,
  ): WorkbenchDecisionDraft {
    const key = entryKey(projectName, workbenchId);
    const current = this.entries()[key] ?? emptyDraft();
    const next = { ...current, ...patch, updatedAt: new Date().toISOString() };
    this.entries.update(entries => {
      const updated = { ...entries, [key]: next };
      persistEntries(updated);
      return updated;
    });
    return next;
  }
}

function emptyDraft(): WorkbenchDecisionDraft {
  return {
    mode: null,
    actor: 'Operator',
    title: '',
    goal: '',
    operationId: null,
    responses: [],
    createdCard: null,
    cliType: readDefaultCli(),
    model: readDefaultModel(readDefaultCli()),
    thinkingLevel: readDefaultThinkingLevel(readDefaultCli()),
    updatedAt: new Date().toISOString(),
  };
}

function copyResponse(response: WorkbenchDecisionResponse): WorkbenchDecisionResponse {
  return { ...response, selectedOptionIds: [...response.selectedOptionIds] };
}

function entryKey(projectName: string, workbenchId: string): string {
  return `${encodeURIComponent(projectName)}:${encodeURIComponent(workbenchId)}`;
}

/**
 * Drafts live in localStorage so unsaved answers survive closing the tab
 * (AGT-3875). A draft an older build left in sessionStorage moves over once.
 */
function readEntries(): Record<string, WorkbenchDecisionDraft> {
  const entries = { ...parseEntries(readStorage('sessionStorage')), ...parseEntries(readStorage('localStorage')) };
  try { globalThis.sessionStorage?.removeItem(STORAGE_KEY); } catch { /* storage unavailable */ }
  if (Object.keys(entries).length > 0) persistEntries(entries);
  return entries;
}

function readStorage(kind: 'localStorage' | 'sessionStorage'): string | null {
  try { return globalThis[kind]?.getItem(STORAGE_KEY) ?? null; }
  catch { return null; }
}

function parseEntries(value: string | null): Record<string, WorkbenchDecisionDraft> {
  try {
    if (!value) return {};
    const parsed = JSON.parse(value) as { version?: unknown; entries?: unknown };
    if (parsed.version !== 1 || !isRecord(parsed.entries)) return {};
    return Object.fromEntries(Object.entries(parsed.entries).flatMap(([key, draft]) =>
      isDraft(draft) ? [[key, draft]] : []));
  } catch {
    return {};
  }
}

function persistEntries(entries: Record<string, WorkbenchDecisionDraft>): void {
  try {
    globalThis.localStorage?.setItem(STORAGE_KEY, JSON.stringify({ version: 1, entries }));
  } catch {
    // Browser storage is a resilience aid. The in-memory draft remains usable.
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isDraft(value: unknown): value is WorkbenchDecisionDraft {
  if (!isRecord(value)) return false;
  return (value['mode'] === null || value['mode'] === 'feature-spawn' || value['mode'] === 'rework')
    && typeof value['actor'] === 'string'
    && typeof value['title'] === 'string'
    && typeof value['goal'] === 'string'
    && (value['operationId'] === null || typeof value['operationId'] === 'string')
    && Array.isArray(value['responses'])
    && (value['createdCard'] === null || isRecord(value['createdCard']))
    && typeof value['updatedAt'] === 'string';
}

function readDefaultCli(): string {
  try { return globalThis.localStorage?.getItem('defaultCliType') || 'claude'; }
  catch { return 'claude'; }
}

function readDefaultModel(cliType: string): string {
  try { return globalThis.localStorage?.getItem(`defaultModel:${cliType}`) || ''; }
  catch { return ''; }
}

function readDefaultThinkingLevel(cliType: string): string | null {
  try { return globalThis.localStorage?.getItem(`defaultThinkingLevel:${cliType}`) || null; }
  catch { return null; }
}
