import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import { DossierReferenceNavigationService } from '../../services/dossier-reference-navigation.service';
import type { DossierReference } from '../../services/dossier-reference.util';
import { DossierReferenceChipComponent } from './dossier-reference-chip';

const reference: DossierReference = {
  id: 'decision-cards',
  key: 'AGT-W54',
  title: 'Decision cards',
  status: 'decision-pending',
  phase: 'decision-ready',
  pattern: 'concept',
  valid: true,
  projectName: 'Agent Studio',
  entryPath: 'docs/operations/decision-cards/index.html',
};

async function render(navigation: Partial<DossierReferenceNavigationService>, inputs: Record<string, unknown> = {}) {
  await TestBed.configureTestingModule({
    imports: [DossierReferenceChipComponent],
    providers: [{
      provide: DossierReferenceNavigationService,
      useValue: {
        routeFor: () => '#/projects/AGENT-STUDIO/workbenches/decision-cards',
        openDossier: vi.fn(),
        openSource: vi.fn(),
        ...navigation,
      },
    }],
  }).compileComponents();
  const fixture = TestBed.createComponent(DossierReferenceChipComponent);
  fixture.componentRef.setInput('reference', reference);
  for (const [name, value] of Object.entries(inputs)) fixture.componentRef.setInput(name, value);
  fixture.detectChanges();
  return fixture;
}

describe('DossierReferenceChipComponent', () => {
  it('renders key, title and status on one line and links into the Dossier view', async () => {
    const openDossier = vi.fn();
    const fixture = await render({ openDossier });
    const link = fixture.nativeElement.querySelector('a') as HTMLAnchorElement;

    expect(link.textContent).toContain('AGT-W54');
    expect(link.textContent).toContain('Decision cards');
    expect(link.textContent).toContain('Decision pending');
    expect(link.getAttribute('href')).toBe('#/projects/AGENT-STUDIO/workbenches/decision-cards');
    expect(link.getAttribute('aria-label')).toBe('Open Dossier AGT-W54: Decision cards');

    link.click();
    expect(openDossier).toHaveBeenCalledWith(reference);
  });

  it('keeps the entry-point file as a secondary "open source" action', async () => {
    const openSource = vi.fn();
    const openDossier = vi.fn();
    const fixture = await render({ openSource, openDossier });
    const source = fixture.nativeElement.querySelector(
      '[data-testid="dossier-reference-chip-source"]') as HTMLButtonElement;

    expect(source.getAttribute('aria-label'))
      .toBe('Open source docs/operations/decision-cards/index.html');
    source.click();
    expect(openSource).toHaveBeenCalledWith(reference);
    expect(openDossier).not.toHaveBeenCalled();
  });

  it('renders a presentation-only chip with no nested link when not interactive', async () => {
    const fixture = await render({}, { interactive: false, testId: 'search-dossier-chip' });

    expect(fixture.nativeElement.querySelector('a')).toBeNull();
    expect(fixture.nativeElement.querySelector('button')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="search-dossier-chip"]').textContent)
      .toContain('AGT-W54');
  });

  it('falls back to the folder id and names an unknown state plainly', async () => {
    const fixture = await render({});
    fixture.componentRef.setInput('reference',
      { ...reference, key: null, status: 'active', phase: null });
    fixture.detectChanges();

    expect(fixture.componentInstance.label()).toBe('decision-cards');
    expect(fixture.componentInstance.statusLabel()).toBe('Active');
  });
});
