import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TokenAnalysisScatterComponent } from './token-analysis-scatter';
import { fixtureResponse } from '../../testing/token-analysis.fixtures';
import * as util from '../../token-analysis.util';

describe('TokenAnalysisScatterComponent', () => {
  it('renders its data', async () => {
    await TestBed.configureTestingModule({
      imports: [TokenAnalysisScatterComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(TokenAnalysisScatterComponent);
    const data = fixtureResponse();
    const cards = util.filterCards(data.cards, util.EMPTY_FILTERS);
    fixture.componentRef.setInput('points', util.scatterPoints(cards));
    fixture.detectChanges();
    await fixture.whenStable();
    const root: HTMLElement = fixture.nativeElement;
    expect(root.querySelectorAll('.sc__area .sc__mark').length).toBe(2);
    expect(root.querySelector('.sc__area .sc__mark--done')?.getAttribute('aria-label')).toContain('3 rounds');
  });
});
