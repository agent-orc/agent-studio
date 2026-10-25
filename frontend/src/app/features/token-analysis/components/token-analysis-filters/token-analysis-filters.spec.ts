import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TokenAnalysisFiltersComponent } from './token-analysis-filters';
import { fixtureResponse } from '../../testing/token-analysis.fixtures';
import * as util from '../../token-analysis.util';

describe('TokenAnalysisFiltersComponent', () => {
  it('renders its data', async () => {
    await TestBed.configureTestingModule({
      imports: [TokenAnalysisFiltersComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(TokenAnalysisFiltersComponent);
    const data = fixtureResponse();
    fixture.componentRef.setInput('filters', { ...util.EMPTY_FILTERS, model: 'Gone model' });
    fixture.componentRef.setInput('options', util.filterOptions(data.cards));
    let next: util.TokenAnalysisFilters | null = null;
    fixture.componentInstance.filtersChange.subscribe(value => (next = value));
    fixture.detectChanges();
    await fixture.whenStable();
    const root: HTMLElement = fixture.nativeElement;
    const model = root.querySelector('[data-testid="token-analysis-filter-model"]') as HTMLSelectElement;
    expect(model.value).toBe('Gone model');
    const kind = root.querySelector('[data-testid="token-analysis-filter-kind"]') as HTMLSelectElement;
    kind.value = 'review-aspect';
    kind.dispatchEvent(new Event('change'));
    expect(next!.kind).toBe('review-aspect');
    (root.querySelector('[data-testid="token-analysis-filter-reset"]') as HTMLButtonElement).click();
    expect(next!).toEqual(util.EMPTY_FILTERS);
  });
});
