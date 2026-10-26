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
});
