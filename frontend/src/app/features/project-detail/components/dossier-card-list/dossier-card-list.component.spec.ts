import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { DossierCardList } from '../../../../models/project-docs.model';
import { TaskReferenceNavigationService } from '../../../../services/task-reference-navigation.service';
import { DossierCardListComponent } from './dossier-card-list.component';

const LIST: DossierCardList = {
  projectName: 'Demo', dossierKey: 'DEM-W1', dossierPath: 'docs/operations/one/index.html',
  implementationSlices: 2, completedSlices: 1,
  cards: [{ key: 'DEM-1', id: 'one', title: 'Ship API', lane: '6-completed',
    state: '6-completed', environments: ['staging'], section: '§4',
    decision: 'NV-D2', implementationIndex: 0 }],
};

describe('DossierCardListComponent', () => {
  let fixture: ComponentFixture<DossierCardListComponent>;
  let openReferenceOrNotify: ReturnType<typeof vi.fn>;
  const root = (): HTMLElement => fixture.nativeElement as HTMLElement;
  const toggle = (): HTMLButtonElement | null => root().querySelector('[data-testid="dossier-cards-toggle"]');
  const show = (list: DossierCardList): void => { fixture.componentRef.setInput('list', list); fixture.detectChanges(); };

  beforeEach(async () => {
    openReferenceOrNotify = vi.fn();
    await TestBed.configureTestingModule({
      imports: [DossierCardListComponent],
      providers: [provideZonelessChangeDetection(),
        { provide: TaskReferenceNavigationService, useValue: { openReferenceOrNotify } }],
    }).compileComponents();
    fixture = TestBed.createComponent(DossierCardListComponent);
  });

  it('starts collapsed with the slice progress visible', () => {
    show(LIST);
    expect(root().querySelector('[data-testid="dossier-slice-progress"]')?.textContent).toContain('1 / 2');
    expect(toggle()?.getAttribute('aria-expanded')).toBe('false');
    expect(toggle()?.firstElementChild?.tagName.toLowerCase()).toBe('app-disclosure-marker');
    expect(root().querySelector('ul')).toBeNull();
  });

  it('expands and collapses in place and opens a card', () => {
    show(LIST);
    toggle()!.click();
    fixture.detectChanges();
    expect(toggle()?.getAttribute('aria-expanded')).toBe('true');
    expect(toggle()?.getAttribute('aria-controls')).toBe(root().querySelector('ul')?.id);
    expect(root().textContent).toContain('Environments: staging');
    expect(root().textContent).toContain('Section: §4');
    expect(root().textContent).toContain('Decision: NV-D2');
    root().querySelector<HTMLButtonElement>('[data-testid="dossier-card-DEM-1"]')?.click();
    expect(openReferenceOrNotify).toHaveBeenCalledWith('DEM-1');
    toggle()!.click();
    fixture.detectChanges();
    expect(toggle()?.getAttribute('aria-expanded')).toBe('false');
    expect(root().querySelector('ul')).toBeNull();
  });

  it('stays open on a refresh of the same Dossier and collapses for a different one', () => {
    show(LIST);
    toggle()!.click();
    fixture.detectChanges();
    show({ ...LIST, completedSlices: 2, cards: [...LIST.cards] });
    expect(toggle()?.getAttribute('aria-expanded')).toBe('true');
    show({ ...LIST, dossierKey: 'DEM-W2' });
    expect(toggle()?.getAttribute('aria-expanded')).toBe('false');
    toggle()!.click();
    fixture.detectChanges();
    show({ ...LIST, dossierKey: 'DEM-W2', dossierPath: 'docs/operations/two/index.html' });
    expect(toggle()?.getAttribute('aria-expanded')).toBe('false');
  });

  it('renders a zero-card row without a button', () => {
    show({ ...LIST, cards: [] });
    expect(toggle()).toBeNull();
    expect(root().querySelector('button')).toBeNull();
    expect(root().querySelector('app-disclosure-marker')).toBeNull();
    expect(root().textContent).toContain('Cards');
    expect(root().textContent).toContain('No cards are assigned to this Dossier yet.');
  });
});
