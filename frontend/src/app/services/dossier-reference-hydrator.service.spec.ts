import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { of } from 'rxjs';
import type { WorkbenchOverviewItem } from '../models/project-docs.model';
import { DossierCatalogueService } from './dossier-catalogue.service';
import { DossierReferenceHydratorService } from './dossier-reference-hydrator.service';
import { DossierReferenceNavigationService } from './dossier-reference-navigation.service';
import { buildDossierIndex } from './dossier-reference.util';

const catalogue: WorkbenchOverviewItem[] = [{
  projectName: 'Agent Studio',
  workbench: {
    id: 'decision-cards', key: 'AGT-W54', title: 'Decision cards', summary: '',
    status: 'decision-pending', phase: 'decision-ready', updatedAtUtc: '2026-09-01T00:00:00Z',
    entryPath: 'docs/operations/decision-cards/index.html', valid: true, error: null,
    sourceTaskKeys: [],
  },
}];

let host: HTMLElement;

function hydrate(html: string): HTMLElement {
  host.innerHTML = html;
  TestBed.inject(DossierReferenceHydratorService).refresh();
  return host;
}

beforeEach(() => {
  const index = buildDossierIndex(catalogue);
  TestBed.configureTestingModule({
    providers: [
      { provide: DossierCatalogueService,
        useValue: { ensureLoaded: () => of(index), index: () => index } },
      { provide: DossierReferenceNavigationService,
        useValue: { routeFor: () => '#/projects/AGENT-STUDIO/workbenches/decision-cards',
          openDossier: vi.fn(), openSource: vi.fn() } },
    ],
  });
  host = document.createElement('div');
  document.body.append(host);
});

afterEach(() => host.remove());

describe('DossierReferenceHydratorService', () => {
  it('turns a key in prose into a chip that opens the Dossier view', () => {
    const root = hydrate('<cac-markdown><p>Decided in AGT-W54 last week.</p></cac-markdown>');
    const chip = root.querySelector('app-dossier-reference-chip');

    expect(chip?.textContent).toContain('AGT-W54');
    expect(chip?.textContent).toContain('Decision cards');
    expect(chip?.querySelector('a')?.getAttribute('href'))
      .toBe('#/projects/AGENT-STUDIO/workbenches/decision-cards');
    expect(root.textContent).toContain('Decided in ');
    expect(root.textContent).toContain(' last week.');
  });

  it('replaces a plain entry-point file link with the chip', () => {
    const root = hydrate('<cac-markdown><p>See <a href="docs/operations/decision-cards/index.html">'
      + 'docs/operations/decision-cards/index.html</a>.</p></cac-markdown>');

    expect(root.querySelectorAll('app-dossier-reference-chip')).toHaveLength(1);
    expect(root.querySelector('a[href$="index.html"]')).toBeNull();
  });

  it('recognises the folder path and the workbench.json descriptor in inline code', () => {
    const root = hydrate('<cac-markdown><p><code>docs/operations/decision-cards/</code> and '
      + '<code>docs/operations/decision-cards/workbench.json</code></p></cac-markdown>');

    expect(root.querySelectorAll('app-dossier-reference-chip')).toHaveLength(2);
  });

  it('leaves unknown paths, code samples, and non-markdown regions as text', () => {
    const root = hydrate(`
      <cac-markdown>
        <p><code>docs/operations/unknown-thing/</code> and <code>workbench.json</code></p>
        <pre><code>docs/operations/decision-cards/index.html</code></pre>
      </cac-markdown>
      <div><p>AGT-W54 outside any rendered document</p></div>`);

    expect(root.querySelectorAll('app-dossier-reference-chip')).toHaveLength(0);
    expect(root.querySelector('pre')?.textContent)
      .toBe('docs/operations/decision-cards/index.html');
  });

  it('hydrates an opted-in scope and reads its project as the path hint', () => {
    const root = hydrate('<article data-dossier-reference-scope="Agent Studio">'
      + '<p>Result: docs/operations/decision-cards/ is decided.</p></article>');

    expect(root.querySelector('app-dossier-reference-chip')?.textContent).toContain('AGT-W54');
  });

  it('is idempotent: a second pass does not nest or duplicate chips', () => {
    const root = hydrate('<cac-markdown><p>AGT-W54</p></cac-markdown>');
    TestBed.inject(DossierReferenceHydratorService).refresh();

    expect(root.querySelectorAll('app-dossier-reference-chip')).toHaveLength(1);
  });
});
