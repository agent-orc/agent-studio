import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TokenAnalysisKindBarsComponent } from './token-analysis-kind-bars';
import { fixtureResponse } from '../../testing/token-analysis.fixtures';
import * as util from '../../token-analysis.util';

describe('TokenAnalysisKindBarsComponent', () => {
  it('renders its data', async () => {
    await TestBed.configureTestingModule({
      imports: [TokenAnalysisKindBarsComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(TokenAnalysisKindBarsComponent);
    const data = fixtureResponse();
    const cards = util.filterCards(data.cards, util.EMPTY_FILTERS);
    fixture.componentRef.setInput('rows', util.topCardStacks(cards, 5));
    fixture.componentRef.setInput('kinds', util.activeKinds(cards));
    let picked = '';
    fixture.componentInstance.cardSelect.subscribe(id => (picked = id));
    fixture.detectChanges();
    await fixture.whenStable();
    const root: HTMLElement = fixture.nativeElement;
    expect(root.querySelectorAll('.kb__row').length).toBe(2);
    (root.querySelector('[data-testid="token-analysis-kind-row-AGT-1"]') as HTMLButtonElement).click();
    expect(picked).toBe('Studio/job-1');
  });
});
