import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TokenAnalysisCardTableComponent } from './token-analysis-card-table';
import { fixtureResponse } from '../../testing/token-analysis.fixtures';
import * as util from '../../token-analysis.util';

describe('TokenAnalysisCardTableComponent', () => {
  it('renders its data', async () => {
    await TestBed.configureTestingModule({
      imports: [TokenAnalysisCardTableComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(TokenAnalysisCardTableComponent);
    const data = fixtureResponse();
    const cards = util.filterCards(data.cards, util.EMPTY_FILTERS);
    fixture.componentRef.setInput('cards', cards);
    fixture.componentRef.setInput('remainders', data.remainders);
    fixture.componentRef.setInput('kinds', util.activeKinds([...cards, ...data.remainders]));
    fixture.detectChanges();
    await fixture.whenStable();
    const root: HTMLElement = fixture.nativeElement;
    expect(root.querySelector('[data-testid="token-analysis-row-AGT-1"]')?.textContent).toContain('GPT-5.6 Sol +1');
    expect(root.querySelector('[data-testid="token-analysis-row-rest"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="token-analysis-table-total"]')?.textContent).toContain('2.6K');
  });

  it('sorts by cost by default, shows usage kinds in USD, and switches to tokens from the header (AGT-3805)', async () => {
    await TestBed.configureTestingModule({
      imports: [TokenAnalysisCardTableComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(TokenAnalysisCardTableComponent);
    const data = fixtureResponse();
    const cards = util.filterCards(data.cards, util.EMPTY_FILTERS);
    fixture.componentRef.setInput('cards', cards);
    fixture.componentRef.setInput('kinds', util.activeKinds(cards));
    let sort = '';
    fixture.componentInstance.sortChange.subscribe(next => (sort = next));
    fixture.detectChanges();
    await fixture.whenStable();
    const root: HTMLElement = fixture.nativeElement;

    const head = [...root.querySelectorAll('thead th')].map(th => th.textContent?.trim());
    expect(head.slice(0, 5)).toEqual(['Card', 'Outcome', 'Rounds', 'Tokens', 'Cost']);
    const cost = root.querySelector('[data-testid="token-analysis-sort-cost"]')!.closest('th')!;
    expect(cost.getAttribute('aria-sort')).toBe('descending');
    expect(root.querySelector('[data-testid="token-analysis-table-measure"]')?.textContent).toContain('usage kinds in USD');
    const kindCells = [...root.querySelectorAll('[data-testid="token-analysis-row-AGT-1"] td.tt__kind')].map(td => td.textContent?.trim());
    expect(kindCells).toEqual(['$0.500', '$0.250']);
    expect(root.querySelector('[data-testid="token-analysis-table-cost"]')?.textContent).toContain('$0.850');

    (root.querySelector('[data-testid="token-analysis-sort-tokens"]') as HTMLButtonElement).click();
    expect(sort).toBe('tokens');
    fixture.componentRef.setInput('measure', 'tokens');
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('[data-testid="token-analysis-sort-tokens"]')!.closest('th')!.getAttribute('aria-sort')).toBe('descending');
    expect([...root.querySelectorAll('[data-testid="token-analysis-row-AGT-1"] td.tt__kind')].map(td => td.textContent?.trim()))
      .toEqual(['1.6K', '400']);
  });
});
