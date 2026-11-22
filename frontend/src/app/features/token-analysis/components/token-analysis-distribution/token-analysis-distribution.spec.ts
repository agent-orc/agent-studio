import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TokenAnalysisDistributionComponent } from './token-analysis-distribution';
import { fixtureResponse } from '../../testing/token-analysis.fixtures';
import * as util from '../../token-analysis.util';

describe('TokenAnalysisDistributionComponent', () => {
  it('renders its data', async () => {
    await TestBed.configureTestingModule({
      imports: [TokenAnalysisDistributionComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(TokenAnalysisDistributionComponent);
    const data = fixtureResponse();
    const cards = util.filterCards(data.cards, util.EMPTY_FILTERS);
    fixture.componentRef.setInput('levels', util.distribution(cards, 'level'));
    fixture.componentRef.setInput('models', util.distribution(cards, 'model'));
    fixture.componentRef.setInput('mix', util.tokenMix(util.allSlices(cards)));
    fixture.detectChanges();
    await fixture.whenStable();
    const root: HTMLElement = fixture.nativeElement;
    expect(root.querySelector('[data-testid="token-analysis-distribution-level"]')?.textContent).toContain('high');
    expect(root.querySelector('[data-testid="token-analysis-mix-reasoning"]')?.textContent).toContain('40');
  });

  it('shows the priced subtotal for a mixed level and no price data for an unpriced model', async () => {
    await TestBed.configureTestingModule({
      imports: [TokenAnalysisDistributionComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(TokenAnalysisDistributionComponent);
    const data = fixtureResponse();
    const unpriced = data.cards[0].slices[1];
    unpriced.model = 'demo-unpriced-model';
    unpriced.level = 'high';
    unpriced.costUsd = 0;
    unpriced.priced = false;
    const cards = util.filterCards(data.cards, util.EMPTY_FILTERS);
    fixture.componentRef.setInput('levels', util.distribution(cards, 'level'));
    fixture.componentRef.setInput('models', util.distribution(cards, 'model'));
    fixture.componentRef.setInput('mix', util.tokenMix(util.allSlices(cards)));
    fixture.detectChanges();
    await fixture.whenStable();

    const root: HTMLElement = fixture.nativeElement;
    const levelRow = [...root.querySelectorAll('[data-testid="token-analysis-distribution-level"] tbody tr')]
      .find(row => row.querySelector('th')?.textContent?.trim() === 'high')!;
    expect(levelRow.querySelector('td:last-child')?.textContent).toContain('$0.600');
    expect(levelRow.querySelector('td:last-child a')?.textContent).toBe('*');
    expect(levelRow.querySelector('td:last-child a')?.getAttribute('href')).toBe('#/workspace/settings/tokens');
    const modelRow = [...root.querySelectorAll('[data-testid="token-analysis-distribution-model"] tbody tr')]
      .find(row => row.querySelector('th')?.textContent?.trim() === 'demo-unpriced-model')!;
    expect(modelRow.querySelector('td:last-child a')?.textContent).toBe('no price data');
    expect(modelRow.querySelector('td:last-child a')?.getAttribute('href')).toBe('#/workspace/settings/tokens');
  });
});
