import { Injectable, inject } from '@angular/core';
import { dossierRoute, type DossierReference } from './dossier-reference.util';
// Both imports use their concrete module paths rather than the feature barrels.
// A barrel here would pull the heavy host components (project shell, studio
// shell) into a root-level service that those very components render, closing
// the module cycle that left a component def undefined at evaluation time
// (Angular NG0919). See TaskReferenceNavigationService for the same seam.
// eslint-disable-next-line no-restricted-imports
import { StudioTabStateService } from '../features/studio-shell/services/studio-tab-state.service';
// eslint-disable-next-line no-restricted-imports
import { toProjectSlug } from '../features/project-detail/components/project-shell/project-shell.config';

/**
 * Where a Dossier chip goes. The primary action is the Dossier view, the same
 * route the Dossier list opens; the raw entry-point file stays reachable as the
 * secondary "open source" action in the project Wiki reader.
 */
@Injectable({ providedIn: 'root' })
export class DossierReferenceNavigationService {
  private readonly tabs = inject(StudioTabStateService);

  /** Canonical hash route, so the chip is a real link for hover and middle-click. */
  routeFor(reference: DossierReference): string {
    return dossierRoute(toProjectSlug(reference.projectName), reference.id);
  }

  openDossier(reference: DossierReference): void {
    this.tabs.open({
      kind: 'workbench',
      projectName: reference.projectName,
      workbenchId: reference.id,
      title: reference.title,
      ...(reference.key ? { key: reference.key } : {}),
    });
  }

  /** Secondary action: the entry-point file in the project's Wiki reader. */
  openSource(reference: DossierReference): void {
    const page = reference.entryPath.trim().replace(/^docs\//i, '');
    if (!page) return;
    this.tabs.open({
      kind: 'hub',
      projectName: reference.projectName,
      section: 'wiki',
      wikiTarget: { kind: 'page', relPath: page },
    }, 'new');
  }
}
