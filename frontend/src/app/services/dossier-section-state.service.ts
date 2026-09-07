import { Injectable, signal } from '@angular/core';

export type DossierSectionId =
  | 'needs-decision'
  | 'current'
  | 'needs-attention'
  | 'history'
  | 'documented'
  | 'discarded';

interface DossierSectionState {
  collapsed: boolean;
  hadItems: boolean;
}

/**
 * Project-scoped disclosure state shared by the Dossier overview and Explorer.
 * Item presence is stored with the preference so an empty section that receives
 * a new item can open once without forgetting the operator's later choice.
 */
@Injectable({ providedIn: 'root' })
export class DossierSectionStateService {
  private readonly states = signal<Record<string, DossierSectionState>>({});

  expanded(projectId: string, section: DossierSectionId): boolean {
    return !(this.states()[storageKey(projectId, section)] ?? readState(projectId, section)).collapsed;
  }

  setExpanded(projectId: string, section: DossierSectionId, expanded: boolean): void {
    const key = storageKey(projectId, section);
    const current = this.states()[key] ?? readState(projectId, section);
    this.store(key, { ...current, collapsed: !expanded });
  }

  observeItems(projectId: string, section: DossierSectionId, itemCount: number): void {
    const key = storageKey(projectId, section);
    const current = this.states()[key] ?? readState(projectId, section);
    const hasItems = itemCount > 0;
    const next = {
      collapsed: !current.hadItems && hasItems ? false : current.collapsed,
      hadItems: hasItems,
    };
    if (next.collapsed === current.collapsed && next.hadItems === current.hadItems
      && this.states()[key]) return;
    this.store(key, next);
  }

  collapse(projectId: string, sections: readonly DossierSectionId[]): void {
    for (const section of sections) this.setExpanded(projectId, section, false);
  }

  private store(key: string, state: DossierSectionState): void {
    this.states.update(states => ({ ...states, [key]: state }));
    try {
      globalThis.localStorage?.setItem(key, JSON.stringify(state));
    } catch {
      /* Storage can be unavailable or full; the in-memory preference still works. */
    }
  }
}

export function dossierSectionStorageKey(projectId: string, section: DossierSectionId): string {
  return storageKey(projectId, section);
}

function storageKey(projectId: string, section: DossierSectionId): string {
  return `dossier-overview:${projectId}:${section}`;
}

function readState(projectId: string, section: DossierSectionId): DossierSectionState {
  try {
    const parsed = JSON.parse(globalThis.localStorage?.getItem(storageKey(projectId, section)) ?? 'null') as unknown;
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return defaultState();
    const record = parsed as Record<string, unknown>;
    return {
      collapsed: record['collapsed'] === true,
      hadItems: record['hadItems'] === true,
    };
  } catch {
    return defaultState();
  }
}

function defaultState(): DossierSectionState {
  return { collapsed: false, hadItems: false };
}
