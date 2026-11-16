import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TokenAnalysisKindCostComponent } from './token-analysis-kind-cost';
import { fixtureResponse } from '../../testing/token-analysis.fixtures';
import * as util from '../../token-analysis.util';

describe('TokenAnalysisKindCostComponent', () => {
  async function render(breakdown: util.KindCostBreakdown, days = 7) {
    await TestBed.configureTestingModule({
      imports: [TokenAnalysisKindCostComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(TokenAnalysisKindCostComponent);
    fixture.componentRef.setInput('breakdown', breakdown);
    fixture.componentRef.setInput('days', days);
    fixture.detectChanges();
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  it('lists cost per usage kind for the period, most expensive first, summing to the total', async () => {
    const data = fixtureResponse();
    const root = await render(util.kindCostBreakdown(util.filterCards(data.cards, util.EMPTY_FILTERS), data.remainders));
    expect(root.textContent).toContain('Last 7 days');
    const rows = [...root.querySelectorAll('tbody tr')].map(tr => tr.getAttribute('data-testid'));
    expect(rows).toEqual(['token-analysis-kind-cost-coding', 'token-analysis-kind-cost-review-aspect', 'token-analysis-kind-cost-chat']);
    expect(root.querySelector('[data-testid="token-analysis-kind-cost-coding"]')?.textContent).toContain('$0.600');
    expect(root.querySelector('[data-testid="token-analysis-kind-cost-total"]')?.textContent).toContain('$0.860');
    // One project: no per-project columns.
    expect(root.querySelector('.kc__project')).toBeNull();
  });

  it('adds one cost column per project in workspace scope', async () => {
    const data = fixtureResponse();
    const cards = [...data.cards, { ...data.cards[1], project: 'Other', jobId: 'job-9' }];
    const root = await render(util.kindCostBreakdown(cards, data.remainders), 30);
    expect(root.querySelector('[data-testid="token-analysis-kind-cost-project-Studio"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="token-analysis-kind-cost-project-Other"]')).not.toBeNull();
    const coding = [...root.querySelectorAll('[data-testid="token-analysis-kind-cost-coding"] td')].map(td => td.textContent?.trim());
    expect(coding.slice(-2)).toEqual(['$0.600', '$0.100']);
  });
});
