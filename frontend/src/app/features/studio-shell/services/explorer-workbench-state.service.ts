import { Injectable, inject, signal } from '@angular/core';
import { DossierSectionStateService } from '../../../services/dossier-section-state.service';
import { ProjectLookupService } from '../../../services/project-lookup.service';

export type ExplorerWorkbenchGroupId = 'needs-decision' | 'in-implementation' | 'history';

export interface ExplorerWorkbenchNavigationState {
  dossiersExpanded: boolean;
  groups: Record<ExplorerWorkbenchGroupId, boolean>;
}

const STORAGE_KEY = 'atp.studio.explorer.workbenches.state.v2';
const DEFAULT_STATE: ExplorerWorkbenchNavigationState = {
  dossiersExpanded: false,
  groups: {
    'needs-decision': true,
    'in-implementation': true,
    history: false,
  },
};

/**
 * Session-scoped state for each project's outer Dossiers navigation branch.
 * Lifecycle groups are also mirrored to the shared local section store so the
 * overview and Explorer keep the same project-scoped disclosure preference.
 * This stores presentation only; lifecycle remains owned by the catalogue.
 */
@Injectable({ providedIn: 'root' })
export class ExplorerWorkbenchStateService {
  private readonly sections = inject(DossierSectionStateService);
  private readonly projects = inject(ProjectLookupService);
  private readonly states = signal<Record<string, ExplorerWorkbenchNavigationState>>(readStates());

  stateFor(projectName: string): ExplorerWorkbenchNavigationState {
    return this.states()[projectName] ?? cloneDefaultState();
  }

  setDossiersExpanded(projectName: string, expanded: boolean): void {
    this.update(projectName, state => ({ ...state, dossiersExpanded: expanded }));
  }

  setGroupExpanded(projectName: string, group: ExplorerWorkbenchGroupId, expanded: boolean): void {
    this.sections.setExpanded(
      this.projects.getProjectDisplay(projectName).id ?? projectName,
      group === 'in-implementation' ? 'current' : group,
      expanded,
    );
    this.update(projectName, state => ({
      ...state,
      groups: { ...state.groups, [group]: expanded },
    }));
  }

  collapseAll(projectNames: readonly string[]): void {
    const next = { ...this.states() };
    for (const projectName of projectNames) {
      this.sections.collapse(this.projects.getProjectDisplay(projectName).id ?? projectName, [
        'needs-decision',
        'current',
        'history',
      ]);
      next[projectName] = {
        dossiersExpanded: false,
        groups: {
          'needs-decision': false,
          'in-implementation': false,
          history: false,
        },
      };
    }
    this.states.set(next);
    writeStates(next);
  }

  private update(
    projectName: string,
    updater: (state: ExplorerWorkbenchNavigationState) => ExplorerWorkbenchNavigationState,
  ): void {
    const next = {
      ...this.states(),
      [projectName]: updater(this.stateFor(projectName)),
    };
    this.states.set(next);
    writeStates(next);
  }
}

function cloneDefaultState(): ExplorerWorkbenchNavigationState {
  return { ...DEFAULT_STATE, groups: { ...DEFAULT_STATE.groups } };
}

function readStates(): Record<string, ExplorerWorkbenchNavigationState> {
  if (typeof window === 'undefined') return {};
  try {
    const parsed = JSON.parse(window.sessionStorage?.getItem(STORAGE_KEY) ?? '{}') as unknown;
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return {};
    const result: Record<string, ExplorerWorkbenchNavigationState> = {};
    for (const [projectName, value] of Object.entries(parsed as Record<string, unknown>)) {
      if (!value || typeof value !== 'object' || Array.isArray(value)) continue;
      const record = value as Record<string, unknown>;
      const groups = record['groups'];
      if (!groups || typeof groups !== 'object' || Array.isArray(groups)) continue;
      const groupRecord = groups as Record<string, unknown>;
      result[projectName] = {
        dossiersExpanded: record['dossiersExpanded'] === true,
        groups: {
          'needs-decision': groupRecord['needs-decision'] === true,
          'in-implementation': groupRecord['in-implementation'] === true,
          history: groupRecord['history'] === true,
        },
      };
    }
    return result;
  } catch {
    return {};
  }
}

function writeStates(states: Record<string, ExplorerWorkbenchNavigationState>): void {
  if (typeof window === 'undefined') return;
  try {
    window.sessionStorage?.setItem(STORAGE_KEY, JSON.stringify(states));
  } catch {
    /* Session storage may be full or blocked. */
  }
}
