import { of } from 'rxjs';
import type { Provider } from '@angular/core';
import type { WorkbenchOverviewItem } from '../app/models/project-docs.model';
import { DossierCatalogueService } from '../app/services/dossier-catalogue.service';
import { DossierReferenceNavigationService } from '../app/services/dossier-reference-navigation.service';
import { buildDossierIndex, dossierRoute, type DossierReference } from '../app/services/dossier-reference.util';

/**
 * Shared Dossier-reference test seam (AGT-2812).
 *
 * Every surface that renders documents embeds the same hydrator, so each
 * surface spec seeds the same one-entry catalogue and asks for one immediate
 * hydration pass instead of reimplementing the stub.
 */
export const DOSSIER_FIXTURE_KEY = 'AGT-W54';
export const DOSSIER_FIXTURE_PATH = 'docs/operations/decision-cards/index.html';

export const DOSSIER_FIXTURE: WorkbenchOverviewItem = {
  projectName: 'Demo',
  workbench: {
    id: 'decision-cards',
    key: DOSSIER_FIXTURE_KEY,
    title: 'Decision cards',
    summary: '',
    status: 'decision-pending',
    phase: 'decision-ready',
    updatedAtUtc: '2026-09-01T00:00:00Z',
    entryPath: DOSSIER_FIXTURE_PATH,
    valid: true,
    error: null,
    sourceTaskKeys: [],
  },
};

/**
 * Stubs the catalogue read and the tab-opening navigation so a surface spec
 * needs neither a live workbenches call nor the Studio shell.
 */
export function provideDossierCatalogueStub(
  items: readonly WorkbenchOverviewItem[] = [DOSSIER_FIXTURE],
): Provider[] {
  const index = buildDossierIndex(items);
  const opened: DossierReference[] = [];
  return [
    {
      provide: DossierCatalogueService,
      useValue: { ensureLoaded: () => of(index), index: () => index, reload: () => of(index) },
    },
    {
      provide: DossierReferenceNavigationService,
      useValue: {
        opened,
        routeFor: (reference: DossierReference) =>
          dossierRoute(reference.projectName, reference.id),
        openDossier: (reference: DossierReference) => opened.push(reference),
        openSource: (reference: DossierReference) => opened.push(reference),
      },
    },
  ];
}

export function dossierChipsIn(root: ParentNode): HTMLElement[] {
  return Array.from(root.querySelectorAll<HTMLElement>('app-dossier-reference-chip'));
}
