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
});
