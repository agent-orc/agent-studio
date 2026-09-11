import { Injectable, effect, inject, signal, untracked } from '@angular/core';
import {
  DEFAULT_PROJECT_RAIL_KEY,
  isProjectHubRoute,
  isProjectRailKey,
  parseProjectHubRoute,
  projectHubRoute,
  type ProjectHubRouteProject,
  withProjectHubRoute,
} from '../../project-detail';
import { WorkspaceManagerService } from '../../shell';
import { FeatureFlagsService } from '../../../services/feature-flags.service';
import { TaskService } from '../../../services/task.service';
import { withRouteSegment } from '../../../services/url-hash.util';
import type { StudioTab, WikiTabTarget } from '../studio-shell.types';
import { StudioTabStateService } from './studio-tab-state.service';

/**
 * Owns the canonical Project Hub route in Studio mode. Keeping registry
 * resolution and browser-history coordination out of App prevents the root
 * component from becoming the system of record for another feature.
 */
@Injectable({ providedIn: 'root' })
export class ProjectHubUrlService {
  private readonly featureFlags = inject(FeatureFlagsService);
  private readonly tabs = inject(StudioTabStateService);
  private readonly tasks = inject(TaskService);
  private readonly workspaceManager = inject(WorkspaceManagerService);

  private readonly projects = signal<readonly ProjectHubRouteProject[]>([]);
  private readonly registryLoaded = signal(false);
  private readonly started = signal(false);
  readonly appliedRevision = signal(0);
  private routeInitialized = false;

  constructor() {
    effect(() => {
      const studio = this.featureFlags.vsCodeLayout();
      const started = this.started();
      const loaded = this.registryLoaded();
      this.projects();
      const tab = this.tabs.activeTab();
      if (!studio || !started || !loaded) return;
      untracked(() => {
        if (!this.routeInitialized) {
          this.routeInitialized = true;
          if (this.applyHash(false)) return;
        }
        this.publish(tab);
      });
    });

    effect(() => {
      const revision = this.workspaceManager.registryChanged();
      if (revision === 0 || !this.started()) return;
      untracked(() => this.loadRegistry());
    });
  }

  start(): void {
    this.started.set(true);
    this.loadRegistry();
  }

  stop(): void {
    this.started.set(false);
    this.routeInitialized = false;
  }

  projectIdForName(projectName: string): string | null {
    return this.projects().find(project => project.displayName === projectName)?.id ?? null;
  }

  /** Open one exact repository document in the project's in-app Wiki reader. */
  openWikiPage(projectName: string, relPath: string): boolean {
    const project = this.projects().find(candidate => candidate.displayName === projectName);
    const page = relPath.trim().replace(/^docs\//i, '');
    if (!project || !page) return false;
    this.tabs.open({
      kind: 'hub',
      projectName: project.displayName,
      section: 'wiki',
      wikiTarget: { kind: 'page', relPath: page },
    });
    return true;
  }

  /** Resolve the current hash into the Studio's in-editor Project Hub tab. */
  applyHash(closeWhenMissing: boolean): boolean {
    if (!this.registryLoaded()) return false;
    const target = parseProjectHubRoute(window.location.hash, this.projects());
    if (!target) {
      if (closeWhenMissing && this.tabs.activeTab()?.kind === 'hub') {
        this.tabs.activateAllProjectsBoard();
      }
      return false;
    }

    if (target.legacySlug) {
      const canonicalRoute = target.section === 'workbenches'
        ? projectDossierRoute(target.project.id, target.workbenchId) + target.query
        : projectHubRoute(target.project.id, target.section) + target.query;
      this.writeHash(withRouteSegment(window.location.hash, canonicalRoute), 'replace');
    }

    if (target.workbenchId) {
      this.tabs.open({
        kind: 'workbench', projectName: target.project.displayName,
        projectId: target.project.id, workbenchId: target.workbenchId,
      });
    } else if (target.section === 'workbenches') {
      this.tabs.open({ kind: 'workbenches', projectName: target.project.displayName });
    } else {
      this.tabs.open({
        kind: 'hub',
        projectName: target.project.displayName,
        section: target.section,
        ...(target.section === 'wiki' ? { wikiTarget: wikiTargetFromQuery(target.query) } : {}),
      });
    }
    this.appliedRevision.update(revision => revision + 1);
    return true;
  }

  private loadRegistry(): void {
    this.tasks.getRegistryWorkspaces({ includeArchived: false }).subscribe({
      next: workspaces => {
        this.projects.set((workspaces ?? []).flatMap(workspace => workspace.projects)
          .map(project => ({ id: project.id, displayName: project.displayName })));
        this.registryLoaded.set(true);
      },
      error: () => {
        // Preserve the URL. A later registry refresh can resolve it safely.
      },
    });
  }

  private publish(tab: StudioTab | null): void {
    const current = parseProjectHubRoute(window.location.hash, this.projects());
    if (tab?.kind === 'hub') {
      const project = this.projects().find(candidate => candidate.displayName === tab.projectName);
      if (!project) return;
      const section = isProjectRailKey(tab.section) ? tab.section : DEFAULT_PROJECT_RAIL_KEY;
      const sameDestination = current?.project.id === project.id && current.section === section;
      const query = section === 'wiki'
        ? wikiTargetQuery(tab.wikiTarget)
        : (sameDestination ? current.query : '');
      const sameTarget = sameDestination && current?.query === query;
      const next = withProjectHubRoute(window.location.hash, project.id, section, query);
      this.writeHash(next, sameTarget && current?.legacySlug ? 'replace' : 'push');
      return;
    }

    if (tab?.kind === 'workbench') {
      const project = this.projects().find(candidate => candidate.displayName === tab.projectName);
      if (!project) return;
      const route = projectDossierRoute(project.id, tab.workbenchId);
      this.writeHash(withRouteSegment(window.location.hash, route), 'push');
      return;
    }

    if (tab?.kind === 'workbenches' && tab.projectName) {
      const project = this.projects().find(candidate => candidate.displayName === tab.projectName);
      if (!project) return;
      this.writeHash(withRouteSegment(window.location.hash, projectDossierRoute(project.id, null)), 'push');
      return;
    }

    // Unknown future `/projects/...` shapes stay untouched for their owner.
    if (current && isProjectHubRoute(window.location.hash)) {
      this.writeHash(withRouteSegment(window.location.hash, null), 'push');
    }
  }

  private writeHash(hash: string, mode: 'push' | 'replace'): void {
    if (window.location.hash === hash) return;
    try {
      history[mode === 'push' ? 'pushState' : 'replaceState'](
        null,
        '',
        window.location.pathname + window.location.search + hash,
      );
    } catch {
      /* Browser history may be unavailable in embedded/test environments. */
    }
  }
}

function projectDossierRoute(projectId: string, workbenchId: string | null): string {
  const base = `/projects/${encodeURIComponent(projectId.trim().toUpperCase())}/workbenches`;
  return workbenchId ? `${base}/${encodeURIComponent(workbenchId)}` : base;
}

function wikiTargetFromQuery(query: string): WikiTabTarget {
  const params = new URLSearchParams(query.startsWith('?') ? query.slice(1) : query);
  const page = params.get('page')?.trim();
  if (page) return { kind: 'page', relPath: page };
  const folder = params.get('folder')?.trim();
  if (folder) return { kind: 'folder', relPath: folder };
  return { kind: 'overview' };
}

function wikiTargetQuery(target: WikiTabTarget | undefined): string {
  if (!target || target.kind === 'overview') return '';
  return `?${target.kind}=${encodeURIComponent(target.relPath)}`;
}
