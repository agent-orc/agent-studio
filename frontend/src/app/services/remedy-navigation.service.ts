import { Injectable, inject } from '@angular/core';
import { REMEDY_ROUTES, type RemedyAction } from '../models/remedy-action.model';
import { FeatureFlagsService } from './feature-flags.service';
import { TaskReferenceNavigationService } from './task-reference-navigation.service';
// Concrete module paths, not the feature barrels: a barrel would pull the
// shell host components into a root service they render (see
// DossierReferenceNavigationService for the same seam).
// eslint-disable-next-line no-restricted-imports
import { StudioTabStateService } from '../features/studio-shell/services/studio-tab-state.service';
// eslint-disable-next-line no-restricted-imports
import { WorkspaceOverlaysService } from '../features/shell/state/workspace-overlays.service';

/**
 * AGT-3580 - where a remedy link goes. A readiness remedy or a pickup-hold way
 * out names the owning project setting or Studio surface; this service is the
 * one place that turns that typed action into navigation.
 */
@Injectable({ providedIn: 'root' })
export class RemedyNavigationService {
  private readonly tabs = inject(StudioTabStateService);
  private readonly overlays = inject(WorkspaceOverlaysService);
  private readonly flags = inject(FeatureFlagsService);
  private readonly references = inject(TaskReferenceNavigationService);

  /** True when `open` can navigate for this action; API actions are buttons, not links. */
  canOpen(action: RemedyAction | null | undefined): boolean {
    if (!action) return false;
    if (action.kind === 'setting') return Boolean(action.project && action.target);
    if (action.kind !== 'route') return false;
    if (action.target === REMEDY_ROUTES.task) return Boolean(action.taskKey);
    return action.target === REMEDY_ROUTES.executionHosts || action.target === REMEDY_ROUTES.gitHubSettings;
  }

  /** Navigate for a route or setting action. Returns false when nothing was opened. */
  open(action: RemedyAction | null | undefined): boolean {
    if (!action || !this.canOpen(action)) return false;
    if (action.kind === 'setting') {
      this.openProjectSetting(action.project!, action.target);
      return true;
    }
    switch (action.target) {
      case REMEDY_ROUTES.task:
        return this.references.openReferenceOrNotify(action.taskKey!);
      case REMEDY_ROUTES.executionHosts:
        this.openWorkspaceSection('remote-hosts');
        return true;
      case REMEDY_ROUTES.gitHubSettings:
        this.openWorkspaceSection('github');
        return true;
      default:
        return false;
    }
  }

  /** Open the project's Settings rail focused on the field marked `data-setting-key`. */
  openProjectSetting(projectName: string, settingKey: string): void {
    this.overlays.close();
    this.tabs.open({ kind: 'hub', projectName, section: 'settings', settingKey });
  }

  private openWorkspaceSection(section: 'remote-hosts' | 'github'): void {
    this.overlays.open(section);
    if (this.flags.vsCodeLayout()) this.tabs.open({ kind: 'workspace-settings' });
  }
}
