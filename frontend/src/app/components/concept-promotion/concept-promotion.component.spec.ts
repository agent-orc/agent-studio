import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { PromoteConceptResponse, PromoteConceptTasksResponse } from '../../models/task.model';
import { TaskService } from '../../services/task.service';
import { ConceptPromotionComponent } from './concept-promotion.component';
import { decisionLabel, promotionSlices } from './concept-promotion.util';

const PLAN: PromoteConceptResponse = {
  source: {
    repoRelativePath: 'docs/delivery-flow/index.html',
    title: 'Delivery flow',
    decisions: [
      { id: 'delivery-mode', label: 'Delivery mode', optionId: 'queued', optionLabel: 'A: queued',
        operatorSelected: false, isWorkingAssumption: true },
      { id: 'delivery-mode', label: 'Delivery mode', optionId: 'direct', optionLabel: 'B: direct',
        operatorSelected: false, isWorkingAssumption: false },
    ],
  },
  items: [
    { title: 'Implement delivery API', promptMarkdown: 'API', section: '§4', decisionId: 'delivery-mode' },
    { title: 'Implement delivery UI', promptMarkdown: 'UI' },
    { title: 'Pick the retry policy', promptMarkdown: 'Fork',
      decision: { question: 'Which retry policy?', options: [], decider: 'operator', status: 'pending' } },
  ],
  mode: 'coding',
  targetState: '1-preparation',
  watchPath: '/tasks',
  projectName: 'Demo',
  promoted: [{ jobId: 'ui-card', taskKey: 'DEM-12', title: 'Implement delivery UI', itemIndex: 1, state: '1-preparation' }],
};

describe('promotionSlices', () => {
  it('lists every slice in Dossier order with its working decision and existing card', () => {
    const slices = promotionSlices(PLAN);
    expect(slices.map(slice => [slice.order, slice.title])).toEqual([
      [1, 'Implement delivery API'], [2, 'Implement delivery UI'], [3, 'Pick the retry policy'],
    ]);
    expect(slices[0].decision).toBe('Delivery mode: A: queued (recommended)');
    expect(slices[0].section).toBe('§4');
    expect(slices[1].decision).toBe('No linked decision');
    expect(slices[1].card).toEqual({ key: 'DEM-12', lane: '1-preparation' });
    expect(slices[2].decision).toBe('Decision card: Which retry policy?');
  });

  it('names an operator choice and falls back to the raw id for an unknown decision', () => {
    expect(decisionLabel('mode', [{ id: 'mode', label: 'Mode', optionId: 'b', optionLabel: 'B',
      operatorSelected: true, isWorkingAssumption: true }])).toBe('Mode: B (operator choice)');
    expect(decisionLabel('missing', [])).toBe('missing');
  });
});

describe('ConceptPromotionComponent', () => {
  let fixture: ComponentFixture<ConceptPromotionComponent>;
  let service: {
    getPromoteConcept: ReturnType<typeof vi.fn>;
    promoteConcept: ReturnType<typeof vi.fn>;
    refresh: ReturnType<typeof vi.fn>;
  };
  const root = (): HTMLElement => fixture.nativeElement as HTMLElement;
  const byTestId = <T extends HTMLElement>(id: string): T | null =>
    root().querySelector<T>(`[data-testid="${id}"]`);
  const render = (): void => { fixture.detectChanges(); };

  beforeEach(async () => {
    service = {
      getPromoteConcept: vi.fn(() => of(PLAN)),
      promoteConcept: vi.fn(),
      refresh: vi.fn(),
    };
    await TestBed.configureTestingModule({
      imports: [ConceptPromotionComponent],
      providers: [provideZonelessChangeDetection(), { provide: TaskService, useValue: service }],
    }).compileComponents();
    fixture = TestBed.createComponent(ConceptPromotionComponent);
    fixture.componentRef.setInput('taskRef', 'DEM-7');
    fixture.componentRef.setInput('projectName', 'Demo');
    render();
  });

  it('opens the slice list and preselects every slice without a card', () => {
    byTestId<HTMLButtonElement>('concept-promotion-open')!.click();
    render();
    expect(service.getPromoteConcept).toHaveBeenCalledWith('DEM-7', undefined, 'Demo');
    expect(byTestId('concept-promotion-slice-1')?.textContent).toContain('Implement delivery API');
    expect(byTestId('concept-promotion-slice-1')?.textContent).toContain('Delivery mode: A: queued');
    expect(byTestId('concept-promotion-slice-2')?.textContent).toContain('Created DEM-12');
    expect(byTestId<HTMLInputElement>('concept-promotion-slice-check-2')!.disabled).toBe(true);
    expect(byTestId('concept-promotion-confirm')?.textContent).toContain('Create all 2 cards');
  });

  it('creates only the slices the owner keeps selected and reports the created cards', () => {
    const response = new Subject<PromoteConceptTasksResponse>();
    service.promoteConcept.mockReturnValue(response);
    const promoted = vi.fn();
    fixture.componentInstance.promoted.subscribe(promoted);
    byTestId<HTMLButtonElement>('concept-promotion-open')!.click();
    render();
    byTestId<HTMLInputElement>('concept-promotion-slice-check-3')!.click();
    render();
    expect(byTestId('concept-promotion-confirm')?.textContent).toContain('Create 1 card');

    byTestId<HTMLFormElement>('concept-promotion-panel')!.dispatchEvent(new Event('submit'));
    render();
    expect(service.promoteConcept).toHaveBeenCalledWith('DEM-7', [0], undefined, 'Demo');
    expect(byTestId('concept-promotion-confirm')?.textContent).toContain('Creating');
    expect(byTestId<HTMLButtonElement>('concept-promotion-confirm')!.disabled).toBe(true);

    const created = [{ jobId: 'api-card', taskKey: 'DEM-13', title: 'Implement delivery API',
      itemIndex: 0, state: '1-preparation' }];
    response.next({ source: PLAN.source, created });
    response.complete();
    render();
    expect(byTestId('concept-promotion-created')?.textContent).toContain('DEM-13');
    expect(byTestId('concept-promotion-slice-1')?.textContent).toContain('Created DEM-13');
    expect(service.refresh).toHaveBeenCalledWith(true);
    expect(promoted).toHaveBeenCalledWith(created);
    expect(byTestId<HTMLButtonElement>('concept-promotion-confirm')!.disabled).toBe(true);
  });

  it('keeps the slice list and shows the server error when creation is refused', () => {
    service.promoteConcept.mockReturnValue(throwError(() => ({
      error: { error: 'Complete sight review before promoting implementation cards.' },
    })));
    byTestId<HTMLButtonElement>('concept-promotion-open')!.click();
    render();
    byTestId<HTMLFormElement>('concept-promotion-panel')!.dispatchEvent(new Event('submit'));
    render();
    expect(byTestId('concept-promotion-error')?.textContent).toContain('Complete sight review');
    expect(byTestId('concept-promotion-slice-1')).not.toBeNull();
  });

  it('explains a Dossier that cannot be promoted', () => {
    service.getPromoteConcept.mockReturnValue(throwError(() => ({
      error: { error: 'The concept Dossier is missing or did not pass concept review.' },
    })));
    byTestId<HTMLButtonElement>('concept-promotion-open')!.click();
    render();
    expect(byTestId('concept-promotion-load-error')?.textContent).toContain('did not pass concept review');
    expect(byTestId<HTMLButtonElement>('concept-promotion-confirm')!.disabled).toBe(true);
  });
});
