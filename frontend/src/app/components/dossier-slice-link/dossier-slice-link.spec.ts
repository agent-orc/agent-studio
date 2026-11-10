import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import type { TaskDossier } from '../../models/task.model';
import { DossierCatalogueService } from '../../services/dossier-catalogue.service';
import { DossierReferenceNavigationService } from '../../services/dossier-reference-navigation.service';
import { EMPTY_DOSSIER_INDEX, buildDossierIndex, type DossierReference } from '../../services/dossier-reference.util';
import { DossierSliceLinkComponent } from './dossier-slice-link';

const reference: DossierReference = {
  id: 'sliced-flow',
  key: 'NOTE-W4',
  title: 'Sliced flow',
  status: 'decided',
  phase: null,
  pattern: 'concept',
  valid: true,
  projectName: 'Notes',
  entryPath: 'docs/sliced-flow/index.html',
};

async function render(dossier: TaskDossier, references: DossierReference[] = [reference]) {
  const openDossier = vi.fn();
  const index = signal(references.length ? buildDossierIndex(references) : EMPTY_DOSSIER_INDEX);
  await TestBed.configureTestingModule({
    imports: [DossierSliceLinkComponent],
    providers: [
      { provide: DossierCatalogueService, useValue: { index, ensureLoaded: () => of(index()) } },
      {
        provide: DossierReferenceNavigationService,
        useValue: { routeFor: () => '#/projects/NOTES/workbenches/sliced-flow', openDossier },
      },
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(DossierSliceLinkComponent);
  fixture.componentRef.setInput('dossier', dossier);
  fixture.componentRef.setInput('projectName', 'Notes');
  fixture.detectChanges();
  return { fixture, openDossier };
}

describe('DossierSliceLinkComponent', () => {
  it('names the slice position and the Dossier key in one link to the Dossier view', async () => {
    const { fixture, openDossier } = await render({
      path: 'docs/sliced-flow/index.html', implementationIndex: 1, implementationCount: 3,
    });
    const link = fixture.nativeElement.querySelector('a') as HTMLAnchorElement;

    expect(link.textContent?.trim()).toBe('Slice 2 of 3 · Dossier NOTE-W4');
    expect(link.getAttribute('href')).toBe('#/projects/NOTES/workbenches/sliced-flow');

    const parentClick = vi.fn();
    fixture.nativeElement.addEventListener('click', parentClick);
    link.click();
    expect(openDossier).toHaveBeenCalledWith(reference);
    expect(parentClick).not.toHaveBeenCalled();
  });

  it('omits the count for a card cut before the slice count was recorded', async () => {
    const { fixture } = await render({ path: 'docs/sliced-flow/index.html', implementationIndex: 0 });
    expect(fixture.nativeElement.textContent.trim()).toBe('Slice 1 · Dossier NOTE-W4');
  });

  it('names the Dossier folder as plain text until the catalogue resolves the path', async () => {
    const { fixture } = await render(
      { path: 'docs/sliced-flow/index.html', implementationIndex: 2, implementationCount: 3 }, []);

    expect(fixture.nativeElement.querySelector('a')).toBeNull();
    expect(fixture.nativeElement.textContent.trim()).toBe('Slice 3 of 3 · Dossier sliced-flow');
  });
});
