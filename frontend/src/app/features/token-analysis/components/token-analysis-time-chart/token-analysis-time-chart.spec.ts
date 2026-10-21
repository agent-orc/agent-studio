import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TokenAnalysisTimeChartComponent } from './token-analysis-time-chart';
import { fixtureResponse } from '../../testing/token-analysis.fixtures';
import * as util from '../../token-analysis.util';

describe('TokenAnalysisTimeChartComponent', () => {
  it('renders its data', async () => {
    await TestBed.configureTestingModule({
      imports: [TokenAnalysisTimeChartComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(TokenAnalysisTimeChartComponent);
    const data = fixtureResponse();
    const cards = util.filterCards(data.cards, util.EMPTY_FILTERS);
    fixture.componentRef.setInput('chart', util.timeChart(cards, data.from, data.to));
    fixture.detectChanges();
    await fixture.whenStable();
    const root: HTMLElement = fixture.nativeElement;
    expect(root.querySelectorAll('.tc__column').length).toBe(3);
    expect(root.querySelector('[data-day="2026-10-21"]')?.getAttribute('data-incomplete')).toBe('true');
    expect(root.querySelector('[data-testid="token-analysis-incomplete-legend"]')).not.toBeNull();
  });
});
